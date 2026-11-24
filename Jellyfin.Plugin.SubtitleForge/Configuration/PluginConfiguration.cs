using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SubtitleForge.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the server-enforced style is injected into the web client.
    /// </summary>
    public bool StyleEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the CSS font family.
    /// </summary>
    public string FontFamily { get; set; } = "\"Noto Sans\", \"Open Sans\", Arial, sans-serif";

    /// <summary>
    /// Gets or sets the font size relative to the video height in percent (vh).
    /// </summary>
    public double FontSizeVh { get; set; } = 4.5;

    /// <summary>
    /// Gets or sets the CSS font weight (400 = normal, 700 = bold).
    /// </summary>
    public int FontWeight { get; set; } = 600;

    /// <summary>
    /// Gets or sets the text color (#rrggbb).
    /// </summary>
    public string TextColor { get; set; } = "#ffffff";

    /// <summary>
    /// Gets or sets the outline color (#rrggbb).
    /// </summary>
    public string OutlineColor { get; set; } = "#000000";

    /// <summary>
    /// Gets or sets the outline width in px (0 = none).
    /// </summary>
    public double OutlineWidthPx { get; set; } = 2;

    /// <summary>
    /// Gets or sets a value indicating whether a soft drop shadow is drawn.
    /// </summary>
    public bool DropShadow { get; set; } = true;

    /// <summary>
    /// Gets or sets the background color (#rrggbb).
    /// </summary>
    public string BackgroundColor { get; set; } = "#000000";

    /// <summary>
    /// Gets or sets the background opacity 0..1 (0 = no box).
    /// </summary>
    public double BackgroundOpacity { get; set; }

    /// <summary>
    /// Gets or sets the distance of the subtitle block from the bottom edge in percent of the video height.
    /// </summary>
    public double BottomOffsetPercent { get; set; } = 6;

    /// <summary>
    /// Gets or sets a value indicating whether image subtitles without language tag are converted too.
    /// </summary>
    public bool OcrIncludeUndefined { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether OCR is skipped when the file already has an embedded text subtitle
    /// with the same language and forced flag.
    /// </summary>
    public bool OcrSkipIfTextExists { get; set; } = true;

    /// <summary>
    /// Gets or sets the CPU threads used by OCR (1-2 recommended on NAS CPUs).
    /// </summary>
    public int OcrThreads { get; set; } = 2;

    /// <summary>
    /// Gets or sets a value indicating whether written subtitles contain only spoken text
    /// (no "[SCREAMING]", "(Schüsse)", speaker labels).
    /// </summary>
    public bool SpokenTextOnly { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether song lyrics (lines with ♪) are removed as well.
    /// </summary>
    public bool RemoveLyrics { get; set; }

    /// <summary>
    /// Gets or sets the comma separated languages to download (ISO 639-1, e.g. "de,en").
    /// </summary>
    public string DownloadLanguages { get; set; } = "de,en";

    /// <summary>
    /// Gets or sets a value indicating whether only safe matches (hash or same release group/source) are saved automatically.
    /// Until audio sync verification exists this should stay on.
    /// </summary>
    public bool DownloadOnlySafeMatches { get; set; } = true;

    /// <summary>
    /// Gets or sets the number of days before an item without a good subtitle is searched again.
    /// </summary>
    public int DownloadRetryDays { get; set; } = 7;

    /// <summary>
    /// Gets or sets the SubDL API key.
    /// </summary>
    public string SubDlApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the OpenSubtitles.com API key.
    /// </summary>
    public string OpenSubtitlesApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the OpenSubtitles.com user name (more downloads per day than anonymous).
    /// </summary>
    public string OpenSubtitlesUsername { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the OpenSubtitles.com password.
    /// </summary>
    public string OpenSubtitlesPassword { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the automatic job only spends OpenSubtitles downloads on hash matches.
    /// </summary>
    public bool OpenSubtitlesOnlyHashMatches { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether downloaded subtitles are verified against the audio and their timing corrected.
    /// </summary>
    public bool SyncEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets how many candidates per language may be downloaded and checked against the audio.
    /// </summary>
    public int SyncMaxCandidates { get; set; } = 4;

    /// <summary>
    /// Gets or sets a value indicating whether the sync task may correct external subtitles that SubtitleForge did not create
    /// (the original is kept as *.orig).
    /// </summary>
    public bool SyncFixForeignSubtitles { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether speech-to-text (Whisper) is available.
    /// </summary>
    public bool WhisperEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether Whisper runs automatically when no fitting subtitle was found.
    /// Off by default: Whisper is expensive on small CPUs, so it is started manually per film/episode.
    /// </summary>
    public bool WhisperAutoFallback { get; set; }

    /// <summary>
    /// Gets or sets the Whisper model: base, small, medium, large-v3-turbo.
    /// </summary>
    public string WhisperModel { get; set; } = "small";

    /// <summary>
    /// Gets or sets a value indicating whether Whisper may use the GPU via Vulkan (AMD, Intel, NVIDIA) with automatic CPU
    /// fallback. Takes effect after a Jellyfin restart.
    /// </summary>
    public bool WhisperUseGpu { get; set; } = true;

    /// <summary>
    /// Gets or sets the CPU threads for Whisper (0 = automatic: all cores but one, max. 8).
    /// </summary>
    public int WhisperThreads { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Whisper output may be translated offline into another language
    /// (de↔en, ja↔en, ja↔de via English; models are downloaded on first use).
    /// </summary>
    public bool TranslationEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether newly added movies/episodes are processed automatically.
    /// </summary>
    public bool ProcessNewItems { get; set; } = true;

    /// <summary>
    /// Gets or sets the delay in minutes after a new item was added before it is processed (lets the library scan finish).
    /// </summary>
    public int NewItemDelayMinutes { get; set; } = 10;

    /// <summary>Gets or sets a value indicating whether a missing language is translated offline from an existing subtitle of another language (before Whisper is considered).</summary>
    public bool TranslateExistingSubtitles { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether heavy work pauses while someone is watching.</summary>
    public bool PauseDuringPlayback { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether automatic work (new items, Whisper as last resort) is limited to a time window.</summary>
    public bool AutoWorkWindowEnabled { get; set; }

    /// <summary>Gets or sets the start hour of the window for automatic work (local time).</summary>
    public int AutoWorkStartHour { get; set; } = 1;

    /// <summary>Gets or sets the end hour of the window for automatic work (local time, exclusive).</summary>
    public int AutoWorkEndHour { get; set; } = 7;

    /// <summary>Gets or sets a value indicating whether a summary is written to Jellyfin's activity log after each run.</summary>
    public bool NotifyActivityLog { get; set; } = true;

    /// <summary>Gets or sets an optional webhook for summaries (ntfy, Gotify, Discord or any JSON endpoint).</summary>
    public string NotifyWebhookUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether Whisper is used automatically as a last resort (see below).</summary>
    public bool WhisperLastResort { get; set; }

    /// <summary>Gets or sets after how many days without a found subtitle Whisper steps in.</summary>
    public int WhisperLastResortAfterDays { get; set; } = 14;

    /// <summary>Gets or sets how many last-resort Whisper jobs run per download run (per night).</summary>
    public int WhisperLastResortPerRun { get; set; } = 3;

    /// <summary>Gets or sets a value indicating whether the GPU is tested briefly after Jellyfin starts.</summary>
    public bool GpuSelfTest { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether embedded ASS/SSA subtitles are converted into clean SRT (dialogue only).</summary>
    public bool ConvertAssToSrt { get; set; }

    /// <summary>Gets or sets a value indicating whether a forced subtitle (signs, songs, foreign-language parts) is derived from full subtitles when none exists.</summary>
    public bool DeriveForcedSubtitles { get; set; }

    /// <summary>Gets or sets the Jimaku API key (anime subtitles, Japanese/English).</summary>
    public string JimakuApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the ids of the libraries the plugin works on (empty = all libraries).
    /// </summary>
#pragma warning disable CA1819 // XML-serialised configuration
    public string[] Libraries { get; set; } = System.Array.Empty<string>();
#pragma warning restore CA1819

    /// <summary>
    /// Gets or sets the language of the plugin's pages and messages: "auto" (server display language), "en" or "de".
    /// </summary>
    public string UiLanguage { get; set; } = "auto";

    /// <summary>
    /// Gets or sets a value indicating whether older subtitle files got the "[SF]" mark (done once).
    /// </summary>
    public bool BrandMigrationDone { get; set; }
}
