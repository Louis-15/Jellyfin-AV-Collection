using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AVCollection;

/// <summary>
/// 插件配置。
///
/// 约定：
/// 1. 所有属性都必须是可读写的简单类型 / 可被 XmlSerializer 序列化的类型
///    （Jellyfin 用 XML 存插件配置，Dictionary 不行，所以运行期状态单独存 json）。
/// 2. 新增配置项一律给默认值，老配置文件缺字段时反序列化才不会炸。
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>总开关。关掉后两个计划任务都不再动任何合集。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 配置结构版本，用于一次性迁移。
    ///
    /// Jellyfin 把插件配置存成 XML 文件，**已存在的文件会覆盖代码里的新默认值**：
    /// 改了 <see cref="AiOptions.TimeoutSeconds"/> 的默认值，老用户升级后仍然读到旧值。
    /// 所以需要显式的版本号来驱动迁移。
    /// </summary>
    public int ConfigVersion { get; set; }

    /// <summary>
    /// 要处理的媒体库 Id。空数组表示"所有 movies 类型的库"，
    /// 这样用户不配置也不会什么都不做。
    /// </summary>
    public Guid[] LibraryIds { get; set; } = [];

    /// <summary>
    /// 合并组的合集名里最多出现几个艺名。
    /// 用户要求默认显示"最出名的两个艺名"，所以默认 2。
    /// </summary>
    public int MaxAliasesInName { get; set; } = 2;

    /// <summary>
    /// 合并后是否删除被合并掉的旧合集（成员已并到目标合集）。
    /// 默认删除，否则库里会留一堆只剩空壳的旧合集。
    /// </summary>
    public bool MergeRemoveOldCollections { get; set; } = true;

    /// <summary>是否用演员头像作为合集封面。</summary>
    public bool SetPosterFromActor { get; set; } = true;

    /// <summary>
    /// 是否清理演员合集上被 TMDb 误匹配的元数据（ProviderIds / 简介 / 首播日期）。
    /// 例如「かすみ」会被匹配成《真田女忍者霞传》。默认开启。
    /// </summary>
    public bool ClearWrongMetadata { get; set; } = true;

    /// <summary>
    /// 一次扫描至少要看到多少个演员才允许动合集。
    /// 媒体库扫描期间 Jellyfin 的 People 字段会返回空，若不设下限就会把
    /// 所有合集清空成空壳。沿用现网脚本的经验值 3。
    /// </summary>
    public int MinActorCount { get; set; } = 3;

    /// <summary>
    /// 是否在发现新片入库时立即更新对应演员的合集（默认开启）。
    ///
    /// 开启后插件每 30 秒检查一次「最近 6 小时内入库且已刮削完成」的影片，
    /// 只对这些片子涉及的演员做局部同步，**不扫全库**。
    /// 计划任务仍然保留，作为漏网条目的兜底。
    /// </summary>
    public bool RealtimeSync { get; set; } = true;

    /// <summary>同步后的校验轮数（防御 CollectionPostScanTask 清理"空合集"的竞态）。</summary>
    public int VerifyRounds { get; set; } = 3;

    /// <summary>每轮校验之间等待的秒数。</summary>
    public int VerifyDelaySeconds { get; set; } = 10;

    /// <summary>AI 艺名建议的模型配置。</summary>
    public AiOptions Ai { get; set; } = new();

    /// <summary>
    /// 用户配置的艺名合并组。每个组表示"这几个艺名其实是同一个人"。
    /// 没有出现在任何组里的演员，各自独立成一个合集（合集名就是演员名）。
    /// </summary>
    public List<ActorAliasGroup> AliasGroups { get; set; } = [];
}

/// <summary>
/// 一组指向同一个演员的艺名。
/// </summary>
public class ActorAliasGroup
{
    /// <summary>
    /// 组的稳定标识。运行期状态（目标合集 Id）按这个 Id 记录，
    /// 所以即使后续改了艺名列表，合集也不会丢。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>组内所有艺名（Jellyfin 里的 Person 名字）。</summary>
    public string[] Aliases { get; set; } = [];

    /// <summary>
    /// 手动指定的合集名。留空表示按"作品数从多到少取前 N 个艺名"自动生成。
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 用哪个艺名的头像做合集封面。留空表示用作品数最多的那个艺名。
    /// </summary>
    public string PosterAlias { get; set; } = string.Empty;
}

/// <summary>
/// OpenAI 兼容接口配置，用于让模型判断"哪些艺名可能是同一个人"。
/// 只做建议，不会自动落库：用户在设置页点确认后才写入 <see cref="PluginConfiguration.AliasGroups"/>。
/// </summary>
public class AiOptions
{
    /// <summary>是否启用 AI 建议。</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// AI 供应商。<see cref="AiProviders.OpenCode"/> / <see cref="AiProviders.CommandCode"/>（内置）
    /// 或 <see cref="AiProviders.Custom"/>。
    ///
    /// 用字符串而不是 enum：Jellyfin 的插件配置走 XML 序列化，
    /// 字符串在升级/回退时都不会因为枚举值缺失而炸掉。
    /// </summary>
    public string Provider { get; set; } = AiProviders.OpenCode;

    /// <summary>
    /// 自定义接口的根地址（不含 /chat/completions）。
    /// 只有 <see cref="Provider"/> 为 <see cref="AiProviders.Custom"/> 时才会用到；
    /// 内置供应商的地址由代码固定，用户不用也不能改。
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// 旧版的单一 API Key 字段，只用于迁移。
    ///
    /// 1.1 起每个供应商各有独立槽位（<see cref="OpenCodeApiKey"/> 等）；
    /// <see cref="MigrateLegacyCredentials"/> 把旧值搬进「当前供应商」的槽位后清空这里。
    /// 保留属性是为了旧 XML 仍能正常解析。
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>旧版的单一模型字段；语义同 <see cref="ApiKey"/>，只用于迁移。</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Open Code 的 API Key。切换供应商不会覆盖这一份。</summary>
    public string OpenCodeApiKey { get; set; } = string.Empty;

    /// <summary>Open Code 的模型名；留空时回落到 <see cref="AiProviders.DefaultOpenCodeModel"/>。</summary>
    public string OpenCodeModel { get; set; } = string.Empty;

    /// <summary>Command Code 的 API Key。与 Open Code / 自定义的 Key 互不影响。</summary>
    public string CommandCodeApiKey { get; set; } = string.Empty;

    /// <summary>Command Code 的模型名；留空时回落到 <see cref="AiProviders.DefaultCommandCodeModel"/>。</summary>
    public string CommandCodeModel { get; set; } = string.Empty;

    /// <summary>自定义供应商的 API Key。与两个内置供应商的 Key 互不影响。</summary>
    public string CustomApiKey { get; set; } = string.Empty;

    /// <summary>
    /// 自定义供应商的模型名。
    /// 自定义网关的模型名无法预知，留空就是留空（不塞一个不存在的默认值）。
    /// </summary>
    public string CustomModel { get; set; } = string.Empty;

    /// <summary>
    /// 是否用 AI 把演员名翻译成中文，并用中文名给合集命名。
    ///
    /// 演员本名（Person 名字）**不会被修改** —— 那是另一个刮削插件的职责。
    /// 这里只影响合集的显示名，例如「つばさ舞」的合集命名为「翼舞」。
    /// </summary>
    public bool TranslateNames { get; set; }

    /// <summary>
    /// 单次请求超时秒数。
    ///
    /// 默认 300 而不是 60：实测推理模型处理一批演员名，
    /// Open Code 网关要花 **100-150 秒**（推理模型会先想一大段）。
    /// 60 秒必然超时，而且超时请求不产生日志，很难排查。
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>是否选中了内置的 Open Code 供应商。</summary>
    [System.Xml.Serialization.XmlIgnore]
    public bool IsOpenCode => string.Equals(Provider, AiProviders.OpenCode, StringComparison.OrdinalIgnoreCase);

    /// <summary>是否选中了内置的 Command Code 供应商。</summary>
    [System.Xml.Serialization.XmlIgnore]
    public bool IsCommandCode => string.Equals(Provider, AiProviders.CommandCode, StringComparison.OrdinalIgnoreCase);

    /// <summary>是否选中了自定义供应商（含手改配置写错的未知值）。</summary>
    [System.Xml.Serialization.XmlIgnore]
    public bool IsCustom => !IsOpenCode && !IsCommandCode;

    /// <summary>
    /// 实际要用的接口根地址。
    ///
    /// 内置供应商的地址写死在代码里：用户选了内置供应商却把地址改坏，
    /// 报错会非常难查（现象是"连接不上"，而不是"地址填错了"）。
    /// </summary>
    public string ResolveBaseUrl()
    {
        if (IsOpenCode)
        {
            return AiProviders.OpenCodeBaseUrl;
        }

        if (IsCommandCode)
        {
            return AiProviders.CommandCodeBaseUrl;
        }

        return (BaseUrl ?? string.Empty).Trim();
    }

    /// <summary>
    /// 取当前供应商的 API Key。
    ///
    /// 槽位为空时回退到旧字段：未经 <see cref="MigrateLegacyCredentials"/> 的对象
    /// （单元测试、刚升级还没迁移）也不会突然变成无鉴权请求。
    /// </summary>
    /// <returns>当前供应商的 API Key。</returns>
    public string ResolveApiKey()
    {
        var key = IsOpenCode ? OpenCodeApiKey
            : IsCommandCode ? CommandCodeApiKey
            : CustomApiKey;

        return string.IsNullOrWhiteSpace(key) ? (ApiKey ?? string.Empty) : key;
    }

    /// <summary>
    /// 取当前供应商的模型名。
    ///
    /// 内置供应商在槽位为空时回落到各自的默认模型，切换过去不用先查文档；
    /// 自定义供应商保持为空 —— 没人能猜出对方网关上有哪些模型。
    /// </summary>
    /// <returns>当前供应商的模型名。</returns>
    public string ResolveModel()
    {
        var model = IsOpenCode ? OpenCodeModel
            : IsCommandCode ? CommandCodeModel
            : CustomModel;

        if (!string.IsNullOrWhiteSpace(model))
        {
            return model;
        }

        // 旧配置（迁移前）只有 Model 这一份值。
        if (!string.IsNullOrWhiteSpace(Model))
        {
            return Model;
        }

        return IsOpenCode ? AiProviders.DefaultOpenCodeModel
            : IsCommandCode ? AiProviders.DefaultCommandCodeModel
            : string.Empty;
    }

    /// <summary>
    /// 把旧版单一字段（<see cref="ApiKey"/> / <see cref="Model"/>）搬进
    /// 「当前选中的供应商」槽位，然后清空旧字段。
    ///
    /// 只在旧字段有值时执行，所以重复调用是空操作；槽位已有值时保留槽位值，
    /// 避免覆盖用户在新版里刚改过的内容。
    /// </summary>
    public void MigrateLegacyCredentials()
    {
        if (string.IsNullOrWhiteSpace(ApiKey) && string.IsNullOrWhiteSpace(Model))
        {
            return;
        }

        if (IsOpenCode)
        {
            OpenCodeApiKey = FirstNonEmpty(OpenCodeApiKey, ApiKey);
            OpenCodeModel = FirstNonEmpty(OpenCodeModel, Model);
        }
        else if (IsCommandCode)
        {
            CommandCodeApiKey = FirstNonEmpty(CommandCodeApiKey, ApiKey);
            CommandCodeModel = FirstNonEmpty(CommandCodeModel, Model);
        }
        else
        {
            CustomApiKey = FirstNonEmpty(CustomApiKey, ApiKey);
            CustomModel = FirstNonEmpty(CustomModel, Model);
        }

        ApiKey = string.Empty;
        Model = string.Empty;
    }

    private static string FirstNonEmpty(string? preferred, string? fallback)
    {
        return !string.IsNullOrWhiteSpace(preferred) ? preferred : (fallback ?? string.Empty);
    }

    /// <summary>
    /// 复制一份并替换超时。用于「测试连接」这种需要用更短超时的场景，
    /// 避免改动用户保存的真实配置。
    /// </summary>
    /// <param name="timeoutSeconds">新超时秒数。</param>
    /// <returns>副本。</returns>
    public AiOptions CloneWithTimeout(int timeoutSeconds)
    {
        return new AiOptions
        {
            Enabled = Enabled,
            Provider = Provider,
            BaseUrl = BaseUrl,
            ApiKey = ApiKey,
            Model = Model,
            OpenCodeApiKey = OpenCodeApiKey,
            OpenCodeModel = OpenCodeModel,
            CommandCodeApiKey = CommandCodeApiKey,
            CommandCodeModel = CommandCodeModel,
            CustomApiKey = CustomApiKey,
            CustomModel = CustomModel,
            TimeoutSeconds = timeoutSeconds,
            TranslateNames = TranslateNames
        };
    }
}

/// <summary>
/// 内置 AI 供应商的常量。
/// </summary>
public static class AiProviders
{
    /// <summary>内置的 Open Code 推理网关。</summary>
    public const string OpenCode = "OpenCode";

    /// <summary>内置的 Command Code Provider API（标准 OpenAI 兼容）。</summary>
    public const string CommandCode = "CommandCode";

    /// <summary>用户自填地址的 OpenAI 兼容接口。</summary>
    public const string Custom = "Custom";

    /// <summary>Open Code 网关的根地址。</summary>
    public const string OpenCodeBaseUrl = "https://opencode.ai/inference/go/openai/v1";

    /// <summary>Open Code 网关的默认模型。</summary>
    public const string DefaultOpenCodeModel = "deepseek-v4.1-flash";

    /// <summary>
    /// Command Code 的根地址。
    /// OpenAI 模型与开源模型都走 <c>/chat/completions</c>，鉴权用 Bearer。
    /// </summary>
    public const string CommandCodeBaseUrl = "https://api.commandcode.ai/provider/v1";

    /// <summary>
    /// Command Code 的默认模型。
    ///
    /// 模型名带厂商前缀（<c>deepseek/…</c>、<c>moonshotai/…</c>、<c>zai-org/…</c>）。
    /// 直接照搬 OpenCode 的 <c>deepseek-v4.1-flash</c> 会被判 <c>400 unsupported_model</c>。
    /// </summary>
    public const string DefaultCommandCodeModel = "deepseek/deepseek-v4.1-flash";

    /// <summary>
    /// 发给 Open Code 的会话 id。
    ///
    /// 必须是**固定值**：网关用它做路由与 prompt 缓存，每次换新 id 会丢掉缓存、
    /// 同一个演员列表重复推理；Thunder Subtitle 用的也是固定值。
    /// </summary>
    public const string SessionId = "jellyfin-av-collection";
}
