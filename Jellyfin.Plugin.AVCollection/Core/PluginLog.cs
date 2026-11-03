using System;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AVCollection.Core;

/// <summary>
/// 统一的日志前缀。
/// Jellyfin 的日志界面把 logger 名字当分类显示，所以正文里再带一次插件名便于 grep。
/// </summary>
public sealed class PluginLog
{
    private const string Prefix = "[AV Collection] ";

    private readonly ILogger _logger;

    /// <summary>初始化日志包装。</summary>
    /// <param name="logger">Jellyfin 注入的 logger。</param>
    public PluginLog(ILogger<PluginLog> logger)
    {
        _logger = logger;
    }

    /// <summary>普通信息。</summary>
    /// <param name="message">消息。</param>
    public void Info(string message) => _logger.LogInformation("{Message}", Prefix + message);

    /// <summary>警告。</summary>
    /// <param name="message">消息。</param>
    public void Warn(string message) => _logger.LogWarning("{Message}", Prefix + message);

    /// <summary>错误。</summary>
    /// <param name="message">消息。</param>
    /// <param name="ex">异常。</param>
    public void Error(string message, Exception? ex = null)
    {
        if (ex is null)
        {
            _logger.LogError("{Message}", Prefix + message);
        }
        else
        {
            _logger.LogError(ex, "{Message}", Prefix + message);
        }
    }
}
