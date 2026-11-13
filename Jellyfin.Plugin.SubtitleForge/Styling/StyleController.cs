using System.IO;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.SubtitleForge.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SubtitleForge.Styling;

/// <summary>
/// Serves the web client script with the configured style baked in.
/// </summary>
[ApiController]
[Route("SubtitleForge")]
public class StyleController : ControllerBase
{
    private static readonly string ClientScript = LoadScript();

    /// <summary>
    /// Gets the client script.
    /// </summary>
    /// <returns>JavaScript.</returns>
    [HttpGet("client.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ContentResult GetClientScript()
    {
        var json = StyleSettingsJson();

        Response.Headers.CacheControl = "no-store";
        return Content("window.__subtitleForgeStyle = " + json + ";\n" + ClientScript, "application/javascript");
    }

    /// <summary>
    /// Serializes the style part of the configuration for the client.
    /// </summary>
    /// <returns>JSON.</returns>
    internal static string StyleSettingsJson()
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        return JsonSerializer.Serialize(new
        {
            enabled = config.StyleEnabled,
            fontFamily = config.FontFamily,
            fontSizeVh = config.FontSizeVh,
            fontWeight = config.FontWeight,
            textColor = config.TextColor,
            outlineColor = config.OutlineColor,
            outlineWidthPx = config.OutlineWidthPx,
            dropShadow = config.DropShadow,
            backgroundColor = config.BackgroundColor,
            backgroundOpacity = config.BackgroundOpacity,
            bottomOffsetPercent = config.BottomOffsetPercent,
        });
    }

    /// <summary>
    /// Gets a short version tag that changes whenever the style or the script changes (cache busting).
    /// </summary>
    /// <returns>Hex string.</returns>
    internal static string VersionTag()
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(StyleSettingsJson() + ClientScript));
        return System.Convert.ToHexString(bytes, 0, 6).ToLowerInvariant();
    }

    private static string LoadScript()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("Jellyfin.Plugin.SubtitleForge.Styling.client.js");
        if (stream is null)
        {
            return "console.error('SubtitleForge: client.js resource missing');";
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
