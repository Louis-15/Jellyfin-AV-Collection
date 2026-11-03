using Jellyfin.Plugin.AVCollection.Core;
using Jellyfin.Plugin.AVCollection.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AVCollection;

/// <summary>
/// 插件服务注册。
///
/// Jellyfin 通过 DI 解析 <see cref="MediaBrowser.Model.Tasks.IScheduledTask"/>，
/// 所以定时任务必须在这里显式注册，光有类是不够的。
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        // AI 建议要发 HTTP 请求，用工厂管理连接池（插件被重载时不会漏 socket）。
        services.AddHttpClient();

        services.AddSingleton<PluginLog>();
        services.AddSingleton<RuntimeState>();
        services.AddSingleton<TranslationStore>();
        services.AddSingleton<ActorIndex>();
        services.AddSingleton<CollectionWriter>();
        services.AddSingleton<CollectionSyncJob>();
        services.AddSingleton<AiClient>();
        services.AddSingleton<AiNameTranslator>();

        services.AddSingleton<ActorCollectionSyncTask>();

        // 实时入库监听：新片刮削完成后只更新它涉及的演员合集，不扫全库。
        services.AddHostedService<ItemWatchService>();
    }
}
