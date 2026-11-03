using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.AVCollection.Core;

/// <summary>
/// 实时入库监听：发现「刚入库且已刮削完成」的影片，只把涉及的演员合集更新一遍。
///
/// 为什么做成插件内的后台任务，而不是外部脚本 + 定时器：
/// 1. 触发点在 Jellyfin 进程内，不存在容器内外路径 / 权限差异；
/// 2. 插件停用即停止，不会留下孤儿进程或残留 timer；
/// 3. 能直接读 Jellyfin 的媒体库索引，不需要为了一次判断去跑 REST。
///
/// 为什么用轮询而不是监听库事件：
/// Jellyfin 的库事件在不同版本上语义并不完全一致（新增 vs 更新、刮削前后各触发几次），
/// 而「最近几小时有没有新条目」这个判断用轮询做只有几十毫秒成本，却稳定得多。
///
/// 处理顺序上有一点很关键：**必须等刮削完成**（People 非空）才动手。
/// 文件刚落地时演员信息还是空的，这时候建出来的合集既没成员也没封面。
/// </summary>
public sealed class ItemWatchService : IHostedService, IDisposable
{
    /// <summary>两次检查之间的间隔。</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>只关心最近这段时间内入库的条目。</summary>
    private static readonly TimeSpan Lookback = TimeSpan.FromHours(6);

    /// <summary>启动后先等一会儿，避开 Jellyfin 开机时的库扫描高峰。</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);

    private readonly ILibraryManager _libraryManager;
    private readonly IPeopleRepository _peopleRepository;
    private readonly ActorIndex _index;
    private readonly CollectionSyncJob _job;
    private readonly PluginLog _log;

    /// <summary>
    /// 已经处理过的条目（值 = 处理时刻，用来做过期清理）。
    /// 只在监听线程里访问，不需要加锁。
    /// </summary>
    private readonly Dictionary<Guid, DateTime> _handled = [];

    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>初始化实时监听。</summary>
    /// <param name="libraryManager">Jellyfin 媒体库服务。</param>
    /// <param name="peopleRepository">演职员仓库。</param>
    /// <param name="index">演员扫描器（用来把配置里的库 Id 解析出来）。</param>
    /// <param name="job">同步任务（提供局部同步）。</param>
    /// <param name="log">日志。</param>
    public ItemWatchService(
        ILibraryManager libraryManager,
        IPeopleRepository peopleRepository,
        ActorIndex index,
        CollectionSyncJob job,
        PluginLog log)
    {
        _libraryManager = libraryManager;
        _peopleRepository = peopleRepository;
        _index = index;
        _job = job;
        _log = log;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常停机。
            }
        }

        _cts.Dispose();
        _cts = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cts?.Dispose();
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(StartupDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await CheckOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // 一次失败不能让监听循环死掉：下一轮还会再来。
                _log.Error("实时入库检查失败", ex);
            }

            try
            {
                await Task.Delay(Interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Current;

        // 总开关或实时开关关掉时什么都不做。
        if (!config.Enabled || !config.RealtimeSync)
        {
            return;
        }

        var libraries = _index.ResolveLibraries(config.LibraryIds);
        if (libraries.Count == 0)
        {
            return;
        }

        var since = DateTime.UtcNow - Lookback;

        // 过期清理：只保留窗口内的记录，否则字典会随运行时间无限增长。
        if (_handled.Count > 0)
        {
            foreach (var stale in _handled.Where(kv => kv.Value < since).Select(kv => kv.Key).ToList())
            {
                _handled.Remove(stale);
            }
        }

        var pendingActors = new HashSet<string>(StringComparer.Ordinal);
        var accepted = new List<Guid>();

        foreach (var libraryId in libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Movie],
                ParentId = libraryId,

                // 只取「入库时间在窗口内」的条目：这是整条实时链路成本最低的关键，
                // 数据库直接按 DateCreated 过滤，不会把整个库的影片都拉回来。
                MinDateCreated = since,

                // 合集里的影片也要能被看到：折叠掉之后，
                // 刚被加进合集的片子会从查询里消失，实时流程就再也认不出它。
                CollapseBoxSetItems = false
            });

            foreach (var item in items)
            {
                // DateCreated 是 UTC；只有「窗口内 + 没处理过」的才继续。
                if (item.DateCreated < since || _handled.ContainsKey(item.Id))
                {
                    continue;
                }

                var names = ActorNamesOf(item.Id);
                if (names.Count == 0)
                {
                    // 还没刮削完（People 为空），**不要**标记为已处理，下一轮再看。
                    continue;
                }

                foreach (var name in names)
                {
                    pendingActors.Add(name);
                }

                accepted.Add(item.Id);
            }
        }

        if (pendingActors.Count == 0)
        {
            return;
        }

        _log.Info($"实时入库：{accepted.Count} 部新片，涉及 {pendingActors.Count} 位演员，开始局部同步");

        // 先记为已处理再执行：即使同步失败也不该每 30 秒重试同一部片。
        // 失败会写日志，用户也可以在设置页点「立即同步」做全量修复。
        var now = DateTime.UtcNow;
        foreach (var id in accepted)
        {
            _handled[id] = now;
        }

        var summary = await _job.RunForActorsAsync(pendingActors, cancellationToken).ConfigureAwait(false);
        _log.Info("实时入库同步完成：" + summary.Describe());
    }

    /// <summary>
    /// 取一条影片的演员名。
    ///
    /// 走 <see cref="IPeopleRepository.GetPeopleByItems"/> 而不是 BaseItem.People：
    /// Jellyfin 12 起 BaseItem 上已经没有 People 属性了。
    /// </summary>
    /// <param name="itemId">影片 Id。</param>
    /// <returns>演员名列表；没刮削或没有演员时为空。</returns>
    private IReadOnlyList<string> ActorNamesOf(Guid itemId)
    {
        var map = _peopleRepository.GetPeopleByItems(new List<Guid> { itemId });
        if (!map.TryGetValue(itemId, out var cast) || cast is null || cast.Count == 0)
        {
            return [];
        }

        return cast
            .Where(p => p.Type == PersonKind.Actor)
            .Select(p => (p.Name ?? string.Empty).Trim())
            .Where(n => n.Length > 0)
            .ToList();
    }
}
