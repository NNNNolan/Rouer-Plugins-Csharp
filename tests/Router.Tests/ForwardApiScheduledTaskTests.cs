using Plugins.ForwardAPI;

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
}
