using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AVCollection.Core;

/// <summary>
/// 发一次 OpenAI 兼容的 <c>chat/completions</c> 请求。
///
/// 单独抽出来是因为「测试连接」和「翻译演员名」都要用它：
/// 两套 header / 超时 / 错误处理各写一遍，早晚会分叉成两种行为。
/// </summary>
public sealed class AiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PluginLog _log;

    /// <summary>初始化客户端。</summary>
    /// <param name="httpClientFactory">HTTP 客户端工厂。</param>
    /// <param name="log">日志。</param>
    public AiClient(IHttpClientFactory httpClientFactory, PluginLog log)
    {
        _httpClientFactory = httpClientFactory;
        _log = log;
    }

    /// <summary>
    /// 发一次请求。
    /// </summary>
    /// <param name="options">AI 配置（供应商 / 地址 / Key / 模型 / 超时）。</param>
    /// <param name="prompt">用户消息。</param>
    /// <param name="maxTokens">输出上限；null 表示不限制。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>调用结果。</returns>
    public async Task<AiCallResult> CompleteAsync(
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
            using var client = _httpClientFactory.CreateClient(nameof(AiClient));
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

            // 超时一定要留日志：静默返回会让用户只看到前端"超时"，
            // 服务器上完全查不到是哪一步、等了多久。
            var seconds = Math.Clamp(options.TimeoutSeconds, 10, 900);
            _log.Warn($"AI 请求超时（已等 {seconds} 秒，实际 {started.ElapsedMilliseconds} ms）。");
            return AiCallResult.Fail(
                $"超时（已等 {seconds} 秒）。可在「AI 设置」里调大超时，或减少一次处理的名字数量。",
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
    /// 从 chat/completions 的响应里取正文。
    /// 兼容 OpenAI 标准结构与个别网关的简化结构。
    /// </summary>
    /// <param name="body">原始响应体。</param>
    /// <returns>正文。</returns>
    public static string ExtractAssistantText(string body)
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
            // 落到下面直接返回原文，交给调用方的宽松解析。
        }

        return body;
    }

    /// <summary>
    /// 把 HTTP 错误码翻译成能直接照着改的中文。
    /// 光甩一个 401 给用户没有意义 —— 他不会知道是 key 错了还是没保存。
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

    /// <summary>截断长文本，避免日志/界面被整页 HTML 刷屏。</summary>
    /// <param name="value">原文。</param>
    /// <param name="max">最大长度。</param>
    /// <returns>截断后的文本。</returns>
    internal static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= max ? value : value[..max] + "…";
    }
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

    /// <summary>说明文字。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>实际请求的完整地址，便于用户核对。</summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>使用的模型名。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>耗时毫秒。</summary>
    public long ElapsedMs { get; init; }

    /// <summary>模型回复的片段。</summary>
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
            Reply = (reply ?? string.Empty).Trim() is { Length: > 0 } r
                ? (r.Length > 80 ? r[..80] + "…" : r)
                : "(空)"
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
