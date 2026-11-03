using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;

namespace Jellyfin.Plugin.AVCollection.Core;

/// <summary>
/// 单个合集的写操作：认领 / 新建 / 改名 / 同步成员 / 换封面 / 清理误匹配元数据。
///
/// 把"一个合集"的操作集中在这里，是为了让 <see cref="CollectionSyncJob"/> 只负责编排：
/// 逐个计划调用 <see cref="ApplyAsync"/>，失败一个不影响其余。
/// </summary>
public sealed class CollectionWriter
{
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly IProviderManager _providerManager;
    private readonly PluginLog _log;

    /// <summary>初始化写入器。</summary>
    /// <param name="libraryManager">媒体库服务。</param>
    /// <param name="collectionManager">合集服务。</param>
    /// <param name="providerManager">元数据 / 图片服务。</param>
    /// <param name="log">日志。</param>
    public CollectionWriter(
        ILibraryManager libraryManager,
        ICollectionManager collectionManager,
        IProviderManager providerManager,
        PluginLog log)
    {
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _providerManager = providerManager;
        _log = log;
    }

    /// <summary>
    /// 读出当前 Jellyfin 里所有合集，建立「合集名 → 合集」索引。
    ///
    /// 刻意一次性全量查询而不是按名字搜索：Jellyfin 的 SearchTerm 是模糊匹配，
    /// 演员名之间又常有一两个字相同（葵 / 小野夕子），模糊匹配会认领错合集。
    /// 合集总量通常只有几十到几百个，全量拉回来反而更快也更准。
    /// </summary>
    /// <returns>按名字精确索引的合集。</returns>
    public Dictionary<string, BoxSet> LoadCollectionsByName()
    {
        var result = new Dictionary<string, BoxSet>(StringComparer.Ordinal);
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.BoxSet]
        });

        foreach (var boxSet in items.OfType<BoxSet>())
        {
            var name = (boxSet.Name ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                continue;
            }

            // 重名合集只保留第一个，其余的不管 —— 交给用户自己清理，
            // 插件默默删合集会吓到人。
            result.TryAdd(name, boxSet);
        }

        return result;
    }

    /// <summary>
    /// 把一个计划落到 Jellyfin 里。
    /// </summary>
    /// <param name="plan">合集计划。</param>
    /// <param name="state">运行期状态，用于复用上次的合集 Id。</param>
    /// <param name="claimedIds">本次运行已经认领的合集 Id，防止两个计划抢同一个合集。</param>
    /// <param name="absorbees">
    /// 输出参数：被本次合并吸收掉的旧合集（成员已并走，调用方决定是否删除）。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>处理结果。</returns>
    public async Task<CollectionOutcome> ApplyAsync(
        CollectionPlan plan,
        RuntimeState state,
        HashSet<Guid> claimedIds,
        List<BoxSet> absorbees,
        CancellationToken cancellationToken)
    {
        var boxSet = ResolveTarget(plan, state, claimedIds);
        var created = false;

        if (boxSet is null)
        {
            boxSet = await CreateAsync(plan, cancellationToken).ConfigureAwait(false);
            if (boxSet is null)
            {
                return CollectionOutcome.Failed;
            }

            created = true;
        }

        claimedIds.Add(boxSet.Id);
        state.SetCollection(plan.GroupId, boxSet.Id);

        // 合并场景：组内其它艺名原来各自的合集，成员并过来之后必须删掉，
        // 否则库里会同时出现「葵」「小野夕子」「小野夕子 / 葵」三个合集。
        CollectAbsorbees(plan, boxSet, claimedIds, absorbees, state);

        var added = await SyncMembersAsync(boxSet, plan.ItemIds, cancellationToken).ConfigureAwait(false);
        await RenameIfNeededAsync(boxSet, plan.DisplayName, cancellationToken).ConfigureAwait(false);

        if (Plugin.Current.SetPosterFromActor)
        {
            await EnsurePosterAsync(boxSet, plan.PosterAlias, plan.PosterPersonId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (Plugin.Current.ClearWrongMetadata)
        {
            await ClearWrongMetadataAsync(boxSet, cancellationToken).ConfigureAwait(false);
        }

        if (created)
        {
            _log.Info($"已新建合集「{plan.DisplayName}」（{plan.ItemIds.Count} 部）");
            return CollectionOutcome.Created;
        }

        return added ? CollectionOutcome.Synced : CollectionOutcome.Unchanged;
    }

    /// <summary>
    /// 决定这次要写哪个合集。优先级：
    /// 1. 状态里记录过、而且现在仍然存在的（最稳，名字变了也不会丢）；
    /// 2. 名字已经等于目标名的；
    /// 3. 组里某个艺名的旧合集（接管现网脚本按演员名建的那一批）。
    /// </summary>
    private BoxSet? ResolveTarget(CollectionPlan plan, RuntimeState state, HashSet<Guid> claimedIds)
    {
        if (state.TryGetCollection(plan.GroupId, out var knownId))
        {
            var known = _libraryManager.GetItemById(knownId) as BoxSet;
            if (known is not null && !claimedIds.Contains(known.Id))
            {
                return known;
            }

            // 合集被用户在 Jellyfin 里删掉了，丢掉记录重新认领。
            state.RemoveCollection(plan.GroupId);
        }

        var byName = LoadCollectionsByName();

        if (byName.TryGetValue(plan.DisplayName, out var exact) && !claimedIds.Contains(exact.Id))
        {
            return exact;
        }

        // 旧合集认领：按"作品数从多到少"依次匹配艺名。
        // 这样合并组会优先接管最活跃的那个艺名的合集，保留它已有的封面和历史。
        foreach (var alias in plan.Aliases)
        {
            if (byName.TryGetValue(alias, out var legacy) && !claimedIds.Contains(legacy.Id))
            {
                return legacy;
            }
        }

        return null;
    }

    private async Task<BoxSet?> CreateAsync(CollectionPlan plan, CancellationToken cancellationToken)
    {
        try
        {
            var options = new CollectionCreationOptions
            {
                Name = plan.DisplayName,
                ParentId = plan.ParentLibraryId,
                ItemIdList = plan.ItemIds.Select(id => id.ToString("N", System.Globalization.CultureInfo.InvariantCulture)).ToList()
            };

            var boxSet = await _collectionManager.CreateCollectionAsync(options).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return boxSet;
        }
        catch (Exception ex)
        {
            _log.Error($"创建合集「{plan.DisplayName}」失败", ex);
            return null;
        }
    }

    private void CollectAbsorbees(
        CollectionPlan plan,
        BoxSet target,
        HashSet<Guid> claimedIds,
        List<BoxSet> absorbees,
        RuntimeState state)
    {
        if (!plan.IsMergeGroup)
        {
            return;
        }

        var byName = LoadCollectionsByName();
        foreach (var alias in plan.Aliases)
        {
            if (!byName.TryGetValue(alias, out var legacy))
            {
                continue;
            }

            if (legacy.Id == target.Id || claimedIds.Contains(legacy.Id))
            {
                continue;
            }

            claimedIds.Add(legacy.Id);
            absorbees.Add(legacy);

            // 旧合集的成员也被并进目标合集了，状态里对应的记录作废。
            state.RemoveCollection("actor:" + alias);
        }
    }

    private async Task<bool> SyncMembersAsync(
        BoxSet boxSet,
        IReadOnlyList<Guid> want,
        CancellationToken cancellationToken)
    {
        var have = _libraryManager.GetItemList(new InternalItemsQuery
        {
            ParentId = boxSet.Id,
            Recursive = true
        }).Select(i => i.Id).ToHashSet();

        var add = want.Where(id => !have.Contains(id)).ToList();
        var remove = have.Where(id => !want.Contains(id)).ToList();

        if (add.Count == 0 && remove.Count == 0)
        {
            return false;
        }

        try
        {
            if (add.Count > 0)
            {
                await _collectionManager.AddToCollectionAsync(boxSet.Id, add).ConfigureAwait(false);
            }

            if (remove.Count > 0)
            {
                await _collectionManager.RemoveFromCollectionAsync(boxSet.Id, remove).ConfigureAwait(false);
            }

            _log.Info($"合集「{boxSet.Name}」成员同步：+{add.Count} -{remove.Count}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"合集「{boxSet.Name}」成员同步失败", ex);
            return false;
        }
    }

    private async Task RenameIfNeededAsync(
        BoxSet boxSet,
        string expectedName,
        CancellationToken cancellationToken)
    {
        var current = (boxSet.Name ?? string.Empty).Trim();
        if (string.Equals(current, expectedName, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            _log.Info($"合集改名：「{current}」→「{expectedName}」");
            boxSet.Name = expectedName;
            await _libraryManager
                .UpdateItemAsync(boxSet, boxSet.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error($"合集「{expectedName}」改名失败", ex);
        }
    }

    private async Task EnsurePosterAsync(
        BoxSet boxSet,
        string posterAlias,
        Guid? posterPersonId,
        CancellationToken cancellationToken)
    {
        var personId = posterPersonId ?? FindPersonIdByName(posterAlias);
        if (personId is null)
        {
            _log.Warn($"「{posterAlias}」没有 Person 记录，跳过封面");
            return;
        }

        var person = _libraryManager.GetItemById(personId.Value);
        var source = person?.ImageInfos?.FirstOrDefault(i => i.Type == ImageType.Primary);
        if (source is null || string.IsNullOrEmpty(source.Path) || !File.Exists(source.Path))
        {
            _log.Info($"「{posterAlias}」没有头像，跳过封面");
            return;
        }

        // 幂等：封面已经是同一张图就不重复上传，否则每次同步都会写一次磁盘 + 触发一次图片处理。
        var current = boxSet.ImageInfos?.FirstOrDefault(i => i.Type == ImageType.Primary);
        if (current is not null && !string.IsNullOrEmpty(current.Path) && File.Exists(current.Path)
            && Md5Equals(current.Path, source.Path))
        {
            return;
        }

        try
        {
            await using var stream = File.OpenRead(source.Path);
            var mime = GuessMimeType(source.Path);
            await _providerManager
                .SaveImage(boxSet, stream, mime, ImageType.Primary, 0, cancellationToken)
                .ConfigureAwait(false);
            _log.Info($"「{boxSet.Name}」封面已设为「{posterAlias}」的头像");
        }
        catch (Exception ex)
        {
            _log.Error($"「{boxSet.Name}」封面上传失败", ex);
        }
    }

    /// <summary>
    /// 只按扩展名判断图片类型。
    /// 不引 Jellyfin 的 MimeTypes 工具类：12.2 里它的位置不稳定，
    /// 而演员头像实际只有 jpg / png / webp 三种。
    /// </summary>
    private static string GuessMimeType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".tbn" => "image/jpeg",
            _ => "image/jpeg"
        };
    }

    /// <summary>
    /// 用演员名反查 Person Id。只在计划里没带 PersonId 时兜底
    /// （例如合并组配置了库里当前没作品的艺名）。
    /// </summary>
    private Guid? FindPersonIdByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var people = _libraryManager.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Person]
        });

        return people.FirstOrDefault(p =>
            string.Equals((p.Name ?? string.Empty).Trim(), name.Trim(), StringComparison.Ordinal))?.Id;
    }

    private async Task ClearWrongMetadataAsync(BoxSet boxSet, CancellationToken cancellationToken)
    {
        var providerIds = boxSet.ProviderIds;
        var hasProviderIds = providerIds is not null && providerIds.Count > 0;
        var hasText = !string.IsNullOrWhiteSpace(boxSet.Overview)
                      || boxSet.PremiereDate is not null
                      || boxSet.ProductionYear is not null;

        if (!hasProviderIds && !hasText)
        {
            return;
        }

        // 演员合集本来就不是 TMDB 里的真实作品。Jellyfin / MetaTube 偶尔会把
        // 「かすみ」匹配成《真田女忍者霞传》，于是合集有了简介、首播日期和背景图，
        // 在详情页看起来像一部电影。这里统一清掉，只留名字和封面。
        try
        {
            if (hasProviderIds)
            {
                _log.Info($"「{boxSet.Name}」清除误匹配元数据：{string.Join(", ", providerIds!.Keys)}");
                providerIds!.Clear();
            }

            boxSet.Overview = null;
            boxSet.PremiereDate = null;
            boxSet.ProductionYear = null;
            boxSet.OfficialRating = null;

            foreach (var extra in new[] { ImageType.Backdrop, ImageType.Banner, ImageType.Logo, ImageType.Thumb })
            {
                var image = boxSet.ImageInfos?.FirstOrDefault(i => i.Type == extra);
                if (image is not null)
                {
                    // Jellyfin 12 删图走 BaseItem 自己的 DeleteImageAsync，
                    // IProviderManager 只保留了 SaveImage。
                    await boxSet.DeleteImageAsync(extra, 0).ConfigureAwait(false);
                }
            }

            await _libraryManager
                .UpdateItemAsync(boxSet, boxSet.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error($"「{boxSet.Name}」清理误匹配元数据失败", ex);
        }
    }

    private static bool Md5Equals(string left, string right)
    {
        try
        {
            using var a = File.OpenRead(left);
            using var b = File.OpenRead(right);
            using var md5 = System.Security.Cryptography.MD5.Create();
            var ha = md5.ComputeHash(a);
            var hb = md5.ComputeHash(b);
            return System.Linq.Enumerable.SequenceEqual(ha, hb);
        }
        catch (IOException)
        {
            return false;
        }
    }
}

/// <summary>单个合集的同步结果。</summary>
public enum CollectionOutcome
{
    /// <summary>什么都没变。</summary>
    Unchanged,

    /// <summary>新建了合集。</summary>
    Created,

    /// <summary>合集已存在，成员有增减。</summary>
    Synced,

    /// <summary>处理失败。</summary>
    Failed
}
