using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleForge.Translation;

/// <summary>
/// One file of a translation model.
/// </summary>
/// <param name="Name">File name (also local name).</param>
/// <param name="RemotePath">Path inside the Hugging Face repository.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="Sha256">SHA-256.</param>
public sealed record ModelFile(string Name, string RemotePath, long Size, string Sha256);

/// <summary>
/// An offline translation model for one direction.
/// </summary>
/// <param name="Pair">"de-en" etc.</param>
/// <param name="Repository">Hugging Face repository.</param>
/// <param name="Commit">Pinned commit.</param>
/// <param name="License">License.</param>
/// <param name="Files">Files.</param>
public sealed record TranslationModelInfo(string Pair, string Repository, string Commit, string License, IReadOnlyList<ModelFile> Files)
{
    /// <summary>Gets the total size in bytes.</summary>
    public long TotalSize => Files.Sum(f => f.Size);
}

/// <summary>
/// Catalogue and downloader of offline translation models (quantised Marian/Opus-MT ONNX exports).
/// de↔en: Helsinki-NLP Opus-MT (Apache-2.0, ONNX by Xenova). ja↔en: FuguMT by staka (CC-BY-SA-4.0, ONNX export by Kadonox).
/// </summary>
public sealed class TranslationModels : IDisposable
{
    /// <summary>Available directions.</summary>
    public static readonly IReadOnlyDictionary<string, TranslationModelInfo> All = new Dictionary<string, TranslationModelInfo>
    {
        ["de-en"] = new("de-en", "Xenova/opus-mt-de-en", "399dfd68706739fffd503f876093e455ae268a06", "Apache-2.0", new[]
        {
            new ModelFile("encoder_model_quantized.onnx", "onnx/encoder_model_quantized.onnx", 49366942, "4cedda8f8c89b72a42b3c6cd1e7a27f2de24457093e3bf80cb3e46829641fcd8"),
            new ModelFile("decoder_model_quantized.onnx", "onnx/decoder_model_quantized.onnx", 56281702, "e44c1c4b50e8f51e49d4d5e54a9af1550dc74763d4023644a38af62611e6efc9"),
            new ModelFile("source.spm", "source.spm", 796845, "bbd1f495eea99c8e21ae086d9146e0fa7b096c3dfdd9ba07ab8b631889df5c9b"),
            new ModelFile("vocab.json", "vocab.json", 1389436, "d5acea957b265a78554999144459c5e391e0df525864edc8287bc090290baa44"),
            new ModelFile("config.json", "config.json", 1376, "6754743ec4754957d8552dad869d1314f3af6626b67446ec4cebe5e2fbaa0fc4"),
        }),
        ["en-de"] = new("en-de", "Xenova/opus-mt-en-de", "1ca130c44c4c5441ef16d48aae521a424ab644f7", "Apache-2.0", new[]
        {
            new ModelFile("encoder_model_quantized.onnx", "onnx/encoder_model_quantized.onnx", 49366942, "15834b45fabd2dfb8c6c029b3ca3e7289aeefd90ece798ce42bcf548d1bd3b8d"),
            new ModelFile("decoder_model_quantized.onnx", "onnx/decoder_model_quantized.onnx", 56281702, "75ef79aa9bde9e3dce9ca584c29507be5f464973f6c600c89ff419bc8de29ebc"),
            new ModelFile("source.spm", "source.spm", 768489, "678f2a1177d8389f67b66299762dcc4fc567e89b07e212ba91b0c56daecf47ce"),
            new ModelFile("vocab.json", "vocab.json", 1389436, "d5acea957b265a78554999144459c5e391e0df525864edc8287bc090290baa44"),
            new ModelFile("config.json", "config.json", 1411, "517955ebf66cd6523f9982851dd41771b74fb2d9c00b4933ba2922ac7a63336f"),
        }),
        ["ja-en"] = new("ja-en", "Kadonox/fugumt-ja-en-onnx", "9b09cd7e6263223ac4a5988da1d881ef02b55be8", "CC-BY-SA-4.0", new[]
        {
            new ModelFile("encoder_model_quantized.onnx", "onnx/encoder_model_quantized.onnx", 35979078, "caea82d93435b76cd01edcb9dfb6dbfce82bde435bdc739a198a298014884b06"),
            new ModelFile("decoder_model_quantized.onnx", "onnx/decoder_model_quantized.onnx", 108270669, "d06c52f05e487e90b43cb36ea02841ccb70bd132858d8efc68a0c72c099b2f39"),
            new ModelFile("source.spm", "source.spm", 796876, "2b75a55d81ba5be08d5cc971eecbb03d8efaf66382f2a4f9f0a5fe2fca2b6741"),
            new ModelFile("vocab.json", "vocab.json", 861152, "bd868a2a41f8e2fd818fd575bb3f8fa5f1986859017de2badd2aa13971ef8769"),
            new ModelFile("config.json", "config.json", 1045, "24e201e35f20db82adcd87924d59398bf986560a46532d01fb2ae19cf606f17f"),
        }),
        ["en-ja"] = new("en-ja", "Kadonox/fugumt-en-ja-onnx", "0997e6ce157c3e784fcfc9917bf57ab4c74dbadc", "CC-BY-SA-4.0", new[]
        {
            new ModelFile("encoder_model_quantized.onnx", "onnx/encoder_model_quantized.onnx", 35979078, "60b290662bcb83d7ac1f20749dcbac95d4ff92752389ae418fd514ef0d4ce50b"),
            new ModelFile("decoder_model_quantized.onnx", "onnx/decoder_model_quantized.onnx", 108270669, "6408fe9d7e729be900cc414d85e6fd56d3f7b10642486f01ec608b6f9961cde4"),
            new ModelFile("source.spm", "source.spm", 768297, "92fcbf6449e47457866ec98891dab09ef625e57d9637fe3fdbbe44fc1a365b6d"),
            new ModelFile("vocab.json", "vocab.json", 791091, "2c0b445181f51bdea35b976a909ba450447ca9d00a2e8d9c978ae1ef46ef964e"),
            new ModelFile("config.json", "config.json", 1045, "add3cc696c382672ac06c390cb9877b0eb6b6cf49da8d2e0bb0c710e6145b9df"),
        }),
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TranslationModels> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="TranslationModels"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public TranslationModels(IHttpClientFactory httpClientFactory, ILogger<TranslationModels> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// The chain of model directions to translate from one language to another (direct, or via English).
    /// </summary>
    /// <param name="from">ISO 639-1 source.</param>
    /// <param name="to">ISO 639-1 target.</param>
    /// <returns>Directions, or null if not possible.</returns>
    public static IReadOnlyList<string>? Route(string from, string to)
    {
        if (from == to)
        {
            return Array.Empty<string>();
        }

        if (All.ContainsKey($"{from}-{to}"))
        {
            return new[] { $"{from}-{to}" };
        }

        if (from != "en" && to != "en" && All.ContainsKey($"{from}-en") && All.ContainsKey($"en-{to}"))
        {
            return new[] { $"{from}-en", $"en-{to}" };
        }

        return null;
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Returns the local folder of a model, downloading and verifying missing files first.
    /// </summary>
    /// <param name="pair">Direction.</param>
    /// <param name="root">Root folder for models.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Folder.</returns>
    public async Task<string> EnsureAsync(string pair, string root, CancellationToken cancellationToken)
    {
        var model = All[pair];
        var dir = Path.Combine(root, pair);
        if (model.Files.All(f => IsPresent(dir, f)))
        {
            return dir;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(dir);
            _logger.LogInformation("SubtitleForge: downloading translation model {Pair} ({Mb} MB, {License})", pair, model.TotalSize / 1_000_000, model.License);
            using var client = _httpClientFactory.CreateClient(nameof(TranslationModels));
            client.Timeout = TimeSpan.FromMinutes(30);
            foreach (var file in model.Files.Where(f => !IsPresent(dir, f)))
            {
                var path = Path.Combine(dir, file.Name);
                var part = path + ".part";
                var url = $"https://huggingface.co/{model.Repository}/resolve/{model.Commit}/{file.RemotePath}";
                using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
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

                if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(part);
                    throw new InvalidDataException($"Translation model {pair}/{file.Name}: checksum mismatch");
                }

                File.Move(part, path, true);
            }

            return dir;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsPresent(string dir, ModelFile file)
    {
        var path = Path.Combine(dir, file.Name);
        return File.Exists(path) && new FileInfo(path).Length == file.Size;
    }
}
