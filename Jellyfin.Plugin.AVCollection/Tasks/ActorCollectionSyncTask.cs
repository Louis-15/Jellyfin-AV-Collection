using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AVCollection.Core;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.AVCollection.Tasks;

/// <summary>
/// 定时同步任务：扫描媒体库，按演员维护合集、封面与合并组。
/// </summary>
public sealed class ActorCollectionSyncTask : IScheduledTask
{
    private readonly CollectionSyncJob _job;

    /// <summary>初始化任务。</summary>
    /// <param name="job">同步任务。</param>
    public ActorCollectionSyncTask(CollectionSyncJob job)
    {
        _job = job;
    }

    /// <inheritdoc />
    public string Name => "演员合集 · 自动同步";

    /// <inheritdoc />
    public string Key => "AVCollectionSync";

    /// <inheritdoc />
    public string Description =>
        "扫描目标影视库，按演员建立并同步 Jellyfin 合集，自动用演员头像做封面，并按配置的合并组把同一演员的多个艺名并进同一个合集。";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return _job.RunAsync(dryRun: false, progress, cancellationToken);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(6).Ticks
            }
        ];
    }
}
