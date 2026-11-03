using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.AVCollection.Core;

/// <summary>
/// 一次同步的完整流程：扫描 → 规划 → 落库 → 校验 → 记录状态。
///
/// 为什么要有"校验轮"：
/// Jellyfin 自带一个 CollectionPostScanTask，会在媒体库扫描后清理"空合集"。
/// 我们的任务是先建合集、再往里加成员，两步之间有一条缝，扫描任务恰好插进来
/// 就会把刚建的合集当空壳删掉。现网脚本用「延时后重新核对 + 重试」解决，
/// 这里沿用同一套做法。
/// </summary>
public sealed class CollectionSyncJob
{
    private readonly ActorIndex _index;
    private readonly CollectionWriter _writer;
    private readonly RuntimeState _state;
    private readonly ILibraryManager _libraryManager;
    private readonly PluginLog _log;

    /// <summary>同一时刻只允许一个同步在跑。</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>初始化同步任务。</summary>
    /// <param name="index">演员扫描器。</param>
    /// <param name="writer">合集写入器。</param>
    /// <param name="state">运行期状态。</param>
    /// <param name="libraryManager">媒体库服务（删除旧合集用）。</param>
    /// <param name="log">日志。</param>
    public CollectionSyncJob(
        ActorIndex index,
        CollectionWriter writer,
        RuntimeState state,
        ILibraryManager libraryManager,
        PluginLog log)
    {
        _index = index;
        _writer = writer;
        _state = state;
        _libraryManager = libraryManager;
        _log = log;
    }

    /// <summary>
    /// 执行一次同步。
    /// </summary>
    /// <param name="dryRun">只计算不写库，用于设置页预览。</param>
    /// <param name="progress">进度回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>同步摘要。</returns>
    public async Task<SyncSummary> RunAsync(
        bool dryRun,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var summary = new SyncSummary();
        var config = Plugin.Current;

        if (!config.Enabled)
        {
            _log.Info("插件已禁用，跳过同步");
            summary.Skipped = "插件已禁用";
            return summary;
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunCoreAsync(config, dryRun, progress, cancellationToken).ConfigureAwait(false)
                   ?? summary;
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<SyncSummary> RunCoreAsync(
        PluginConfiguration config,
        bool dryRun,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var summary = new SyncSummary { DryRun = dryRun };

        progress?.Report(0);
        var libraries = _index.ResolveLibraries(config.LibraryIds);
        summary.LibraryCount = libraries.Count;
        _log.Info($"开始扫描 {libraries.Count} 个媒体库{(dryRun ? "（预览模式，不写库）" : string.Empty)}");

        var actors = _index.Scan(libraries, cancellationToken);
        summary.ActorCount = actors.Count;

        // 下限保护：媒体库扫描期间 People 会整体返回空。
        // 没有这道闸，一次"扫到 0 个演员"就会把库里所有合集清空。
        if (actors.Count < Math.Max(1, config.MinActorCount))
        {
            var message = $"只扫到 {actors.Count} 个演员（下限 {config.MinActorCount}），为避免误删合集已中止";
            _log.Warn(message);
            summary.Skipped = message;
            return summary;
        }

        progress?.Report(0.1);

        var plans = CollectionPlanner.BuildPlans(actors, config);
        summary.PlanCount = plans.Count;
        _log.Info($"演员 {actors.Count} 位，规划出 {plans.Count} 个合集");

        if (dryRun)
        {
            foreach (var plan in plans)
            {
                summary.Details.Add($"{(plan.IsMergeGroup ? "[合并] " : string.Empty)}{plan.DisplayName}：{plan.ItemIds.Count} 部（艺名 {string.Join(" / ", plan.Aliases)}）");
            }

            progress?.Report(1);
            return summary;
        }

        var claimed = new HashSet<Guid>();
        var absorbees = new List<BoxSet>();
        var index = 0;

        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;

            try
            {
                var outcome = await _writer
                    .ApplyAsync(plan, _state, claimed, absorbees, cancellationToken)
                    .ConfigureAwait(false);

                switch (outcome)
                {
                    case CollectionOutcome.Created:
                        summary.Created++;
                        break;
                    case CollectionOutcome.Synced:
                        summary.Synced++;
                        break;
                    case CollectionOutcome.Failed:
                        summary.Failed++;
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                summary.Failed++;
                _log.Error($"处理合集「{plan.DisplayName}」时出错", ex);
            }

            progress?.Report(0.1 + (0.75 * index / Math.Max(1, plans.Count)));
        }

        await RemoveAbsorbedAsync(absorbees, config, summary, cancellationToken).ConfigureAwait(false);
        await VerifyAsync(plans, config, summary, cancellationToken).ConfigureAwait(false);

        _state.MarkRun(
            $"合集 {summary.Created + summary.Synced} 个（新建 {summary.Created} / 同步 {summary.Synced}），" +
            $"演员 {summary.ActorCount} 位");
        _state.Save();

        progress?.Report(1);
        _log.Info($"完成：新建 {summary.Created}，同步 {summary.Synced}，失败 {summary.Failed}");

        return summary;
    }

    /// <summary>
    /// 删除被合并吸收的旧合集。默认开启，因为不删的话库里会同时存在
    /// 「葵」「小野夕子」「小野夕子 / 葵」三个合集，前两个都是单部影片的残壳。
    /// </summary>
    private Task RemoveAbsorbedAsync(
        List<BoxSet> absorbees,
        PluginConfiguration config,
        SyncSummary summary,
        CancellationToken cancellationToken)
    {
        if (!config.MergeRemoveOldCollections || absorbees.Count == 0)
        {
            return Task.CompletedTask;
        }

        foreach (var legacy in absorbees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // 不传 parent：交给 Jellyfin 自己解析，避免父级引用过期。
                _libraryManager.DeleteItem(legacy, new DeleteOptions());
                summary.Removed++;
                _log.Info($"已删除被合并的旧合集「{legacy.Name}」");
            }
            catch (Exception ex)
            {
                _log.Error($"删除旧合集「{legacy.Name}」失败", ex);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 延时核对成员是否齐全，缺了补一次。
    /// 这里只看"该在的不在"，不处理"多出来的" —— 多出来的成员由正常同步处理，
    /// 校验阶段做删除风险太高（比如用户手动往合集里塞了一部片）。
    /// </summary>
    private async Task VerifyAsync(
        IReadOnlyList<CollectionPlan> plans,
        PluginConfiguration config,
        SyncSummary summary,
        CancellationToken cancellationToken)
    {
        var rounds = Math.Max(0, config.VerifyRounds);
        if (rounds == 0)
        {
            return;
        }

        var delay = TimeSpan.FromSeconds(Math.Max(0, config.VerifyDelaySeconds));

        for (var round = 1; round <= rounds; round++)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            var broken = 0;
            foreach (var plan in plans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_state.TryGetCollection(plan.GroupId, out var collectionId))
                {
                    continue;
                }

                var have = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
                {
                    ParentId = collectionId,
                    Recursive = true
                }).Select(i => i.Id).ToHashSet();

                var missing = plan.ItemIds.Where(id => !have.Contains(id)).ToList();
                if (missing.Count == 0)
                {
                    continue;
                }

                broken++;
                try
                {
                    // 复用写入器的成员同步会顺带重算增删，这里直接补一次。
                    var boxSet = _libraryManager.GetItemById(collectionId) as BoxSet;
                    if (boxSet is null)
                    {
                        continue;
                    }

                    await _writer
                        .ApplyAsync(plan, _state, [], [], cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Error($"校验补齐「{plan.DisplayName}」失败", ex);
                }
            }

            if (broken == 0)
            {
                _log.Info($"校验第 {round} 轮：{plans.Count} 个合集全部正常");
                return;
            }

            summary.Repaired += broken;
            _log.Info($"校验第 {round} 轮：{broken} 个合集成员缺失，已补齐");
        }
    }
}

/// <summary>一次同步的统计结果。</summary>
public sealed class SyncSummary
{
    /// <summary>是否为预览模式。</summary>
    public bool DryRun { get; set; }

    /// <summary>参与扫描的媒体库数量。</summary>
    public int LibraryCount { get; set; }

    /// <summary>扫到的演员数量。</summary>
    public int ActorCount { get; set; }

    /// <summary>规划出的合集数量。</summary>
    public int PlanCount { get; set; }

    /// <summary>新建合集数。</summary>
    public int Created { get; set; }

    /// <summary>成员有变化并已同步的合集数。</summary>
    public int Synced { get; set; }

    /// <summary>被合并吸收并删除的旧合集数。</summary>
    public int Removed { get; set; }

    /// <summary>校验阶段补齐的合集数。</summary>
    public int Repaired { get; set; }

    /// <summary>失败数。</summary>
    public int Failed { get; set; }

    /// <summary>被跳过的原因（非空表示本次没有执行）。</summary>
    public string Skipped { get; set; } = string.Empty;

    /// <summary>预览模式下的逐条明细。</summary>
    public List<string> Details { get; } = [];

    /// <summary>生成一句中文摘要，供 API 与设置页展示。</summary>
    /// <returns>摘要文本。</returns>
    public string Describe()
    {
        if (!string.IsNullOrEmpty(Skipped))
        {
            return "已跳过：" + Skipped;
        }

        if (DryRun)
        {
            return $"预览：{LibraryCount} 个库 / {ActorCount} 位演员 / {PlanCount} 个合集";
        }

        return $"演员 {ActorCount} 位，合集 {PlanCount} 个：新建 {Created}，同步 {Synced}，删除旧合集 {Removed}，校验补齐 {Repaired}，失败 {Failed}";
    }
}
