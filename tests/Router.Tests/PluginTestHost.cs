using Moq;
using Router.Contracts.Host;

namespace Router.Tests;

internal static class PluginTestHost
{
    public static IPluginHost Create(string key, IProxyPoolHttpClientFactory? pool = null)
    {
        var capabilities = new Mock<IPluginServices>();
        var http = new Mock<IPluginHttpServices>();
        http.SetupGet(value => value.Pool).Returns(pool ?? Mock.Of<IProxyPoolHttpClientFactory>());
        capabilities.SetupGet(value => value.PluginKey).Returns(key);
        capabilities.SetupGet(value => value.Http).Returns(http.Object);
        capabilities.SetupGet(value => value.State).Returns(new PluginStateServices(Mock.Of<IPluginStateStore>(), Mock.Of<IPluginStateStore>()));
        capabilities.SetupGet(value => value.Accounts).Returns(Mock.Of<IPluginAccounts>());
        capabilities.SetupGet(value => value.Models).Returns(Mock.Of<IPluginModels>());
        capabilities.SetupGet(value => value.Tasks).Returns(Mock.Of<IPluginTasks>());
        capabilities.SetupGet(value => value.Log).Returns(Mock.Of<IPluginLogSink>());
        capabilities.SetupGet(value => value.Execution).Returns(new PluginExecutionOptions());
        var host = new Mock<IPluginHost>(MockBehavior.Strict);
        host.SetupGet(value => value.PluginKey).Returns(key);
        host.SetupGet(value => value.Services).Returns(capabilities.Object);
        // 只模拟 Contracts 能力，不使用宿主实现或服务定位器。
        return host.Object;
    }
}
