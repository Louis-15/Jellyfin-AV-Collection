using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.AVCollection.Core;

/// <summary>
/// 演员名中文译名的持久化缓存。
///
/// 为什么必须缓存：
/// 1. 翻译要调 AI，一次几十秒。每次同步都重翻 20 个名字既慢又费钱。
/// 2. 合集名按译名生成，**译名一旦变化，Jellyfin 里的合集就会被改名**。
///    没有缓存的话，模型每次给的字面略有不同（「翼舞」/「飞鸟」），
///    合集名就会来回跳 —— 这正是最需要避免的现象。
/// 3. 用户手工修正过的译名必须记住，否则下次同步又被 AI 覆盖。
///
/// 存储位置与 <see cref="RuntimeState"/> 一致，放在 Jellyfin data 目录下。
/// </summary>
public sealed class TranslationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _sync = new();
    private readonly string _path;
    private Dictionary<string, string> _names = new(StringComparer.Ordinal);

    /// <summary>初始化缓存。</summary>
    /// <param name="paths">Jellyfin 路径表。</param>
    public TranslationStore(IApplicationPaths paths)
    {
        var dir = Path.Combine(paths.DataPath, "av-collection");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "translations.json");
        Load();
    }

    /// <summary>当前所有译名（日文原名 → 中文名）。</summary>
    public IReadOnlyDictionary<string, string> All
    {
        get
        {
            lock (_sync)
            {
                return new Dictionary<string, string>(_names, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// 查一个名字的译名。没有缓存时返回原名本身，
    /// 这样"没翻译"和"翻译成同一个字"在调用方看来是一致的。
    /// </summary>
    /// <param name="original">日文原名。</param>
    /// <returns>中文名或原名。</returns>
    public string Resolve(string original)
    {
        if (string.IsNullOrWhiteSpace(original))
        {
            return original ?? string.Empty;
        }

        lock (_sync)
        {
            return _names.TryGetValue(original.Trim(), out var translated)
                   && !string.IsNullOrWhiteSpace(translated)
                ? translated
                : original;
        }
    }

    /// <summary>某个名字是否已经有译名。</summary>
    /// <param name="original">日文原名。</param>
    /// <returns>是否有缓存。</returns>
    public bool Contains(string original)
    {
        if (string.IsNullOrWhiteSpace(original))
        {
            return false;
        }

        lock (_sync)
        {
            return _names.ContainsKey(original.Trim());
        }
    }

    /// <summary>写入/覆盖一个译名。</summary>
    /// <param name="original">日文原名。</param>
    /// <param name="translated">中文名。传空表示删除缓存（下次重新翻译）。</param>
    public void Set(string original, string translated)
    {
        var key = (original ?? string.Empty).Trim();
        if (key.Length == 0)
        {
            return;
        }

        lock (_sync)
        {
            var value = (translated ?? string.Empty).Trim();
            if (value.Length == 0 || string.Equals(value, key, StringComparison.Ordinal))
            {
                // 译名和原名相同（本来就是汉字姓名）也算有效结果，照样存下来，
                // 这样下次不会重复送审。
                _names[key] = value.Length == 0 ? key : value;
            }
            else
            {
                _names[key] = value;
            }
        }
    }

    /// <summary>批量写入。空值一律忽略，避免一次失败的翻译把已有译名抹掉。</summary>
    /// <param name="map">译名映射。</param>
    public void SetRange(IEnumerable<KeyValuePair<string, string>> map)
    {
        foreach (var pair in map)
        {
            if (string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }

            Set(pair.Key, pair.Value);
        }
    }

    /// <summary>删除某条缓存，让它下次重新翻译。</summary>
    /// <param name="original">日文原名。</param>
    public void Remove(string original)
    {
        var key = (original ?? string.Empty).Trim();
        if (key.Length == 0)
        {
            return;
        }

        lock (_sync)
        {
            _names.Remove(key);
        }
    }

    /// <summary>把缓存写回磁盘。写失败只影响下次是否重翻，不该让同步失败。</summary>
    public void Save()
    {
        lock (_sync)
        {
            try
            {
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_names, JsonOptions));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (IOException)
            {
                // 忽略：最坏情况是下次重新翻译一遍。
            }
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

            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path));
            if (map is null)
            {
                return;
            }

            _names = new Dictionary<string, string>(
                map.Where(kv => !string.IsNullOrWhiteSpace(kv.Key)),
                StringComparer.Ordinal);
        }
        catch (Exception)
        {
            // 文件损坏就当没有缓存：最坏情况是重新翻译一遍，不影响功能。
            _names = new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
