using System;
using System.IO;
using System.Linq;
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
        // These values end up in a <style> on every client: only well-formed values pass (no CSS injection).
        return JsonSerializer.Serialize(new
        {
            enabled = config.StyleEnabled,
            fontFamily = SafeFont(config.FontFamily),
            fontSizeVh = Finite(config.FontSizeVh, 4.5, 1, 15),
            fontWeight = Math.Clamp(config.FontWeight, 100, 900),
            textColor = SafeColor(config.TextColor, "#ffffff"),
            outlineColor = SafeColor(config.OutlineColor, "#000000"),
            outlineWidthPx = Finite(config.OutlineWidthPx, 2, 0, 8),
            dropShadow = config.DropShadow,
            backgroundColor = SafeColor(config.BackgroundColor, "#000000"),
            backgroundOpacity = Finite(config.BackgroundOpacity, 0, 0, 1),
            bottomOffsetPercent = Finite(config.BottomOffsetPercent, 6, 0, 50),
            lang = Jellyfin.Plugin.SubtitleForge.Common.Loc.Language,
        });
    }

    /// <summary>
    /// Gets a short version tag that changes whenever the style or the script changes (cache busting).
    /// </summary>
    /// <returns>Hex string.</returns>
    private static double Finite(double value, double fallback, double min, double max) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private static string SafeColor(string? value, string fallback) =>
        value is not null && System.Text.RegularExpressions.Regex.IsMatch(value, "^#[0-9a-fA-F]{6}\\z") ? value : fallback;

    private static string SafeFont(string? value) =>
        value is not null && value.Length <= 200 && System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9 ,'\"_-]+\\z") && value.Count(c => c == '"') % 2 == 0 && value.Count(c => c == '\'') % 2 == 0 ? value : "sans-serif";

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
