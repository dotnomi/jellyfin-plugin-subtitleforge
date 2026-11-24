using Jellyfin.Plugin.SubtitleForge.Common;
using Jellyfin.Plugin.SubtitleForge.Providers;
using Jellyfin.Plugin.SubtitleForge.Styling;
using Jellyfin.Plugin.SubtitleForge.Sync;
using Jellyfin.Plugin.SubtitleForge.Translation;
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
        serviceCollection.AddSingleton<Throttle>();
        serviceCollection.AddSingleton<Notifier>();
        serviceCollection.AddSingleton<SubtitleFileWriter>();
        serviceCollection.AddSingleton<SubDlSource>();
        serviceCollection.AddSingleton<OpenSubtitlesSource>();

        // OpenSubtitles requests carry the API key in a custom header, which .NET would keep on a redirect to another
        // host: this client does not follow redirects at all.
        serviceCollection.AddHttpClient(nameof(OpenSubtitlesSource))
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            });
        serviceCollection.AddSingleton<JimakuSource>();
        serviceCollection.AddSingleton<QuotaGuard>();
        serviceCollection.AddSingleton<SubtitleSearchService>();
        serviceCollection.AddSingleton<DownloadStateStore>();
        serviceCollection.AddSingleton<SpeechAnalyzer>();
        serviceCollection.AddSingleton<SyncService>();
        serviceCollection.AddSingleton<MediaBrowser.Controller.Subtitles.ISubtitleProvider, ForgeSubtitleProvider>();
        serviceCollection.AddSingleton<LanguageResolver>();
        serviceCollection.AddSingleton<WhisperModels>();
        serviceCollection.AddSingleton<TranslationModels>();
        serviceCollection.AddSingleton<TranslationService>();
        serviceCollection.AddSingleton<Translation.SubtitleTranslator>();
        serviceCollection.AddSingleton<WhisperService>();
        serviceCollection.AddSingleton<ItemProcessor>();
        serviceCollection.AddSingleton<Whisper.GpuSelfTest>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<Whisper.GpuSelfTest>());
        serviceCollection.AddSingleton<BrandMigration>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<BrandMigration>());
        serviceCollection.AddSingleton<WorkQueue>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<WorkQueue>());
    }
}
