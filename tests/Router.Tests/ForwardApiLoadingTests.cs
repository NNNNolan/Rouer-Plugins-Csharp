using FluentAssertions;
using Moq;
using Plugins.ForwardAPI;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Contracts.Plugins;
using Router.Host.Plugins;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class ForwardApiLoadingTests
{
    [TestMethod]
    public async Task DotNetLoaderProducesTheSameRuntimeIndependentPackageCapabilities()
    {
        var boundary = Path.Combine(Path.GetTempPath(), "Router2API-PackageTests");
        var root = Path.Combine(boundary, Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "forwardapi");
        var snapshot = Path.Combine(root, "snapshot");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(snapshot);
        File.Copy(typeof(ForwardApiTerminal).Assembly.Location, Path.Combine(source, "Plugins.ForwardAPI.dll"));
        try
        {
            var host = PluginTestHost.Create("forwardapi");
            Mock.Get(host.Services.Accounts).Setup(value => value.ListAsync("forwardapi", It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<Account>());
            var hosts = new Mock<IPluginHostFactory>();
            hosts.Setup(value => value.Create("forwardapi", It.IsAny<IReadOnlyList<string>>())).Returns(host);
            var loader = new DotNetPackageLoader(hosts.Object);
            loader.CanLoad(source, "forwardapi").Should().BeTrue();
            await using var package = await loader.LoadAsync("forwardapi", source, snapshot, CancellationToken.None);
            package.Runtime.Should().Be("dotnet");
            package.Platforms.Single().ModelCacheTtl.Should().Be(TimeSpan.Zero);
            package.Platforms.Single().Tasks.Single().Name.Should().Be("forwardapi-daily-checkin");
            package.MainPage!.Html.Should().Contain("ForwardAPI");
            await package.StartAsync(CancellationToken.None);
            var result = await package.Endpoints.Single(endpoint => endpoint.Route == "accounts").InvokeAsync(new PluginHttpContext
            {
                PluginKey = "forwardapi", Platform = "forwardapi"
            });
            result.StatusCode.Should().Be(200);
        }
        finally
        {
            var target = Path.GetFullPath(root);
            if (target.StartsWith(Path.GetFullPath(boundary) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(target, recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { /* Collectible assemblies can remain mapped until the test invocation has returned. */ }
            }
        }
    }

}
