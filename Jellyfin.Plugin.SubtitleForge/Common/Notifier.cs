using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SubtitleForge.Configuration;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleForge.Common;

/// <summary>
/// Collects what SubtitleForge created and reports a summary after a run: in Jellyfin's activity log
/// (Dashboard → Aktivität) and optionally to a webhook (ntfy, Gotify, Discord or any JSON endpoint).
/// </summary>
public class Notifier
{
    private const int MaxLines = 25;
    private readonly IActivityManager _activity;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<Notifier> _logger;
    private readonly ConcurrentQueue<string> _lines = new();
    private int _count;

    /// <summary>
    /// Initializes a new instance of the <see cref="Notifier"/> class.
    /// </summary>
    /// <param name="activity">Activity manager.</param>
    /// <param name="http">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public Notifier(IActivityManager activity, IHttpClientFactory http, ILogger<Notifier> logger)
    {
        _activity = activity;
        _http = http;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Remembers one created subtitle.
    /// </summary>
    /// <param name="line">Description.</param>
    public void Record(string line)
    {
        Interlocked.Increment(ref _count);
        if (_lines.Count < MaxLines)
        {
            _lines.Enqueue(line);
        }
    }

    /// <summary>
    /// Sends the summary of everything recorded since the last flush (nothing is sent when nothing happened).
    /// </summary>
    /// <param name="run">Name of the run (e.g. "OCR", "Download").</param>
    /// <param name="extra">Additional summary text.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Task.</returns>
    public async Task FlushAsync(string run, string? extra, CancellationToken cancellationToken)
    {
        var count = Interlocked.Exchange(ref _count, 0);
        var lines = new List<string>();
        while (_lines.TryDequeue(out var l))
        {
            lines.Add(l);
        }

        if (count == 0 && string.IsNullOrEmpty(extra))
        {
            return;
        }

        var title = Loc.German
            ? FormattableString.Invariant($"SubtitleForge {run}: {count} neue Untertitel")
            : FormattableString.Invariant($"SubtitleForge {run}: {count} new subtitles");
        var body = new StringBuilder();
        if (!string.IsNullOrEmpty(extra))
        {
            body.AppendLine(extra);
        }

        foreach (var l in lines)
        {
            body.Append("• ").AppendLine(l);
        }

        if (count > lines.Count)
        {
            body.AppendLine(Loc.German
                ? FormattableString.Invariant($"… und {count - lines.Count} weitere")
                : FormattableString.Invariant($"… and {count - lines.Count} more"));
        }

        await DeliverAsync(title, extra ?? string.Join(", ", lines.Take(3)), body.ToString(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a message to the configured targets (activity log, webhook).
    /// </summary>
    /// <param name="title">Title.</param>
    /// <param name="shortOverview">One line.</param>
    /// <param name="body">Text.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Task.</returns>
    public async Task DeliverAsync(string title, string shortOverview, string body, CancellationToken cancellationToken)
    {
        var config = Config;
        if (config.NotifyActivityLog)
        {
            try
            {
                await _activity.CreateAsync(new ActivityLog(title, "SubtitleForge", Guid.Empty)
                {
                    ShortOverview = shortOverview,
                    Overview = body,
                    LogSeverity = LogLevel.Information,
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SubtitleForge: could not write activity log entry");
            }
        }

        if (!string.IsNullOrWhiteSpace(config.NotifyWebhookUrl))
        {
            await SendWebhookAsync(config.NotifyWebhookUrl.Trim(), title, body, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends a test message to the configured targets.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Task.</returns>
    public Task TestAsync(CancellationToken cancellationToken)
    {
        Record(Loc.T("Test message – notifications are working.", "Testnachricht – die Benachrichtigung funktioniert."));
        return FlushAsync("Test", null, cancellationToken);
    }

    private async Task SendWebhookAsync(string url, string title, string body, CancellationToken cancellationToken)
    {
        try
        {
            using var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(20);
            HttpResponseMessage response;
            if (url.Contains("discord.com/api/webhooks", StringComparison.OrdinalIgnoreCase))
            {
                var text = $"**{title}**\n{body}";
                response = await client.PostAsJsonAsync(url, new { content = text.Length > 1900 ? text[..1900] + " …" : text }, cancellationToken).ConfigureAwait(false);
            }
            else if (url.Contains("ntfy", StringComparison.OrdinalIgnoreCase))
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8) };
                request.Headers.TryAddWithoutValidation("Title", title);
                response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Gotify and generic JSON receivers.
                response = await client.PostAsJsonAsync(url, new { title, message = body }, cancellationToken).ConfigureAwait(false);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("SubtitleForge: webhook answered {Status}", (int)response.StatusCode);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            _logger.LogWarning("SubtitleForge: webhook failed: {Message}", ex.Message);
        }
    }
}
