using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AVCollection.Core;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AVCollection.Api;

/// <summary>
/// 设置页背后的 API。
///
/// 所有写操作都要求管理员权限（RequiresElevation）：
/// 合并组一旦配错会把不相干的作品并到一起，不能开放给普通用户。
/// </summary>
[ApiController]
[Route("AVCollection")]
public sealed class CollectionController : ControllerBase
{
    private readonly ActorIndex _index;
    private readonly CollectionSyncJob _job;
    private readonly AiNameTranslator _translator;
    private readonly TranslationStore _translations;
    private readonly RuntimeState _state;
    private readonly ILibraryManager _libraryManager;
    private readonly PluginLog _log;

    /// <summary>初始化控制器。</summary>
    /// <param name="index">演员扫描器。</param>
    /// <param name="job">同步任务。</param>
    /// <param name="translator">AI 翻译器。</param>
    /// <param name="translations">译名缓存。</param>
    /// <param name="state">运行期状态。</param>
    /// <param name="libraryManager">媒体库服务。</param>
    /// <param name="log">日志。</param>
    public CollectionController(
        ActorIndex index,
        CollectionSyncJob job,
        AiNameTranslator translator,
        TranslationStore translations,
        RuntimeState state,
        ILibraryManager libraryManager,
        PluginLog log)
    {
        _index = index;
        _job = job;
        _translator = translator;
        _translations = translations;
        _state = state;
        _libraryManager = libraryManager;
        _log = log;
    }

    /// <summary>列出可选的媒体库。</summary>
    /// <returns>媒体库列表。</returns>
    [HttpGet("Libraries")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult Libraries()
    {
        var libraries = _libraryManager.GetUserRootFolder()
            .Children
            .OfType<MediaBrowser.Controller.Entities.CollectionFolder>()
            .Select(f => new LibraryDto
            {
                Id = f.Id.ToString("N"),
                Name = f.Name ?? string.Empty,
                CollectionType = f.CollectionType?.ToString() ?? string.Empty
            })
            .OrderBy(l => l.Name, StringComparer.Ordinal)
            .ToList();

        return Ok(libraries);
    }

    /// <summary>
    /// 列出当前扫描到的演员，附带他们属于哪个合并组、对应哪个合集。
    /// 设置页用它渲染"艺名合并"的候选列表。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>演员列表。</returns>
    [HttpGet("Actors")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<IActionResult> ActorsAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Current;
        var snapshot = await GetActorsAsync(config, cancellationToken).ConfigureAwait(false);

        var aliasToGroup = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in config.AliasGroups ?? [])
        {
            foreach (var alias in group.Aliases ?? [])
            {
                aliasToGroup.TryAdd((alias ?? string.Empty).Trim(), group.Id);
            }
        }

        var result = snapshot
            .Select(a =>
            {
                aliasToGroup.TryGetValue(a.Name, out var groupId);
                var groupKey = groupId is null ? "actor:" + a.Name : "group:" + groupId;
                _state.TryGetCollection(groupKey, out var collectionGuid);

                return new ActorDto
                {
                    Name = a.Name,
                    ItemCount = a.ItemIds.Count,
                    PersonId = a.PersonId?.ToString("N"),
                    GroupId = groupId,
                    CollectionId = collectionGuid == Guid.Empty ? null : collectionGuid.ToString("N"),
                    Samples = a.SampleTitles.ToArray()
                };
            })
            .OrderByDescending(a => a.ItemCount)
            .ThenBy(a => a.Name, StringComparer.Ordinal)
            .ToList();

        return Ok(result);
    }

    /// <summary>列出已配置的合并组。</summary>
    /// <returns>合并组列表。</returns>
    [HttpGet("Groups")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult Groups()
    {
        var config = Plugin.Current;
        var result = (config.AliasGroups ?? [])
            .Select(g => new GroupDto
            {
                Id = g.Id,
                Aliases = g.Aliases ?? [],
                DisplayName = g.DisplayName ?? string.Empty,
                PosterAlias = g.PosterAlias ?? string.Empty
            })
            .ToList();

        return Ok(result);
    }

    /// <summary>
    /// 新增或更新一个合并组。
    ///
    /// 约束：同一个艺名不能出现在两个组里，否则一个演员会进两个合集。
    /// 这里直接拒绝重复配置，并返回明确的中文错误，避免用户配错后一脸问号。
    /// </summary>
    /// <param name="request">合并组内容。</param>
    /// <returns>保存后的组。</returns>
    [HttpPost("Groups")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult UpsertGroup([FromBody] GroupRequest request)
    {
        if (request is null || request.Aliases is null)
        {
            return BadRequest(new { error = "缺少 aliases" });
        }

        var aliases = request.Aliases
            .Select(a => (a ?? string.Empty).Trim())
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (aliases.Length < 2)
        {
            return BadRequest(new { error = "至少要选两个艺名才能合并" });
        }

        var config = Plugin.Current;
        var groups = config.AliasGroups ?? [];

        var id = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("N") : request.Id!.Trim();

        var conflict = groups
            .Where(g => !string.Equals(g.Id, id, StringComparison.Ordinal))
            .SelectMany(g => (g.Aliases ?? []).Select(a => (Alias: (a ?? string.Empty).Trim(), GroupId: g.Id)))
            .FirstOrDefault(p => aliases.Contains(p.Alias, StringComparer.Ordinal));

        if (conflict.Alias is not null)
        {
            return BadRequest(new { error = $"艺名「{conflict.Alias}」已经属于另一个合并组" });
        }

        var existing = groups.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.Ordinal));
        if (existing is null)
        {
            existing = new ActorAliasGroup { Id = id };
            groups.Add(existing);
        }

        existing.Aliases = aliases;
        existing.DisplayName = (request.DisplayName ?? string.Empty).Trim();
        existing.PosterAlias = (request.PosterAlias ?? string.Empty).Trim();

        config.AliasGroups = groups;

        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(503, new { error = "插件尚未就绪" });
        }

        plugin.UpdateConfiguration(config);
        _log.Info($"合并组已保存：{string.Join(" + ", aliases)}");

        return Ok(new GroupDto
        {
            Id = existing.Id,
            Aliases = existing.Aliases,
            DisplayName = existing.DisplayName,
            PosterAlias = existing.PosterAlias
        });
    }

    /// <summary>删除一个合并组。删除后各艺名恢复成独立合集。</summary>
    /// <param name="id">组 Id。</param>
    /// <returns>操作结果。</returns>
    [HttpDelete("Groups/{id}")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult DeleteGroup(string id)
    {
        var config = Plugin.Current;
        var groups = config.AliasGroups ?? [];
        var target = groups.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.Ordinal));
        if (target is null)
        {
            return NotFound(new { error = "合并组不存在" });
        }

        groups.Remove(target);
        config.AliasGroups = groups;

        // 删组合集不会自动消失，这里只清掉状态记录，
        // 让下一次同步按艺名重新认领或重建。
        _state.RemoveCollection("group:" + id);
        _state.Save();

        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(503, new { error = "插件尚未就绪" });
        }

        plugin.UpdateConfiguration(config);
        _log.Info($"合并组已删除：{string.Join(" + ", target.Aliases ?? [])}");

        return Ok(new { ok = true });
    }

    /// <summary>
    /// 测试 AI 连接是否可用。
    ///
    /// 只发一个极短的请求，几十秒内就能给出结论：
    /// 地址通不通、Key 对不对、模型名存不存在。
    /// 用户配完 Key 后先点这个，比直接等一次完整翻译要友好得多。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>测试结果。</returns>
    [HttpPost("Ai/Test")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<IActionResult> TestAiAsync(CancellationToken cancellationToken)
    {
        var result = await _translator
            .TestAsync(Plugin.Current.Ai, cancellationToken)
            .ConfigureAwait(false);

        return Ok(new
        {
            ok = result.Ok,
            message = result.Message,
            endpoint = result.Endpoint,
            model = result.Model,
            elapsedMs = result.ElapsedMs,
            reply = result.Reply
        });
    }

    /// <summary>
    /// 列出「演员原名 → 中文译名」的完整对照表。
    ///
    /// 设置页用它渲染可编辑的译名表：AI 翻译不会 100% 准，
    /// 用户要能一眼看出错在哪并直接改掉。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>对照表。</returns>
    [HttpGet("Translations")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<IActionResult> TranslationsAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Current;
        var actors = await GetActorsAsync(config, cancellationToken).ConfigureAwait(false);
        var cached = _translations.All;

        var rows = actors
            .OrderByDescending(a => a.ItemIds.Count)
            .ThenBy(a => a.Name, StringComparer.Ordinal)
            .Select(a =>
            {
                var hasTranslation = cached.TryGetValue(a.Name, out var translated);
                return new
                {
                    original = a.Name,
                    translated = hasTranslation ? translated : a.Name,
                    hasTranslation,
                    itemCount = a.ItemIds.Count
                };
            })
            .ToList();

        return Ok(new
        {
            rows,
            total = rows.Count,
            translated = rows.Count(r => r.hasTranslation),
            aiEnabled = config.Ai?.TranslateNames == true
        });
    }

    /// <summary>
    /// 让 AI 翻译缺失的演员名（或全部重翻）。
    /// 结果写入缓存，下一次同步就会用中文名命名合集。
    /// </summary>
    /// <param name="request">请求参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>翻译结果。</returns>
    [HttpPost("Translations")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<IActionResult> TranslateAsync(
        [FromBody] TranslateRequest? request,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Current;
        var actors = await GetActorsAsync(config, cancellationToken).ConfigureAwait(false);

        var result = await _translator
            .TranslateAsync(actors.Select(a => a.Name), config.Ai, request?.Force == true, cancellationToken)
            .ConfigureAwait(false);

        return Ok(new
        {
            ok = result.Ok,
            summary = result.Describe(),
            requested = result.Requested,
            translated = result.Translated.Count,
            elapsedMs = result.ElapsedMs,
            error = result.Error
        });
    }

    /// <summary>
    /// 手工修正某个演员的译名。写完立即生效，且不会被后续 AI 翻译覆盖。
    /// </summary>
    /// <param name="request">修正内容。</param>
    /// <returns>操作结果。</returns>
    [HttpPost("Translations/Set")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult SetTranslation([FromBody] TranslateSetRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Original))
        {
            return BadRequest(new { error = "缺少 original" });
        }

        var original = request.Original.Trim();
        var translated = (request.Translated ?? string.Empty).Trim();

        if (translated.Length == 0)
        {
            // 清空表示"删掉这条缓存，下次重新翻译"。
            _translations.Remove(original);
            _translations.Save();
            return Ok(new { ok = true, original, translated = original, removed = true });
        }

        _translations.Set(original, translated);
        _translations.Save();
        _log.Info($"译名已手工修正：{original} → {translated}");

        return Ok(new { ok = true, original, translated });
    }

    /// <summary>预览：只计算会得到哪些合集，不写库。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>预览结果。</returns>
    [HttpPost("Preview")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<IActionResult> PreviewAsync(CancellationToken cancellationToken)
    {
        var summary = await _job.RunAsync(dryRun: true, progress: null, cancellationToken).ConfigureAwait(false);
        return Ok(new { summary = summary.Describe(), details = summary.Details });
    }

    /// <summary>立即执行一次完整同步。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>执行结果。</returns>
    [HttpPost("Run")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<IActionResult> RunAsync(CancellationToken cancellationToken)
    {
        var summary = await _job.RunAsync(dryRun: false, progress: null, cancellationToken).ConfigureAwait(false);
        return Ok(new
        {
            summary = summary.Describe(),
            created = summary.Created,
            synced = summary.Synced,
            removed = summary.Removed,
            repaired = summary.Repaired,
            failed = summary.Failed,
            skipped = summary.Skipped
        });
    }

    /// <summary>读取一次演员扫描结果。</summary>
    private async Task<IReadOnlyList<ActorInfo>> GetActorsAsync(
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var libraries = _index.ResolveLibraries(config.LibraryIds);
        return await Task.Run(() => _index.Scan(libraries, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>媒体库 DTO。</summary>
public sealed class LibraryDto
{
    /// <summary>库 Id（32 位无连字符）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>库名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>库类型（movies / tvshows …）。</summary>
    public string CollectionType { get; set; } = string.Empty;
}

/// <summary>演员 DTO。</summary>
public sealed class ActorDto
{
    /// <summary>艺名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>作品数。</summary>
    public int ItemCount { get; set; }

    /// <summary>Person Id。</summary>
    public string? PersonId { get; set; }

    /// <summary>所属合并组 Id，未合并为 null。</summary>
    public string? GroupId { get; set; }

    /// <summary>当前对应的合集 Id。</summary>
    public string? CollectionId { get; set; }

    /// <summary>代表作标题片段，用于辅助人工判断。</summary>
    public string[] Samples { get; set; } = [];
}

/// <summary>合并组 DTO。</summary>
public sealed class GroupDto
{
    /// <summary>组 Id。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>组内艺名。</summary>
    public string[] Aliases { get; set; } = [];

    /// <summary>手动指定的合集名（空表示自动）。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>用哪个艺名的头像做封面（空表示用作品数最多的）。</summary>
    public string PosterAlias { get; set; } = string.Empty;
}

/// <summary>翻译请求。</summary>
public sealed class TranslateRequest
{
    /// <summary>true 表示忽略缓存，把所有名字重新翻译一遍。</summary>
    public bool Force { get; set; }
}

/// <summary>手工修正译名的请求。</summary>
public sealed class TranslateSetRequest
{
    /// <summary>演员原名（日文）。</summary>
    public string? Original { get; set; }

    /// <summary>新的中文名。留空表示删除缓存、下次重新翻译。</summary>
    public string? Translated { get; set; }
}

/// <summary>合并组保存请求。</summary>
public sealed class GroupRequest
{
    /// <summary>组 Id；为空表示新建。</summary>
    public string? Id { get; set; }

    /// <summary>组内艺名。</summary>
    public string[]? Aliases { get; set; }

    /// <summary>手动指定的合集名。</summary>
    public string? DisplayName { get; set; }

    /// <summary>封面艺名。</summary>
    public string? PosterAlias { get; set; }
}
