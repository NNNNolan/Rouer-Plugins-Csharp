using System.Net;
using Router.Contracts.Host;

namespace Plugins.ForwardAPI;

public sealed partial class ForwardApiTerminal
{
    private static bool TryApplyProxySettings(
        ForwardApiSettings settings, ForwardApiSettings previous,
        string? proxyMode, string? proxyUrl, string? proxyUsername, string? proxyPassword,
        out string error)
    {
        settings.ProxyMode = (proxyMode ?? previous.ProxyMode).Trim().ToLowerInvariant();
        settings.ProxyUrl = (proxyUrl ?? previous.ProxyUrl).Trim();
        settings.ProxyUsername = proxyUsername ?? previous.ProxyUsername;
        var connectionChanged = settings.ProxyMode != previous.ProxyMode
            || settings.ProxyUrl != previous.ProxyUrl || settings.ProxyUsername != previous.ProxyUsername;
        settings.ProxyPassword = !string.IsNullOrEmpty(proxyPassword) ? proxyPassword
            : connectionChanged ? string.Empty : previous.ProxyPassword;
        if (settings.ProxyMode != "custom")
        {
            settings.ProxyUrl = string.Empty;
            settings.ProxyUsername = string.Empty;
            settings.ProxyPassword = string.Empty;
        }
        return TryValidateProxySettings(settings, out error);
    }

    private static bool TryValidateProxySettings(ForwardApiSettings settings, out string error)
    {
        error = string.Empty;
        if (settings.ProxyMode is not ("direct" or "pool" or "custom"))
        {
            error = "请求出口必须是 direct、pool 或 custom";
            return false;
        }
        if (settings.ProxyMode != "custom") return true;
        if (!Uri.TryCreate(settings.ProxyUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https" or "socks5")
            || string.IsNullOrWhiteSpace(uri.Host) || uri.Port is < 1 or > 65535
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath is not ("" or "/"))
        {
            error = "指定代理必须是 http、https 或 socks5 的 host:port 地址；认证请填写用户名和密码，不要放在地址中";
            return false;
        }
        if (settings.ProxyUsername.Any(char.IsControl) || settings.ProxyPassword.Any(char.IsControl))
        {
            error = "代理认证信息不能包含控制字符";
            return false;
        }
        if (settings.ProxyUsername.Length == 0 && settings.ProxyPassword.Length > 0)
        {
            error = "配置代理密码时必须填写代理用户名";
            return false;
        }
        return true;
    }

    /// <summary>管理请求复用宿主直连/代理池；指定代理仅连接配置地址，不启用系统代理或自动重试。</summary>
    private async Task<HttpClient> CreateAccountClientAsync(ForwardApiSettings settings, CancellationToken cancellationToken)
    {
        if (!TryValidateProxySettings(settings, out var error)) throw new InvalidOperationException(error);
        return settings.ProxyMode switch
        {
            "pool" => await _host.Http.Pool.CreateClientAsync(new ProxyPoolHttpClientOptions
            {
                AllowDirectFallback = false, AllowAutoRedirect = true
            }, cancellationToken),
            "custom" => CreateCustomProxyClient(settings, allowAutoRedirect: true),
            _ => _host.Http.CreateDirectClient(new PluginHttpClientOptions { AllowAutoRedirect = true })
        };
    }

    private static HttpClient CreateCustomProxyClient(ForwardApiSettings settings, bool allowAutoRedirect)
    {
        var proxy = new WebProxy(new Uri(settings.ProxyUrl)) { BypassProxyOnLocal = false };
        if (settings.ProxyUsername.Length > 0)
            proxy.Credentials = new NetworkCredential(settings.ProxyUsername, settings.ProxyPassword);
        return new HttpClient(new SocketsHttpHandler
        {
            Proxy = proxy, UseProxy = true, UseCookies = false,
            AllowAutoRedirect = allowAutoRedirect,
            ConnectTimeout = TimeSpan.FromSeconds(60)
        }) { Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>指定代理响应持有客户端直到响应或流被释放，避免响应头返回即断开 SSE。</summary>
    private sealed class ProxyResponseLifetime(HttpClient client) : IAsyncDisposable
    {
        private HttpClient? _client = client;
        public HttpResponseMessage? Response { get; set; }
        public void Dispose()
        {
            Response?.Dispose();
            Interlocked.Exchange(ref _client, null)?.Dispose();
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
