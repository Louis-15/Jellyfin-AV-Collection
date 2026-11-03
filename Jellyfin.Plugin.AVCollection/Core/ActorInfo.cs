using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AVCollection.Core;

/// <summary>
/// 扫描阶段得到的一个演员及其作品。
/// </summary>
public sealed class ActorInfo
{
    /// <summary>初始化演员条目。</summary>
    /// <param name="name">演员名（Jellyfin 里的 Person 名字，也就是"艺名"）。</param>
    public ActorInfo(string name)
    {
        Name = name;
    }

    /// <summary>演员名 / 艺名。</summary>
    public string Name { get; }

    /// <summary>
    /// Jellyfin 里该演员的 Person 条目 Id。
    /// 用它去读头像；同一个名字在多部片子里应当指向同一个 Id。
    /// 扫描期间 People 可能返回空，所以允许为 null。
    /// </summary>
    public Guid? PersonId { get; set; }

    /// <summary>该演员在目标库里出现的影片 Id 集合。</summary>
    public HashSet<Guid> ItemIds { get; } = [];

    /// <summary>
    /// 影片 Id → 所属媒体库 Id。
    /// 建合集时要指定 parentId，合集才会出现在用户习惯的那个库下面
    /// （现网脚本一直是这么做的，不指定会跑到全局 Collections 里）。
    /// </summary>
    public Dictionary<Guid, Guid> ItemLibraries { get; } = [];

    /// <summary>
    /// 影片标题样本。只用于把演员列表发给 AI 做"是否同一人"的判断，
    /// 不参与合集构建逻辑。
    /// </summary>
    public List<string> SampleTitles { get; } = [];

    /// <summary>
    /// 记一部影片。同一条目重复出现时会自动去重。
    /// </summary>
    /// <param name="itemId">影片 Id。</param>
    /// <param name="title">影片标题，用于 AI 样本。</param>
    /// <param name="libraryId">影片所属媒体库 Id。</param>
    /// <param name="sampleLimit">样本标题最多保留多少条。</param>
    public void AddItem(Guid itemId, string? title, Guid libraryId, int sampleLimit = 3)
    {
        if (!ItemIds.Add(itemId))
        {
            return;
        }

        if (libraryId != Guid.Empty)
        {
            ItemLibraries[itemId] = libraryId;
        }

        if (!string.IsNullOrWhiteSpace(title) && SampleTitles.Count < sampleLimit)
        {
            // 只留开头一段：AV 标题经常长到几百字，整段塞进 prompt 纯属浪费 token。
            var text = title!.Trim();
            if (text.Length > 40)
            {
                text = text[..40] + "…";
            }

            SampleTitles.Add(text);
        }
    }
}

/// <summary>
/// 一个"合集"的构建计划。可能来自单个演员，也可能来自多个艺名的合并组。
/// </summary>
public sealed class CollectionPlan
{
    /// <summary>
    /// 稳定分组键：合并组用 <c>group:{组Id}</c>，独立演员用 <c>actor:{演员名}</c>。
    /// 运行期状态按这个键记录目标合集 Id —— 绝不能改用合集名当键，
    /// 因为合并组的名字会随作品数变化（"葵 / 小野夕子" ←→ "小野夕子 / 葵"）。
    /// </summary>
    public required string GroupId { get; init; }

    /// <summary>期望的合集名。</summary>
    public required string DisplayName { get; init; }

    /// <summary>组内实际有作品的艺名，按作品数从多到少排列（同数量按名字排序保证稳定）。</summary>
    public required IReadOnlyList<string> Aliases { get; init; }

    /// <summary>合集应当包含的影片。</summary>
    public required IReadOnlyList<Guid> ItemIds { get; init; }

    /// <summary>用哪个艺名的头像做封面。</summary>
    public required string PosterAlias { get; init; }

    /// <summary>该计划是否来自用户配置的合并组。</summary>
    public bool IsMergeGroup { get; init; }

    /// <summary>该计划对应的 Person Id（独立演员时有值，合并组为空）。</summary>
    public Guid? PersonId { get; init; }

    /// <summary>
    /// 用来取封面头像的 Person Id（按 <see cref="PosterAlias"/> 查到的那一个）。
    /// 合并组里 <see cref="PersonId"/> 为空，只能用这个。
    /// </summary>
    public Guid? PosterPersonId { get; init; }

    /// <summary>
    /// 新建合集时要挂到的媒体库 Id。
    /// 取组内影片数最多的那个库；为 null 表示沿用 Jellyfin 的全局 Collections 位置。
    /// </summary>
    public Guid? ParentLibraryId { get; init; }
}
