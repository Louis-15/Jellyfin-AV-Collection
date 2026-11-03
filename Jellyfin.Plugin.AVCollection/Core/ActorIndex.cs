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
    /// 决定这次要扫哪些库：配置里指定了就用指定的，没指定就退回"所有电影库"。
    /// 这样用户装完插件什么都不配也能工作。
    /// </summary>
    /// <param name="configured">配置里的媒体库 Id。</param>
    /// <returns>实际要扫的库。</returns>
    public IReadOnlyList<Guid> ResolveLibraries(IEnumerable<Guid>? configured)
    {
        var explicitIds = (configured ?? []).Where(id => id != Guid.Empty).Distinct().ToList();
        if (explicitIds.Count > 0)
        {
            return explicitIds;
        }

        return _libraryManager.GetUserRootFolder()
            .Children
            .OfType<CollectionFolder>()
            .Where(f => f.CollectionType == CollectionType.movies)
            .Select(f => f.Id)
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

                    // 同一个演员理论上只有一个 Person Id；保留第一次看到的非空值即可。
                    actor.PersonId ??= person.Id == Guid.Empty ? null : person.Id;
                    actor.AddItem(item.Id, item.Name, libraryId);
                }
            }
        }

        return actors.Values
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .ToList();
    }
}
