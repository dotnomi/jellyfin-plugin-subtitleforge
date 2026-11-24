using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SubtitleForge.Common;

/// <summary>
/// Guards for outbound requests: only https to known hosts, and bounded response sizes.
/// </summary>
public static class SafeHttp
{
    /// <summary>Largest subtitle download (archive or file) that is accepted.</summary>
    public const long MaxSubtitleDownloadBytes = 10L * 1024 * 1024;

    /// <summary>
    /// Parses a URL and checks that it is https and points to one of the given hosts (or a subdomain of them).
    /// </summary>
    /// <param name="url">URL.</param>
    /// <param name="hosts">Allowed hosts, e.g. "subdl.com".</param>
    /// <returns>The URI.</returns>
    /// <exception cref="InvalidDataException">If the URL is not allowed.</exception>
    public static Uri RequireHost(string url, params string[] hosts)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !IsAllowedHost(uri.Host, hosts))
        {
            throw new InvalidDataException("Download URL not allowed: " + (uri?.Host ?? "invalid"));
        }

        return uri;
    }

    /// <summary>
    /// Whether a host is one of the allowed hosts or a subdomain of one.
    /// </summary>
    /// <param name="host">Host name.</param>
    /// <param name="hosts">Allowed hosts.</param>
    /// <returns>True if allowed.</returns>
    public static bool IsAllowedHost(string host, params string[] hosts) =>
        hosts.Any(h => string.Equals(host, h, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// GETs a URL and reads the body, aborting when it is larger than <paramref name="maxBytes"/>.
    /// </summary>
    /// <param name="client">Client.</param>
    /// <param name="uri">URL.</param>
    /// <param name="maxBytes">Size limit.</param>
    /// <param name="onResponse">Inspects the response before reading (status checks); may throw.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Body.</returns>
    public static async Task<byte[]> GetBytesLimitedAsync(HttpClient client, Uri uri, long maxBytes, Action<HttpResponseMessage>? onResponse, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        onResponse?.Invoke(response);
        response.EnsureSuccessStatusCode();
        return await ReadLimitedAsync(response.Content, maxBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads content up to a size limit.
    /// </summary>
    /// <param name="content">Content.</param>
    /// <param name="maxBytes">Size limit.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Bytes.</returns>
    /// <exception cref="InvalidDataException">If the content is larger.</exception>
    public static async Task<byte[]> ReadLimitedAsync(HttpContent content, long maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maxBytes)
        {
            throw new InvalidDataException("Response too large");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await ReadLimitedAsync(stream, maxBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a stream up to a size limit.
    /// </summary>
    /// <param name="stream">Stream.</param>
    /// <param name="maxBytes">Size limit.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Bytes.</returns>
    /// <exception cref="InvalidDataException">If the stream is longer.</exception>
    public static async Task<byte[]> ReadLimitedAsync(Stream stream, long maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new InvalidDataException("Response too large");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Copies a stream to a file, aborting when more than <paramref name="maxBytes"/> arrive (a model download from a
    /// misbehaving mirror must not fill the disk). The partial file is removed on any failure.
    /// </summary>
    /// <param name="source">Source.</param>
    /// <param name="path">Target file.</param>
    /// <param name="maxBytes">Size limit.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Task.</returns>
    public static async Task CopyToFileLimitedAsync(Stream source, string path, long maxBytes, CancellationToken cancellationToken)
    {
        var ok = false;
        try
        {
            var target = File.Create(path);
            await using (target.ConfigureAwait(false))
            {
                var chunk = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                    {
                        throw new InvalidDataException("Download larger than expected");
                    }

                    await target.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }

            ok = true;
        }
        finally
        {
            if (!ok && File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// Synchronous variant for decompression streams.
    /// </summary>
    /// <param name="stream">Stream.</param>
    /// <param name="maxBytes">Size limit.</param>
    /// <returns>Bytes, or null if the stream is longer.</returns>
    public static byte[]? ReadLimited(Stream stream, long maxBytes)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
