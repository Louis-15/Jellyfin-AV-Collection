using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AVCollection.Core;

/// <summary>
/// 让 OpenAI 兼容模型看一眼演员列表，猜测哪些艺名属于同一个人。
///
/// 重要边界：这里只产出"建议"，永远不会自动写进配置。
/// 演员同名判定一旦出错后果很重（两部不相干的作品被并进一个合集，
/// 还带着别人的头像），所以必须由用户在设置页确认。
/// </summary>
public sealed class AiAliasAdvisor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PluginLog _log;

    /// <summary>初始化 AI 顾问。</summary>
    /// <param name="httpClientFactory">HTTP 客户端工厂。</param>
    /// <param name="log">日志。</param>
    public AiAliasAdvisor(IHttpClientFactory httpClientFactory, PluginLog log)
    {
        _httpClientFactory = httpClientFactory;
        _log = log;
    }

    /// <summary>
    /// 请求模型给出合并建议。
    /// </summary>
    /// <param name="actors">已扫到的演员。</param>
    /// <param name="options">AI 配置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>建议列表；调用失败时为空列表，并把错误写进 <paramref name="error"/>。</returns>
    public async Task<(IReadOnlyList<AliasSuggestion> Suggestions, string Error)> SuggestAsync(
        IReadOnlyList<ActorInfo> actors,
        AiOptions options,
        CancellationToken cancellationToken)
    {
        if (options is null || !options.Enabled)
        {
            return ([], "AI 建议未启用");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return ([], "未填写 API Key");
        }

        if (!options.IsOpenCode && string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            return ([], "供应商选的是「自定义」，但没填接口地址");
        }

        var pool = actors
            .Where(a => a.ItemIds.Count > 0)
            .OrderByDescending(a => a.ItemIds.Count)
            .ThenBy(a => a.Name, StringComparer.Ordinal)
            .Take(Math.Clamp(options.MaxActorsPerRequest, 10, 400))
            .ToList();

        if (pool.Count < 2)
        {
            return ([], "演员太少，不需要判断");
        }

        var prompt = BuildPrompt(pool);

        // 真实请求不限制 max_tokens：模型要先输出完整的 JSON 分组。
        var result = await CallAsync(options, prompt, maxTokens: null, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Ok)
        {
            return ([], result.Error);
        }

        var suggestions = ParseSuggestions(result.Content, pool);
        _log.Info($"AI 建议：模型返回 {suggestions.Count} 组可能的同一人（耗时 {result.ElapsedMs} ms）");
        return (suggestions, string.Empty);
    }

    /// <summary>
    /// 测试连接是否可用。只发一个极短的请求，验证三件事：
    /// 地址能连通、API Key 被接受、模型名存在。
    ///
    /// 刻意用 <c>max_tokens</c> 掐断输出：推理模型即使回答"ok"也会先思考一大段，
    /// 不限制的话测试要等十几秒，用户会以为按钮坏了。
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

        // 测试用独立的短超时：连通性有问题应该几秒内就暴露，
        // 让用户对着一个 5 分钟的转圈等待没有意义。
        var probe = new AiOptions
        {
            Enabled = options.Enabled,
            Provider = options.Provider,
            BaseUrl = options.BaseUrl,
            ApiKey = options.ApiKey,
            Model = options.Model,
            TimeoutSeconds = 45,
            MaxActorsPerRequest = options.MaxActorsPerRequest
        };

        var result = await CallAsync(
                probe,
                "只回复两个字：可以",
                // 16 个 token 不够：推理模型会先把预算全花在 reasoning 上，
                // 实测 content 返回空串；64 才能拿到 "可以" 且仍然 1.9 秒结束。
                maxTokens: 64,
                cancellationToken)
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
    /// 真正发一次 chat/completions 请求。Suggest 与 Test 共用，避免两套 header /
    /// 超时 / 错误处理各写一遍后逐渐分叉。
    /// </summary>
    /// <param name="options">AI 配置。</param>
    /// <param name="prompt">用户消息。</param>
    /// <param name="maxTokens">输出上限；null 表示不限制。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>调用结果。</returns>
    private async Task<AiCallResult> CallAsync(
        AiOptions options,
        string prompt,
        int? maxTokens,
        CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["model"] = options.Model,
            ["temperature"] = 0.1,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = prompt }
            }
        };

        if (maxTokens is > 0)
        {
            payload["max_tokens"] = maxTokens.Value;
        }

        var started = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var client = _httpClientFactory.CreateClient(nameof(AiAliasAdvisor));
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 10, 900));

            var url = options.ResolveBaseUrl().TrimEnd('/') + "/chat/completions";
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload.ToJsonString(JsonOptions), Encoding.UTF8, "application/json")
            };

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey.Trim());

            // Open Code 网关从 2025-09-06 起强制要求这个头；自带接口不能乱加，
            // 部分服务端会因为未知请求头直接 400。所以按供应商决定，不做成开关。
            // 值必须固定：网关用它做路由与 prompt 缓存（见 AiProviders.SessionId）。
            if (options.IsOpenCode)
            {
                request.Headers.TryAddWithoutValidation("x-opencode-session", AiProviders.SessionId);
            }

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            started.Stop();

            if (!response.IsSuccessStatusCode)
            {
                _log.Warn($"AI 请求失败：HTTP {(int)response.StatusCode}");
                return AiCallResult.Fail(
                    DescribeHttpError((int)response.StatusCode, body),
                    started.ElapsedMilliseconds);
            }

            return AiCallResult.Success(ExtractAssistantText(body), started.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            started.Stop();

            // 超时一定要留日志：之前这里静默返回，用户只看到前端"超时"，
            // 服务器上完全查不到是哪一步、等了多久。
            var seconds = Math.Clamp(options.TimeoutSeconds, 10, 900);
            _log.Warn($"AI 请求超时（已等 {seconds} 秒，实际 {started.ElapsedMilliseconds} ms）。推理模型判断几十位演员通常要 2-3 分钟。");
            return AiCallResult.Fail(
                $"超时（已等 {seconds} 秒）。推理模型判断一大批演员通常要 2-3 分钟，可调大「超时」或减少送审人数。",
                started.ElapsedMilliseconds);
        }
        catch (HttpRequestException ex)
        {
            started.Stop();
            _log.Error("AI 请求网络异常", ex);
            return AiCallResult.Fail($"网络错误：{ex.Message}", started.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            started.Stop();
            _log.Error("AI 请求异常", ex);
            return AiCallResult.Fail(ex.Message, started.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// 把 HTTP 错误码翻译成能直接照着改的中文。
    /// 光甩一个 401 给用户没有意义 —— 他不会知道是 key 错了还是 key 没保存。
    /// </summary>
    private static string DescribeHttpError(int status, string body)
    {
        var detail = Truncate(body, 300);
        return status switch
        {
            401 => $"API Key 无效或已过期（HTTP 401）。请到供应商后台确认密钥，注意不要带多余空格。原文：{detail}",
            403 => $"API Key 没有访问该模型的权限（HTTP 403）。原文：{detail}",
            404 => $"接口地址不存在（HTTP 404）。检查地址是否少了 /v1，或该供应商不支持这个路径。原文：{detail}",
            429 => $"请求过于频繁或额度用尽（HTTP 429）。稍后再试，或检查账户余额。原文：{detail}",
            >= 500 => $"供应商服务端错误（HTTP {status}）。稍后再试。原文：{detail}",
            _ => $"接口返回 HTTP {status}：{detail}"
        };
    }

    /// <summary>
    /// 构造提示词。刻意把"宁可漏报不要误报"写在最前面：
    /// 这类判断一旦出错，用户很难第一时间发现。
    /// </summary>
    private static string BuildPrompt(IReadOnlyList<ActorInfo> pool)
    {
        var builder = new StringBuilder();
        builder.AppendLine("你在整理一个日本成人影片（AV）媒体库的演员列表。");
        builder.AppendLine("任务：找出列表中「其实是同一个人、但使用了不同艺名」的演员。");
        builder.AppendLine();
        builder.AppendLine("规则（必须严格遵守）：");
        builder.AppendLine("1. 只输出你有高度把握的组合。不确定就不要输出，宁可漏报。");
        builder.AppendLine("2. 每组 2 到 4 个艺名。");
        builder.AppendLine("3. 艺名必须与下面列表里的写法完全一致，一个字都不能改。");
        builder.AppendLine("4. 不要输出只有 1 个艺名的组。");
        builder.AppendLine("5. 只返回 JSON，不要任何解释性文字或 Markdown 代码块。");
        builder.AppendLine();
        builder.AppendLine("返回格式：");
        builder.AppendLine("{\"groups\":[{\"aliases\":[\"艺名A\",\"艺名B\"],\"reason\":\"一句话理由\"}]}");
        builder.AppendLine();
        builder.AppendLine("没有把握时返回 {\"groups\":[]}。");
        builder.AppendLine();
        builder.AppendLine("演员列表（格式：艺名 | 作品数 | 代表作标题片段）：");

        foreach (var actor in pool)
        {
            var samples = actor.SampleTitles.Count > 0
                ? string.Join(" ; ", actor.SampleTitles)
                : "-";
            builder.Append("- ").Append(actor.Name)
                .Append(" | ").Append(actor.ItemIds.Count)
                .Append(" | ").AppendLine(samples);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 从 chat/completions 的响应里取正文。
    /// 兼容 OpenAI 标准结构与个别网关的简化结构。
    /// </summary>
    private static string ExtractAssistantText(string body)
    {
        try
        {
            var root = JsonNode.Parse(body);
            var content = root?["choices"]?[0]?["message"]?["content"];
            if (content is not null)
            {
                return content.GetValue<string>();
            }
        }
        catch (JsonException)
        {
            // 落到下面直接返回原文，交给 ParseSuggestions 的宽松解析。
        }

        return body;
    }

    /// <summary>
    /// 从模型回复里抠出 JSON。模型经常会加 ```json 包裹或前后缀，
    /// 所以退化到"取第一个 { 到最后一个 }"。
    /// </summary>
    private List<AliasSuggestion> ParseSuggestions(string content, IReadOnlyList<ActorInfo> pool)
    {
        var result = new List<AliasSuggestion>();
        if (string.IsNullOrWhiteSpace(content))
        {
            return result;
        }

        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            _log.Warn("AI 回复里没有 JSON");
            return result;
        }

        var json = content[start..(end + 1)];
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            _log.Warn($"AI 回复 JSON 解析失败：{ex.Message}");
            return result;
        }

        var groups = root?["groups"] as JsonArray;
        if (groups is null)
        {
            return result;
        }

        // 只接受确实存在于本次演员列表里的艺名：模型偶尔会"编"出名字，
        // 或者把列表外的常见艺名带进来。
        var known = new HashSet<string>(pool.Select(a => a.Name), StringComparer.Ordinal);

        foreach (var node in groups)
        {
            var aliases = node?["aliases"] as JsonArray;
            if (aliases is null)
            {
                continue;
            }

            var names = aliases
                .Select(a => (a?.GetValue<string>() ?? string.Empty).Trim())
                .Where(n => n.Length > 0 && known.Contains(n))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (names.Count < 2)
            {
                continue;
            }

            result.Add(new AliasSuggestion
            {
                Aliases = names,
                Reason = (node?["reason"]?.GetValue<string>() ?? string.Empty).Trim()
            });
        }

        return result;
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= max ? value : value[..max] + "…";
    }
}

/// <summary>模型给出的一条"可能是同一个人"的建议。</summary>
public sealed class AliasSuggestion
{
    /// <summary>建议合并的艺名（已经过滤成真实存在的演员名）。</summary>
    public required IReadOnlyList<string> Aliases { get; init; }

    /// <summary>模型给的理由，仅供参考。</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>一次 <c>chat/completions</c> 调用的原始结果。</summary>
/// <param name="Ok">是否成功。</param>
/// <param name="Content">模型回复正文（失败时为空）。</param>
/// <param name="Error">失败原因（成功时为空）。</param>
/// <param name="ElapsedMs">耗时毫秒。</param>
public sealed record AiCallResult(bool Ok, string Content, string Error, long ElapsedMs)
{
    /// <summary>构造成功结果。</summary>
    /// <param name="content">回复正文。</param>
    /// <param name="elapsedMs">耗时毫秒。</param>
    /// <returns>结果。</returns>
    public static AiCallResult Success(string content, long elapsedMs)
        => new(true, content ?? string.Empty, string.Empty, elapsedMs);

    /// <summary>构造失败结果。</summary>
    /// <param name="error">失败原因。</param>
    /// <param name="elapsedMs">耗时毫秒。</param>
    /// <returns>结果。</returns>
    public static AiCallResult Fail(string error, long elapsedMs)
        => new(false, string.Empty, error ?? "未知错误", elapsedMs);
}

/// <summary>「测试连接」的返回结果，字段直接对应设置页要显示的内容。</summary>
public sealed class AiTestResult
{
    /// <summary>是否连通。</summary>
    public bool Ok { get; init; }

    /// <summary>说明文字（成功时是"连接正常"，失败时是具体原因）。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>实际请求的完整地址，便于用户核对。</summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>使用的模型名。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>耗时毫秒。</summary>
    public long ElapsedMs { get; init; }

    /// <summary>模型回复的片段（成功时可用于确认模型真的在响应）。</summary>
    public string Reply { get; init; } = string.Empty;

    /// <summary>构造成功结果。</summary>
    /// <param name="endpoint">请求地址。</param>
    /// <param name="model">模型名。</param>
    /// <param name="elapsedMs">耗时毫秒。</param>
    /// <param name="reply">回复片段。</param>
    /// <returns>结果。</returns>
    public static AiTestResult Success(string endpoint, string model, long elapsedMs, string reply)
        => new()
        {
            Ok = true,
            Message = $"连接正常（{elapsedMs} ms）",
            Endpoint = endpoint,
            Model = model,
            ElapsedMs = elapsedMs,
            Reply = (reply ?? string.Empty).Trim() is { Length: > 0 } r ? (r.Length > 80 ? r[..80] + "…" : r) : "(空)"
        };

    /// <summary>构造失败结果。</summary>
    /// <param name="error">失败原因。</param>
    /// <param name="endpoint">请求地址（未知时为空）。</param>
    /// <param name="elapsedMs">耗时毫秒。</param>
    /// <returns>结果。</returns>
    public static AiTestResult Fail(string error, string endpoint = "", long elapsedMs = 0)
        => new()
        {
            Ok = false,
            Message = error,
            Endpoint = endpoint,
            ElapsedMs = elapsedMs
        };
}
