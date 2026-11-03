using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AVCollection.Core;

/// <summary>
/// 用 AI 把日本演员名翻译成中文名。
///
/// 与之前"猜哪些艺名是同一个人"不同：翻译是**确定性任务**，
/// 模型不需要做事实判断，幻觉空间小得多，出错也能被用户一眼看出来
/// （设置页会把「原名 → 译名」整张表列出来）。
///
/// 仍然保留人工兜底：译名表可在设置页逐条修正，修正后不会被 AI 覆盖。
/// </summary>
public sealed class AiNameTranslator
{
    private readonly AiClient _client;
    private readonly TranslationStore _store;
    private readonly PluginLog _log;

    /// <summary>初始化翻译器。</summary>
    /// <param name="client">AI 调用层。</param>
    /// <param name="store">译名缓存。</param>
    /// <param name="log">日志。</param>
    public AiNameTranslator(AiClient client, TranslationStore store, PluginLog log)
    {
        _client = client;
        _store = store;
        _log = log;
    }

    /// <summary>
    /// 测试 AI 连接是否可用。
    ///
    /// 只发一个极短请求，验证三件事：地址能连通、API Key 被接受、模型名存在。
    /// 用 <c>max_tokens</c> 掐断输出：推理模型即使回答"ok"也会先思考一大段，
    /// 不限制的话测试要等十几秒（16 个 token 会被 reasoning 吃光，实测要 64 才有正文）。
    /// </summary>
    /// <param name="options">AI 配置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>测试结果。</returns>
    public async Task<AiTestResult> TestAsync(AiOptions options, CancellationToken cancellationToken)
    {
        if (options is null)
        {
            return AiTestResult.Fail("没有 AI 配置");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return AiTestResult.Fail("未填写 API Key");
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            return AiTestResult.Fail("未填写模型名");
        }

        if (!options.IsOpenCode && string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            return AiTestResult.Fail("供应商选的是「自定义」，但没填接口地址");
        }

        // 测试用较短的超时：连通性有问题应该几秒内暴露，
        // 让用户对着 5 分钟的转圈等待没有意义。
        var probe = options.CloneWithTimeout(45);

        var result = await _client
            .CompleteAsync(probe, "只回复两个字：可以", 64, cancellationToken)
            .ConfigureAwait(false);

        var endpoint = options.ResolveBaseUrl().TrimEnd('/') + "/chat/completions";

        if (!result.Ok)
        {
            _log.Warn($"AI 连接测试失败：{result.Error}");
            return AiTestResult.Fail(result.Error, endpoint, result.ElapsedMs);
        }

        _log.Info($"AI 连接测试成功：{options.Provider} / {options.Model}，耗时 {result.ElapsedMs} ms");
        return AiTestResult.Success(endpoint, options.Model, result.ElapsedMs, result.Content);
    }

    /// <summary>
    /// 翻译一批演员名。已经有缓存的名字会跳过，不重复送审。
    /// </summary>
    /// <param name="names">待翻译的日文原名。</param>
    /// <param name="options">AI 配置。</param>
    /// <param name="force">true 表示忽略缓存全部重翻。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>翻译结果。</returns>
    public async Task<TranslateResult> TranslateAsync(
        IEnumerable<string> names,
        AiOptions options,
        bool force,
        CancellationToken cancellationToken)
    {
        var pending = names
            .Select(n => (n ?? string.Empty).Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Where(n => force || !_store.Contains(n))
            .ToList();

        if (pending.Count == 0)
        {
            return TranslateResult.NothingToDo();
        }

        if (options is null || !options.Enabled)
        {
            return TranslateResult.Fail("AI 未启用。请到「AI 设置」里勾选「启用 AI」。");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return TranslateResult.Fail("未填写 API Key");
        }

        if (!options.IsOpenCode && string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            return TranslateResult.Fail("供应商选的是「自定义」，但没填接口地址");
        }

        // 分批：一次几十个名字既容易超时，模型也更容易串行。
        // 每批 25 个，翻译任务比"判断同一个人"轻得多。
        const int BatchSize = 25;
        var translated = new Dictionary<string, string>(StringComparer.Ordinal);
        var failures = new List<string>();
        var totalElapsed = 0L;

        for (var offset = 0; offset < pending.Count; offset += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = pending.Skip(offset).Take(BatchSize).ToList();
            var prompt = BuildPrompt(batch);

            var result = await _client
                .CompleteAsync(options, prompt, maxTokens: null, cancellationToken)
                .ConfigureAwait(false);

            totalElapsed += result.ElapsedMs;

            if (!result.Ok)
            {
                failures.Add(result.Error);
                continue;
            }

            var parsed = ParseMapping(result.Content, batch);
            if (parsed.Count == 0)
            {
                failures.Add("模型回复里没有可用的翻译结果");
                continue;
            }

            foreach (var pair in parsed)
            {
                translated[pair.Key] = pair.Value;
            }
        }

        if (translated.Count > 0)
        {
            _store.SetRange(translated);
            _store.Save();
            _log.Info($"AI 翻译：已翻译 {translated.Count} 个名字（耗时 {totalElapsed} ms）");
        }

        return new TranslateResult
        {
            Ok = translated.Count > 0,
            Translated = translated,
            Requested = pending.Count,
            ElapsedMs = totalElapsed,
            Error = failures.Count > 0 ? string.Join("；", failures.Distinct(StringComparer.Ordinal)) : string.Empty
        };
    }

    /// <summary>
    /// 构造翻译提示词。
    ///
    /// 关键约束都写进去了：
    /// - 要求"通用译名"而不是字面直译（AV 女优在中文圈有固定叫法）
    /// - 已经是汉字的姓名只做简繁转换，不要音译成别的字
    /// - 拿不准就原样返回，绝不硬造
    /// - 严格 JSON，方便解析
    /// </summary>
    private static string BuildPrompt(IReadOnlyList<string> names)
    {
        var builder = new StringBuilder();
        builder.AppendLine("你在整理一个日本成人影片（AV）媒体库，需要把日本演员名转换成中文名，用来给合集命名。");
        builder.AppendLine();
        builder.AppendLine("规则（必须严格遵守）：");
        builder.AppendLine("1. 优先使用中文圈**通用的中文译名**，不要逐字直译。");
        builder.AppendLine("2. 如果名字本身就是汉字（如「小野夕子」「彩月七緒」），");
        builder.AppendLine("   只做繁体→简体转换（「七緒」→「七绪」、「瀬」→「濑」），不要换成别的字。");
        builder.AppendLine("3. 假名（平假名/片假名）按常见音译处理，例如「つばさ舞」→「翼舞」。");
        builder.AppendLine("4. 完全拿不准的名字，中文名**原样返回日文名**，绝对不要编造一个。");
        builder.AppendLine("5. 只返回 JSON，不要解释、不要 Markdown 代码块。");
        builder.AppendLine();
        builder.AppendLine("返回格式（键必须是下面列表里的原名，一个字都不能改）：");
        builder.AppendLine("{\"names\":{\"日文原名\":\"中文名\"}}");
        builder.AppendLine();
        builder.AppendLine("待转换的演员名：");

        foreach (var name in names)
        {
            builder.Append("- ").AppendLine(name);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 从模型回复里抠出「原名 → 译名」映射。
    /// 模型偶尔会加 ```json 包裹或前后缀，所以退化到"取第一个 { 到最后一个 }"。
    /// </summary>
    private Dictionary<string, string> ParseMapping(string content, IReadOnlyList<string> requested)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(content))
        {
            return result;
        }

        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            _log.Warn("AI 翻译回复里没有 JSON");
            return result;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(content[start..(end + 1)]);
        }
        catch (System.Text.Json.JsonException ex)
        {
            _log.Warn($"AI 翻译回复 JSON 解析失败：{ex.Message}");
            return result;
        }

        var map = root?["names"] as JsonObject;
        if (map is null)
        {
            return result;
        }

        // 只接受本次确实送审过的键：模型偶尔会"顺带"翻几个列表外的名字。
        var allowed = new HashSet<string>(requested, StringComparer.Ordinal);

        foreach (var pair in map)
        {
            var key = (pair.Key ?? string.Empty).Trim();
            if (!allowed.Contains(key))
            {
                continue;
            }

            var value = (pair.Value?.GetValue<string>() ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                continue;
            }

            result[key] = value;
        }

        return result;
    }
}

/// <summary>一次翻译任务的结果。</summary>
public sealed class TranslateResult
{
    /// <summary>是否至少翻译成功一个名字。</summary>
    public bool Ok { get; init; }

    /// <summary>本次新翻译出的映射。</summary>
    public IReadOnlyDictionary<string, string> Translated { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>本次送审的名字数量（已排除缓存命中的）。</summary>
    public int Requested { get; init; }

    /// <summary>耗时毫秒。</summary>
    public long ElapsedMs { get; init; }

    /// <summary>失败原因（成功时为空）。</summary>
    public string Error { get; init; } = string.Empty;

    /// <summary>构造"没有需要翻译的名字"的结果。</summary>
    /// <returns>结果。</returns>
    public static TranslateResult NothingToDo()
        => new() { Ok = true, Requested = 0 };

    /// <summary>构造失败结果。</summary>
    /// <param name="error">失败原因。</param>
    /// <returns>结果。</returns>
    public static TranslateResult Fail(string error)
        => new() { Ok = false, Error = error };

    /// <summary>生成一句中文摘要。</summary>
    /// <returns>摘要。</returns>
    public string Describe()
    {
        if (!Ok && Requested == 0)
        {
            return Error;
        }

        if (Requested == 0)
        {
            return "所有演员名都已有译名（缓存命中），无需重新翻译。";
        }

        var text = $"已翻译 {Translated.Count} / {Requested} 个名字（{ElapsedMs} ms）";
        if (!string.IsNullOrEmpty(Error))
        {
            text += "；" + Error;
        }

        return text;
    }
}
