using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleForge.Whisper;

/// <summary>
/// A downloadable whisper.cpp model (quantised ggml).
/// </summary>
/// <param name="Key">Config key.</param>
/// <param name="File">File name on Hugging Face.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="Sha256">SHA-256 of the file.</param>
/// <param name="CanTranslate">Whether the model supports the translate task.</param>
public sealed record WhisperModelInfo(string Key, string File, long Size, string Sha256, bool CanTranslate);

/// <summary>
/// Downloads and verifies whisper models on first use (pinned to a fixed commit of ggerganov/whisper.cpp, MIT).
/// </summary>
public sealed class WhisperModels : IDisposable
{
    private const string Commit = "5359861c739e955e79d9a303bcbc70fb988958b1";

    /// <summary>Known models.</summary>
    public static readonly IReadOnlyDictionary<string, WhisperModelInfo> All = new Dictionary<string, WhisperModelInfo>(StringComparer.OrdinalIgnoreCase)
    {
        ["base"] = new("base", "ggml-base-q5_1.bin", 59707625, "422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898", true),
        ["small"] = new("small", "ggml-small-q5_1.bin", 190085487, "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb", true),
        ["medium"] = new("medium", "ggml-medium-q5_0.bin", 539212467, "19fea4b380c3a618ec4723c3eef2eb785ffba0d0538cf43f8f235e7b3b34220f", true),
        ["large-v3-turbo"] = new("large-v3-turbo", "ggml-large-v3-turbo-q5_0.bin", 574041195, "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2", false),
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WhisperModels> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="WhisperModels"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public WhisperModels(IHttpClientFactory httpClientFactory, ILogger<WhisperModels> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets the model info for a key (falls back to "small").
    /// </summary>
    /// <param name="key">Key.</param>
    /// <returns>Info.</returns>
    public static WhisperModelInfo Get(string? key) => key is not null && All.TryGetValue(key, out var m) ? m : All["small"];

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Returns the local path of a model, downloading and verifying it first if needed.
    /// </summary>
    /// <param name="model">Model.</param>
    /// <param name="directory">Target directory.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Path.</returns>
    public async Task<string> EnsureAsync(WhisperModelInfo model, string directory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, model.File);
        if (File.Exists(path) && new FileInfo(path).Length == model.Size)
        {
            return path;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length == model.Size)
            {
                return path;
            }

            Directory.CreateDirectory(directory);
            var part = path + ".part";
            var url = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/{Commit}/{model.File}";
            _logger.LogInformation("SubtitleForge: downloading whisper model {File} ({Mb} MB)", model.File, model.Size / 1_000_000);
            using (var client = _httpClientFactory.CreateClient(nameof(WhisperModels)))
            {
                client.Timeout = TimeSpan.FromMinutes(30);
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var target = File.Create(part);
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            string hash;
            await using (var fs = File.OpenRead(part))
            {
                hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            }

            if (!hash.Equals(model.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                throw new InvalidDataException($"Whisper model {model.File}: checksum mismatch");
            }

            File.Move(part, path, true);
            _logger.LogInformation("SubtitleForge: whisper model {File} ready", model.File);
            return path;
        }
        finally
        {
            _gate.Release();
        }
    }
}
