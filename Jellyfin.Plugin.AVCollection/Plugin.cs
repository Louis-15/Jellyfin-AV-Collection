using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AVCollection;

/// <summary>
/// AV Collection：按演员自动维护 Jellyfin 合集。
///
/// 三个能力：
/// 1. 把同一个演员的影片自动归到以他命名的合集里（每次计划任务增量同步）。
/// 2. 用演员头像当合集封面。
/// 3. 把同一演员的多个艺名合并到一个合集，合集名包含最出名的几个艺名。
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>插件 Id，必须与 meta.json 一致。</summary>
    public static readonly Guid PluginId = Guid.Parse("a9a417c4-254a-4c10-b869-f6a38bf418ee");

    /// <summary>初始化插件。</summary>
    /// <param name="applicationPaths">Jellyfin 的路径表，用来定位配置/数据目录。</param>
    /// <param name="xmlSerializer">配置序列化器。</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>获取当前插件实例（计划任务启动时可能还是 null）。</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "AV Collection";

    /// <inheritdoc />
    public override string Description =>
        "按演员自动建立并维护合集：自动用演员头像做封面，并支持把同一演员的多个艺名合并到一个合集。";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <summary>
    /// 当前配置。计划任务可能在插件实例就绪前被调用，此时回落到一份默认配置，
    /// 避免到处写 null 判断。
    /// </summary>
    public static PluginConfiguration Current => Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        // 只保留一个页面。第二个页面若带 EnableInMainMenu 会抢走插件「设置」按钮的落点，
        // 这是 Jellyfin 插件常见的坑。
        return
        [
            new PluginPageInfo
            {
                Name = "avcollection",
                EmbeddedResourcePath = GetType().Namespace + ".Web.configPage.html"
            }
        ];
    }
}
