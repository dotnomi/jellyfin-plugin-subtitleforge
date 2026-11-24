namespace Jellyfin.Plugin.SubtitleForge.Ocr;

/// <summary>
/// Limits for decoding image subtitles. Subtitle tracks come from media files, which may be crafted: these bounds
/// keep a hostile track from exhausting Jellyfin's memory. Real Blu-ray/UHD/DVD subtitles stay far below them.
/// </summary>
public static class OcrLimits
{
    /// <summary>Largest object width or height (UHD is 3840×2160).</summary>
    public const int MaxDimension = 4096;

    /// <summary>Largest object size in pixels.</summary>
    public const long MaxObjectPixels = 4096L * 2304;

    /// <summary>Largest compressed (RLE) size of one object.</summary>
    public const int MaxObjectBytes = 8 * 1024 * 1024;

    /// <summary>At most this many objects of one display set are rendered.</summary>
    public const int MaxObjectsPerSubtitle = 8;

    /// <summary>Decoded images of one stream that may be kept at once (VobSub keeps a whole stream; bytes = pixels).</summary>
    public const long MaxStreamPixels = 400_000_000;

    /// <summary>Total decoding work per stream (pixels); bounds the CPU time a crafted stream can cost.</summary>
    public const long MaxDecodedPixels = 4_000_000_000;

    /// <summary>Largest DVD subtitle area (DVD is 720×576; generous for odd sources).</summary>
    public const int MaxDvdWidth = 1920;

    /// <summary>Largest DVD subtitle height.</summary>
    public const int MaxDvdHeight = 1080;

    /// <summary>Largest VobSub stream that is read (a full film is a few MB).</summary>
    public const int MaxVobSubBytes = 256 * 1024 * 1024;

    /// <summary>Compressed object data of one PGS stream that may be cached at once.</summary>
    public const long MaxRleBytes = 64L * 1024 * 1024;
}
