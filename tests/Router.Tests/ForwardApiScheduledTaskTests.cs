using Plugins.ForwardAPI;
using Router.Host.Plugins;
using Router.Host.Services;

namespace Router.Tests;

[TestClass]
public sealed class ForwardApiScheduledTaskTests
{
    [TestMethod]
    public void ForwardApiRegistersDailyCheckInTask()
    {
        using var terminal = new ForwardApiTerminal(PluginTestHost.Create("forwardapi"));

        var task = terminal.ScheduledTasks.Single();
        Assert.AreEqual("forwardapi-daily-checkin", task.Name);
        Assert.AreEqual("0 10 10 * * *", task.Cron);
    }

    [TestMethod]
    public void HostCatalogDiscoversTasksProvidedByTerminal()
    {
        using var terminal = new ForwardApiTerminal(PluginTestHost.Create("forwardapi"));
        var tasks = DotNetPackageLoader.CreateScheduledTasks(terminal);

        Assert.AreEqual("forwardapi-daily-checkin", tasks.Single().Name);
    }

}
