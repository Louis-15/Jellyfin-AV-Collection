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
    /// AI 供应商。<see cref="AiProviders.OpenCode"/>（内置）或 <see cref="AiProviders.Custom"/>。
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

    /// <summary>API Key。只保存在服务器插件配置里，绝不写进日志。</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>模型名。</summary>
    public string Model { get; set; } = AiProviders.DefaultOpenCodeModel;

    /// <summary>
    /// 单次请求超时秒数。
    ///
    /// 默认 300 而不是 60：实测用 <c>deepseek-v4.1-flash</c> 判断 20 位演员，
    /// Open Code 网关要花 **149 秒**（推理模型会先想一大段）。
    /// 60 秒必然超时，而且超时请求不产生日志，很难排查。
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>一次送审多少个演员。列表太长会撑爆上下文，默认 120。</summary>
    public int MaxActorsPerRequest { get; set; } = 120;

    /// <summary>是否选中了内置的 Open Code 供应商。</summary>
    [System.Xml.Serialization.XmlIgnore]
    public bool IsOpenCode => string.Equals(Provider, AiProviders.OpenCode, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 实际要用的接口根地址。
    ///
    /// 内置供应商的地址写死在代码里：用户选了 Open Code 却把地址改坏，
    /// 报错会非常难查（现象是"连接不上"，而不是"地址填错了"）。
    /// </summary>
    public string ResolveBaseUrl()
    {
        return IsOpenCode
            ? AiProviders.OpenCodeBaseUrl
            : (BaseUrl ?? string.Empty).Trim();
    }
}

/// <summary>
/// 内置 AI 供应商的常量。
/// </summary>
public static class AiProviders
{
    /// <summary>内置的 Open Code 推理网关。</summary>
    public const string OpenCode = "OpenCode";

    /// <summary>用户自填地址的 OpenAI 兼容接口。</summary>
    public const string Custom = "Custom";

    /// <summary>Open Code 网关的根地址。</summary>
    public const string OpenCodeBaseUrl = "https://opencode.ai/inference/go/openai/v1";

    /// <summary>Open Code 网关的默认模型。</summary>
    public const string DefaultOpenCodeModel = "deepseek-v4.1-flash";

    /// <summary>
    /// 发给 Open Code 的会话 id。
    ///
    /// 必须是**固定值**：网关用它做路由与 prompt 缓存，每次换新 id 会丢掉缓存、
    /// 同一个演员列表重复推理；Thunder Subtitle 用的也是固定值。
    /// </summary>
    public const string SessionId = "jellyfin-av-collection";
}
