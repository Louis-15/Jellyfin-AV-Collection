using Jellyfin.Plugin.AVCollection.Core;
using Xunit;

namespace Jellyfin.Plugin.AVCollection.Tests;

/// <summary>
/// AI 供应商配置的单元测试。
///
/// 这一层出错的表现很难查：用户在设置页看到的地址是 A，实际请求打到 B，
/// 报错只会显示"连接失败"或 401，根本联想不到是地址解析的问题。
/// </summary>
public class AiProviderTests
{
    [Fact]
    public void 默认是内置的_OpenCode()
    {
        var options = new AiOptions();
        Assert.Equal(AiProviders.OpenCode, options.Provider);
        Assert.True(options.IsOpenCode);
    }

    [Fact]
    public void OpenCode_用内置地址忽略用户填的值()
    {
        var options = new AiOptions
        {
            Provider = AiProviders.OpenCode,

            // 用户从旧版本升级时，BaseUrl 里可能残留着别的地址；
            // 只要供应商还是 OpenCode，就必须用内置地址。
            BaseUrl = "https://api.example.com/v1"
        };

        Assert.Equal(AiProviders.OpenCodeBaseUrl, options.ResolveBaseUrl());
    }

    [Fact]
    public void 自定义供应商用用户填的地址()
    {
        var options = new AiOptions
        {
            Provider = AiProviders.Custom,
            BaseUrl = "  https://api.deepseek.com/v1  "
        };

        Assert.False(options.IsOpenCode);

        // 前后空格必须去掉，否则拼出来是 "https://... /v1/chat/completions"。
        Assert.Equal("https://api.deepseek.com/v1", options.ResolveBaseUrl());
    }

    [Fact]
    public void 自定义供应商没填地址时返回空串()
    {
        var options = new AiOptions { Provider = AiProviders.Custom, BaseUrl = string.Empty };
        Assert.Equal(string.Empty, options.ResolveBaseUrl());
    }

    [Fact]
    public void 供应商名大小写不敏感()
    {
        var options = new AiOptions { Provider = "opencode" };
        Assert.True(options.IsOpenCode);
        Assert.Equal(AiProviders.OpenCodeBaseUrl, options.ResolveBaseUrl());
    }
}
