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
        var payload = new JsonObject
        {
            ["model"] = options.Model,
            ["temperature"] = 0.1,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = prompt }
            }
        };

        try
        {
            using var client = _httpClientFactory.CreateClient(nameof(AiAliasAdvisor));
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 10, 300));

            var url = options.ResolveBaseUrl().TrimEnd('/') + "/chat/completions";
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload.ToJsonString(JsonOptions), Encoding.UTF8, "application/json")
            };

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey.Trim());

            // Open Code 网关从 2025-09-06 起强制要求这个头；自带接口不能乱加，
            // 部分服务端会因为未知请求头直接 400。所以按供应商决定，不做成开关。
            if (options.IsOpenCode)
            {
                request.Headers.TryAddWithoutValidation("x-opencode-session", Guid.NewGuid().ToString("N"));
            }

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _log.Warn($"AI 建议请求失败：HTTP {(int)response.StatusCode}");
                return ([], $"接口返回 HTTP {(int)response.StatusCode}：{Truncate(body, 200)}");
            }

            var content = ExtractAssistantText(body);
            var suggestions = ParseSuggestions(content, pool);

            _log.Info($"AI 建议：模型返回 {suggestions.Count} 组可能的同一人");
            return (suggestions, string.Empty);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ([], "AI 请求超时");
        }
        catch (Exception ex)
        {
            _log.Error("AI 建议请求异常", ex);
            return ([], ex.Message);
        }
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
