using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.AVCollection.Core;

/// <summary>
/// 从 Jellyfin 媒体库里扫出「演员 → 作品」的映射。
/// </summary>
public sealed class ActorIndex
{
    private readonly ILibraryManager _libraryManager;
    private readonly IPeopleRepository _peopleRepository;

    /// <summary>初始化扫描器。</summary>
    /// <param name="libraryManager">Jellyfin 媒体库服务。</param>
    /// <param name="peopleRepository">演职员仓库（Jellyfin 12 起 People 不再挂在 BaseItem 上）。</param>
    public ActorIndex(ILibraryManager libraryManager, IPeopleRepository peopleRepository)
    {
        _libraryManager = libraryManager;
        _peopleRepository = peopleRepository;
    }

    /// <summary>
    /// 决定这次要扫哪些库。
    ///
    /// 刻意**不做**"没配就扫所有电影库"的回退：
    /// 一个演员合集插件去给整个「电影」库的几百位演员各建一个合集是灾难性的，
    /// 而且很难一键撤销。没配置就什么都不做，由设置页提示用户先选库。
    /// </summary>
    /// <param name="configured">配置里的媒体库 Id。</param>
    /// <returns>实际要扫的库；未配置时为空。</returns>
    public IReadOnlyList<Guid> ResolveLibraries(IEnumerable<Guid>? configured)
    {
        return (configured ?? [])
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// 扫描所有目标库。
    /// </summary>
    /// <param name="libraryIds">要扫的库。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>演员列表（按名字排序）。</returns>
    public IReadOnlyList<ActorInfo> Scan(
        IReadOnlyList<Guid> libraryIds,
        CancellationToken cancellationToken)
    {
        var actors = new Dictionary<string, ActorInfo>(StringComparer.Ordinal);

        foreach (var libraryId in libraryIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Movie],

                // 只扫这个库自身的影片；不加这个限制会把别的库也扫进来。
                ParentId = libraryId,

                // 关键：不能把合集内的影片折叠掉。
                // 折叠后"已进合集的影片"会从结果里消失，第二次运行就会以为演员没作品，
                // 把合集清空或删掉。现网脚本踩过同一个坑。
                CollapseBoxSetItems = false
            });

            if (items.Count == 0)
            {
                continue;
            }

            var people = _peopleRepository.GetPeopleByItems(items.Select(i => i.Id).ToList());

            foreach (var item in items)
            {
                // Jellyfin 12 起 BaseItem 上没有 People 属性，必须走演职员仓库。
                // GetPeopleByItems 是批量接口：一次拿到整批影片的演员，
                // 逐条查询会变成 N+1，大库上会慢到没法用。
                if (!people.TryGetValue(item.Id, out var cast) || cast is null || cast.Count == 0)
                {
                    // 媒体库扫描期间演职员可能整个为空。这里不报错，
                    // 由调用方用 MinActorCount 做整体保护。
                    continue;
                }

                foreach (var person in cast)
                {
                    if (person.Type != PersonKind.Actor)
                    {
                        continue;
                    }

                    var name = (person.Name ?? string.Empty).Trim();
                    if (name.Length == 0)
                    {
                        continue;
                    }

                    if (!actors.TryGetValue(name, out var actor))
                    {
                        actor = new ActorInfo(name);
                        actors[name] = actor;
                    }

                    // 这里不要读 person.Id —— 见 Scan 末尾的说明。
                    actor.AddItem(item.Id, item.Name, libraryId);
                }
            }
        }

        ResolvePersonIds(actors);

        return actors.Values
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 只扫描指定演员名下的作品。
    ///
    /// 「实时入库」不能用全库扫描：一部新片进来只需要更新它涉及的一两位演员，
    /// 把整个库重扫一遍既慢又会牵连无关合集。这里用
    /// <see cref="InternalItemsQuery.Person"/> 让数据库按演员名过滤。
    /// </summary>
    /// <param name="actorNames">要扫描的演员名（Jellyfin 里的 Person 名字）。</param>
    /// <param name="libraryIds">要扫的媒体库。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>这些演员及其作品；按名字排序。</returns>
    public IReadOnlyList<ActorInfo> ScanActors(
        IReadOnlyCollection<string> actorNames,
        IReadOnlyList<Guid> libraryIds,
        CancellationToken cancellationToken)
    {
        var wanted = actorNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var actors = new Dictionary<string, ActorInfo>(StringComparer.Ordinal);

        if (wanted.Count == 0)
        {
            return [];
        }

        foreach (var libraryId in libraryIds)
        {
            foreach (var name in wanted)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // InternalItemsQuery 只支持单个 Person 名，逐个查即可：
                // 实时流程涉及的演员通常只有一两位。
                var items = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    IncludeItemTypes = [BaseItemKind.Movie],
                    ParentId = libraryId,
                    CollapseBoxSetItems = false,
                    Person = name
                });

                if (items.Count == 0)
                {
                    continue;
                }

                // 二次核实：确认这些片子在 People 里确实挂着这个演员。
                // 把 A 的片子塞进 B 的合集是不可接受的错误，多花一次批量查询换确定性。
                var people = _peopleRepository.GetPeopleByItems(items.Select(i => i.Id).ToList());

                var verified = items
                    .Where(item => people.TryGetValue(item.Id, out var cast)
                                   && cast is not null
                                   && cast.Any(p => p.Type == PersonKind.Actor
                                                    && string.Equals((p.Name ?? string.Empty).Trim(), name, StringComparison.Ordinal)))
                    .ToList();

                if (verified.Count == 0)
                {
                    continue;
                }

                if (!actors.TryGetValue(name, out var actor))
                {
                    actor = new ActorInfo(name);
                    actors[name] = actor;
                }

                foreach (var item in verified)
                {
                    actor.AddItem(item.Id, item.Name, libraryId);
                }
            }
        }

        ResolvePersonIds(actors);

        return actors.Values
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 按演员名解析真正的 Person 条目 Id。
    ///
    /// 为什么不能直接用 <c>PersonInfo.Id</c>：
    /// <c>IPeopleRepository.GetPeopleByItems</c> 返回的 Id 并不是 Person 这条
    /// BaseItem 的 Id（实测同一个演员返回一个稳定但查不到任何条目的 GUID）。
    /// 用它去 <c>GetItemById</c> 永远是 null，于是每位演员都被判定"没有头像"，
    /// 合集永远设不上封面 —— 1.0.0.6 之前的版本就是这个症状。
    ///
    /// 这里一次性把 Person 条目全取回来按名字索引：
    /// 一次查询换 N 次解析，比逐个按名字搜便宜得多。
    /// </summary>
    /// <param name="actors">已扫到的演员（按名字索引）。</param>
    private void ResolvePersonIds(Dictionary<string, ActorInfo> actors)
    {
        var map = new Dictionary<string, Guid>(StringComparer.Ordinal);

        try
        {
            var people = _libraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Person]
            });

            foreach (var person in people)
            {
                var name = (person.Name ?? string.Empty).Trim();
                if (name.Length == 0)
                {
                    continue;
                }

                // 同名演员理论上指向同一个 Person 条目；真有重名时保留第一个，
                // 与"合集按名字切分"的语义一致。
                map.TryAdd(name, person.Id);
            }
        }
        catch (Exception)
        {
            // Person 条目查不到只影响封面，不该让整次同步失败。
            map.Clear();
        }

        var resolved = 0;
        foreach (var actor in actors.Values)
        {
            if (map.TryGetValue(actor.Name, out var personId))
            {
                actor.PersonId = personId;
                resolved++;
            }
            else
            {
                actor.PersonId = null;
            }
        }

        _resolvedPersonCount = resolved;
    }

    private int _resolvedPersonCount;

    /// <summary>
    /// 最近一次扫描里能解析到 Person 条目的演员数量（供调用方打日志，
    /// 数量为 0 通常意味着 Person 条目还没生成出来）。
    /// </summary>
    public int ResolvedPersonCount => _resolvedPersonCount;
}
