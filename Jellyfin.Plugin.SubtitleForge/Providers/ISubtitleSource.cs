using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SubtitleForge.Providers;

/// <summary>
/// A remote subtitle source.
/// </summary>
public interface ISubtitleSource
{
    /// <summary>Gets the source name (also used as file name tag).</summary>
    string Name { get; }

    /// <summary>Gets a value indicating whether credentials are configured.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Searches subtitles.
    /// </summary>
    /// <param name="query">Query.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Candidates (unscored).</returns>
    Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(SubtitleQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads the raw payload of a candidate.
    /// </summary>
    /// <param name="candidate">Candidate.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Bytes and an optional file name.</returns>
    Task<(byte[] Data, string? FileName)> DownloadAsync(SubtitleCandidate candidate, CancellationToken cancellationToken);
}
