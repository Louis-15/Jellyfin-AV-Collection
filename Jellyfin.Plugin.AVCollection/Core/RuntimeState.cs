using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Configuration;
namespace Jellyfin.Plugin.AVCollection.Core;

/// <summary>
/// 运行期状态：记住"哪个分组对应哪个合集 Id"。
///
/// 为什么不能只靠名字找合集：
/// 合并组的名字是按作品数动态拼出来的（"小野夕子 / 葵" ←→ "葵 / 小野夕子"），
/// 演员作品数一变名字就变。没有稳定 Id 的话，每次改名都会新建一个合集，
/// 旧合集变成孤儿。所以这里以 <c>group:{组Id}</c> / <c>actor:{演员名}</c> 为键持久化。
///
/// 存储位置放在 Jellyfin 的 data 目录，不写进插件 XML 配置：
/// 配置文件是给用户看的，状态是给程序看的，混在一起会让用户误改。
/// </summary>
public sealed class RuntimeState
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 状态文件里会有中文演员名。不转义成 \uXXXX 便于人工排查，
        // 反正是本机文件，不存在注入风险。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _sync = new();
    private readonly string _path;

    /// <summary>初始化状态存储。</summary>
    /// <param name="paths">Jellyfin 路径表。</param>
    public RuntimeState(IApplicationPaths paths)
    {
        var dir = Path.Combine(paths.DataPath, "av-collection");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "state.json");
        Load();
    }

    /// <summary>分组键 → 合集 Id。</summary>
    public Dictionary<string, Guid> Collections { get; private set; } =
        new(StringComparer.Ordinal);

    /// <summary>最近一次成功同步的时间（UTC）。仅用于设置页展示。</summary>
    public DateTimeOffset? LastRunUtc { get; private set; }

    /// <summary>最近一次同步摘要，供设置页展示。</summary>
    public string LastRunSummary { get; private set; } = string.Empty;

    /// <summary>读取某个分组已记录的合集 Id。</summary>
    /// <param name="groupId">分组键。</param>
    /// <param name="collectionId">合集 Id。</param>
    /// <returns>是否存在记录。</returns>
    public bool TryGetCollection(string groupId, out Guid collectionId)
    {
        lock (_sync)
        {
            return Collections.TryGetValue(groupId, out collectionId);
        }
    }

    /// <summary>记录分组与合集的对应关系。</summary>
    /// <param name="groupId">分组键。</param>
    /// <param name="collectionId">合集 Id。</param>
    public void SetCollection(string groupId, Guid collectionId)
    {
        lock (_sync)
        {
            Collections[groupId] = collectionId;
        }
    }

    /// <summary>删除某个分组的记录（合集已被删除时调用）。</summary>
    /// <param name="groupId">分组键。</param>
    public void RemoveCollection(string groupId)
    {
        lock (_sync)
        {
            Collections.Remove(groupId);
        }
    }

    /// <summary>记录一次成功运行。</summary>
    /// <param name="summary">摘要文本。</param>
    public void MarkRun(string summary)
    {
        lock (_sync)
        {
            LastRunUtc = DateTimeOffset.UtcNow;
            LastRunSummary = summary;
        }
    }

    /// <summary>把状态写回磁盘。写失败只影响下次能否复用合集 Id，不该让整个任务失败。</summary>
    public void Save()
    {
        lock (_sync)
        {
            var payload = new StateFile
            {
                Collections = Collections,
                LastRunUtc = LastRunUtc,
                LastRunSummary = LastRunSummary
            };

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(payload, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var payload = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(_path));
            if (payload?.Collections is null)
            {
                return;
            }

            Collections = new Dictionary<string, Guid>(payload.Collections, StringComparer.Ordinal);
            LastRunUtc = payload.LastRunUtc;
            LastRunSummary = payload.LastRunSummary ?? string.Empty;
        }
        catch (Exception)
        {
            // 状态文件损坏只意味着"下次按名字重新认领合集"，不影响功能，静默重来即可。
            Collections = new Dictionary<string, Guid>(StringComparer.Ordinal);
        }
    }

    private sealed class StateFile
    {
        public Dictionary<string, Guid> Collections { get; set; } = new(StringComparer.Ordinal);

        public DateTimeOffset? LastRunUtc { get; set; }

        public string? LastRunSummary { get; set; }
    }
}
