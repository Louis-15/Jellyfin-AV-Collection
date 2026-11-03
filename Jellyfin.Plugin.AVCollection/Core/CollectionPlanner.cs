using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AVCollection.Core;

/// <summary>
/// 把「演员列表 + 用户配置的合并组」规划成一组「合集计划」。
///
/// 这个类刻意不依赖任何 Jellyfin 类型，只吃 <see cref="ActorInfo"/>，
/// 这样命名规则、排序稳定性、合并语义都能用普通单元测试覆盖。
/// </summary>
public static class CollectionPlanner
{
    /// <summary>
    /// 生成合集计划。
    /// </summary>
    /// <param name="actors">扫描到的演员（可能为空，调用方应先做下限保护）。</param>
    /// <param name="config">插件配置。</param>
    /// <param name="displayNames">
    /// 演员原名 → 合集显示名（中文译名）。
    /// 为 null 或查不到时直接用原名，所以"关掉翻译"和"某个名字没翻出来"走同一条路径。
    /// </param>
    /// <returns>按合集名排序的计划列表。</returns>
    public static IReadOnlyList<CollectionPlan> BuildPlans(
        IEnumerable<ActorInfo> actors,
        PluginConfiguration config,
        IReadOnlyDictionary<string, string>? displayNames = null)
    {
        ArgumentNullException.ThrowIfNull(actors);
        ArgumentNullException.ThrowIfNull(config);

        // 合集名优先用译名。返回空/空白一律回退原名，避免出现空合集名。
        string ResolveName(string original)
        {
            if (displayNames is not null
                && displayNames.TryGetValue(original, out var translated)
                && !string.IsNullOrWhiteSpace(translated))
            {
                return translated.Trim();
            }

            return original;
        }

        // 艺名 → 所属合并组。同名艺名出现在多个组里时以第一个为准，
        // 避免用户配重复之后出现"一个演员进两个合集"。
        var aliasToGroup = new Dictionary<string, ActorAliasGroup>(StringComparer.Ordinal);
        foreach (var group in config.AliasGroups ?? [])
        {
            if (group is null || string.IsNullOrWhiteSpace(group.Id))
            {
                continue;
            }

            foreach (var alias in group.Aliases ?? [])
            {
                var trimmed = (alias ?? string.Empty).Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                aliasToGroup.TryAdd(trimmed, group);
            }
        }

        var buckets = new Dictionary<string, Bucket>(StringComparer.Ordinal);
        foreach (var actor in actors)
        {
            if (actor is null || string.IsNullOrWhiteSpace(actor.Name))
            {
                continue;
            }

            // 没有作品的演员不参与：否则会建出一堆空合集，
            // 而 Jellyfin 的 CollectionPostScanTask 随后又会把空合集清掉，白白刷日志。
            if (actor.ItemIds.Count == 0)
            {
                continue;
            }

            var name = actor.Name.Trim();
            if (aliasToGroup.TryGetValue(name, out var group))
            {
                var key = "group:" + group.Id;
                if (!buckets.TryGetValue(key, out var bucket))
                {
                    bucket = new Bucket(key, group);
                    buckets[key] = bucket;
                }

                bucket.Actors.Add(actor);
            }
            else
            {
                var key = "actor:" + name;
                if (!buckets.TryGetValue(key, out var bucket))
                {
                    bucket = new Bucket(key, null);
                    buckets[key] = bucket;
                }

                bucket.Actors.Add(actor);
            }
        }

        var maxAliases = Math.Max(1, config.MaxAliasesInName);
        var plans = new List<CollectionPlan>(buckets.Count);

        foreach (var bucket in buckets.Values)
        {
            // 作品数从多到少；数量相同时按名字排序，保证每次运行结果一致。
            // 名字顺序一旦不稳定，合集名就会来回变，Jellyfin 里会出现反复改名。
            var ranked = bucket.Actors
                .OrderByDescending(a => a.ItemIds.Count)
                .ThenBy(a => a.Name, StringComparer.Ordinal)
                .ToList();

            if (ranked.Count == 0)
            {
                continue;
            }

            var manualName = bucket.Group?.DisplayName?.Trim();
            var displayName = string.IsNullOrEmpty(manualName)
                ? string.Join(" / ", ranked.Take(maxAliases).Select(a => ResolveName(a.Name)))
                : manualName!;

            var items = new HashSet<Guid>();
            var libraryCounts = new Dictionary<Guid, int>();
            foreach (var actor in ranked)
            {
                items.UnionWith(actor.ItemIds);
                foreach (var itemId in actor.ItemIds)
                {
                    if (actor.ItemLibraries.TryGetValue(itemId, out var libId) && libId != Guid.Empty)
                    {
                        libraryCounts[libId] = libraryCounts.GetValueOrDefault(libId) + 1;
                    }
                }
            }

            if (items.Count == 0)
            {
                continue;
            }

            // 合集挂到"贡献影片最多"的那个库下面；数量相同按 Id 排序，保证结果稳定。
            var parentLibraryId = libraryCounts.Count == 0
                ? (Guid?)null
                : libraryCounts
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key)
                    .First()
                    .Key;

            var posterAlias = bucket.Group?.PosterAlias?.Trim();
            if (string.IsNullOrEmpty(posterAlias) ||
                !ranked.Any(a => string.Equals(a.Name, posterAlias, StringComparison.Ordinal)))
            {
                posterAlias = ranked[0].Name;
            }

            // 封面用哪个艺名的头像：按名字找到那一条 ActorInfo 的 PersonId。
            var posterPersonId = ranked
                .FirstOrDefault(a => string.Equals(a.Name, posterAlias, StringComparison.Ordinal))
                ?.PersonId;

            plans.Add(new CollectionPlan
            {
                GroupId = bucket.Key,
                DisplayName = displayName,
                Aliases = ranked.Select(a => a.Name).ToList(),
                ItemIds = items.OrderBy(id => id).ToList(),
                PosterAlias = posterAlias!,

                // 只有"真正并到了一起"（≥2 个艺名）才算合并组。
                // 用户可能把同一个艺名配进两个组，先到先得之后另一个组可能只剩一个艺名；
                // 这时若仍标记为合并组，后续会去删除那个演员原本的合集，属于误伤。
                IsMergeGroup = bucket.Group is not null && ranked.Count >= 2,
                PersonId = bucket.Group is null && ranked.Count < 2 ? ranked[0].PersonId : null,
                PosterPersonId = posterPersonId,
                ParentLibraryId = parentLibraryId
            });
        }

        return plans
            .OrderBy(p => p.DisplayName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 找出配置里那些"一个艺名在库里都找不到"的合并组。
    /// 设置页用它提示用户：可能是名字拼错，或者那个艺名已经改名了。
    /// </summary>
    /// <param name="actors">扫描到的演员。</param>
    /// <param name="config">插件配置。</param>
    /// <returns>失效的组 Id 集合。</returns>
    public static IReadOnlyCollection<string> FindStaleGroups(
        IEnumerable<ActorInfo> actors,
        PluginConfiguration config)
    {
        var present = new HashSet<string>(
            actors.Where(a => a.ItemIds.Count > 0).Select(a => a.Name),
            StringComparer.Ordinal);

        var stale = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in config.AliasGroups ?? [])
        {
            if (group is null || string.IsNullOrWhiteSpace(group.Id))
            {
                continue;
            }

            var any = (group.Aliases ?? []).Any(a => present.Contains((a ?? string.Empty).Trim()));
            if (!any)
            {
                stale.Add(group.Id);
            }
        }

        return stale;
    }

    private sealed class Bucket
    {
        public Bucket(string key, ActorAliasGroup? group)
        {
            Key = key;
            Group = group;
        }

        public string Key { get; }

        public ActorAliasGroup? Group { get; }

        public List<ActorInfo> Actors { get; } = [];
    }
}
