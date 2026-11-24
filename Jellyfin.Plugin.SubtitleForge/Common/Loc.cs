using System;
using System.Globalization;

namespace Jellyfin.Plugin.SubtitleForge.Common;

/// <summary>
/// User-facing texts in English (default) or German. The language is the plugin setting "UiLanguage", or with
/// "auto" the server's preferred display language (Dashboard → General).
/// </summary>
public static class Loc
{
    /// <summary>
    /// Gets or sets the source of the server's display language (set by the plugin at startup).
    /// </summary>
    internal static Func<string?> ServerCulture { get; set; } = () => null;

    /// <summary>
    /// Gets the active language: "en" or "de".
    /// </summary>
    public static string Language
    {
        get
        {
            var configured = Plugin.Instance?.Configuration.UiLanguage;
            if (configured is "en" or "de")
            {
                return configured;
            }

            string? culture;
            try
            {
                culture = ServerCulture();
            }
            catch (InvalidOperationException)
            {
                culture = null;
            }

            culture = string.IsNullOrEmpty(culture) ? CultureInfo.CurrentUICulture.Name : culture;
            return culture.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? "de" : "en";
        }
    }

    /// <summary>
    /// Gets a value indicating whether texts are German.
    /// </summary>
    public static bool German => Language == "de";

    /// <summary>
    /// Picks the text for the active language.
    /// </summary>
    /// <param name="en">English text.</param>
    /// <param name="de">German text.</param>
    /// <returns>Text.</returns>
    public static string T(string en, string de) => German ? de : en;
}
