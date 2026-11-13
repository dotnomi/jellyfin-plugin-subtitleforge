using Jellyfin.Plugin.SubtitleForge.Common;
using Jellyfin.Plugin.SubtitleForge.Providers;
using Jellyfin.Plugin.SubtitleForge.Styling;
using Jellyfin.Plugin.SubtitleForge.Sync;
using Jellyfin.Plugin.SubtitleForge.Whisper;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SubtitleForge;

/// <summary>
/// Registers plugin services in the Jellyfin DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddTransient<IStartupFilter, ClientScriptStartupFilter>();
        serviceCollection.AddSingleton<FfmpegRunner>();
        serviceCollection.AddSingleton<SubtitleFileWriter>();
        serviceCollection.AddSingleton<SubDlSource>();
        serviceCollection.AddSingleton<OpenSubtitlesSource>();
        serviceCollection.AddSingleton<SubtitleSearchService>();
        serviceCollection.AddSingleton<DownloadStateStore>();
        serviceCollection.AddSingleton<SpeechAnalyzer>();
        serviceCollection.AddSingleton<SyncService>();
        serviceCollection.AddSingleton<MediaBrowser.Controller.Subtitles.ISubtitleProvider, ForgeSubtitleProvider>();
        serviceCollection.AddSingleton<LanguageResolver>();
        serviceCollection.AddSingleton<WhisperModels>();
        serviceCollection.AddSingleton<WhisperService>();
        serviceCollection.AddSingleton<ItemProcessor>();
        serviceCollection.AddSingleton<WorkQueue>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<WorkQueue>());
    }
}
