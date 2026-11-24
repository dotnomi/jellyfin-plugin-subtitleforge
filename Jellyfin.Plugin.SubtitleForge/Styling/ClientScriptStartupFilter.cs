using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleForge.Styling;

/// <summary>
/// Adds a middleware in front of the Jellyfin pipeline that appends the SubtitleForge client script
/// to the web client's index.html. Nothing is written to disk, so it also works with read-only web dirs.
/// </summary>
public class ClientScriptStartupFilter : IStartupFilter
{
    private const string ScriptMarker = "SubtitleForge/client.js";

    private readonly ILogger<ClientScriptStartupFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClientScriptStartupFilter"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public ClientScriptStartupFilter(ILogger<ClientScriptStartupFilter> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        _logger.LogInformation("SubtitleForge: registering web client script injection");
        return app =>
        {
            app.Use(InjectAsync);
            next(app);
        };
    }

    private static bool IsIndexRequest(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method))
        {
            return false;
        }

        var path = request.Path.Value ?? string.Empty;
        return path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase);
    }

    private async Task InjectAsync(HttpContext context, Func<Task> next)
    {
        // Always injected: besides the style (only when enabled) it adds the "Untertitel" button for admins.
        if (Plugin.Instance is null || !IsIndexRequest(context.Request))
        {
            await next().ConfigureAwait(false);
            return;
        }

        // Ask for an uncompressed, non-cached body so it can be rewritten.
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("If-None-Match");
        context.Request.Headers.Remove("If-Modified-Since");

        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next().ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        buffer.Position = 0;
        var isHtml = context.Response.StatusCode == StatusCodes.Status200OK
            && (context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false);
        if (!isHtml)
        {
            await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
            return;
        }

        var html = Encoding.UTF8.GetString(buffer.ToArray());
        if (!html.Contains(ScriptMarker, StringComparison.Ordinal))
        {
            var scriptTag = "<script src=\"../" + ScriptMarker + "?v=" + StyleController.VersionTag() + "\" defer></script>";
            var idx = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            html = idx >= 0 ? html.Insert(idx, scriptTag) : html + scriptTag;
        }

        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentLength = bytes.Length;
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.CacheControl = "no-cache";
        await originalBody.WriteAsync(bytes).ConfigureAwait(false);
    }
}
