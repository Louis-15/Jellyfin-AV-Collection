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
        MigrateConfiguration();
    }

    /// <summary>
    /// 把上一版写进配置文件的旧值迁移到当前语义。
    ///
    /// 触发场景：1.0.0.9 及更早版本的配置文件里 <c>Ai.TimeoutSeconds</c> 是 60，
    /// 而实测推理模型判断 20 位演员要 149 秒 —— 升级后不改这个值，「AI 智能合并」永远超时。
    /// 迁移只跑一次（靠 <see cref="PluginConfiguration.ConfigVersion"/> 判断），
    /// 之后用户手动调小也不会被再覆盖。
    /// </summary>
    private void MigrateConfiguration()
    {
        const int CurrentVersion = 1;

        if (Configuration.ConfigVersion >= CurrentVersion)
        {
            return;
        }

        try
        {
            var timeout = Configuration.Ai?.TimeoutSeconds ?? 0;

            // 只提升明显不够用的旧值；用户如果已经调到 120 以上就尊重他的选择。
            if (timeout < 120)
            {
                Configuration.Ai ??= new AiOptions();
                Configuration.Ai.TimeoutSeconds = 300;
            }

            Configuration.ConfigVersion = CurrentVersion;
            UpdateConfiguration(Configuration);
        }
        catch (Exception)
        {
            // 迁移失败不该阻止插件加载：最坏情况是用户再去设置页手改一次。
        }
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
