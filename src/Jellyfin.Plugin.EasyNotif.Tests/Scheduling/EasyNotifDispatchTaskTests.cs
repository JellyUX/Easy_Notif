using Jellyfin.Plugin.EasyNotif.Scheduling;
using MediaBrowser.Model.Tasks;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Scheduling;

/// <summary>
/// Covers <see cref="EasyNotifDispatchTask"/>: it is a plain <see cref="IScheduledTask"/> with a
/// startup trigger and a 15-minute interval trigger (no continuous loop), and it delegates to the
/// dispatch service.
/// </summary>
public sealed class EasyNotifDispatchTaskTests
{
    [Fact]
    public void GetDefaultTriggers_IsStartupPlusFifteenMinuteInterval()
    {
        var triggers = new EasyNotifDispatchTask(Mock.Of<IDispatchService>()).GetDefaultTriggers().ToList();

        Assert.Equal(2, triggers.Count);
        Assert.Contains(triggers, t => t.Type == TaskTriggerInfoType.StartupTrigger);

        var interval = Assert.Single(triggers, t => t.Type == TaskTriggerInfoType.IntervalTrigger);
        Assert.Equal(TimeSpan.FromMinutes(15).Ticks, interval.IntervalTicks);
    }

    [Fact]
    public async Task ExecuteAsync_DelegatesToDispatch_AndReports100()
    {
        var dispatch = new Mock<IDispatchService>();
        var progress = new Mock<IProgress<double>>();

        await new EasyNotifDispatchTask(dispatch.Object).ExecuteAsync(progress.Object, CancellationToken.None);

        dispatch.Verify(d => d.RunDueAsync(It.IsAny<CancellationToken>()), Times.Once);
        progress.Verify(p => p.Report(100), Times.Once);
    }

    [Fact]
    public void Identity_IsStable()
    {
        var task = new EasyNotifDispatchTask(Mock.Of<IDispatchService>());

        Assert.Equal("EasyNotifDispatch", task.Key);
        Assert.False(string.IsNullOrWhiteSpace(task.Name));
        Assert.False(string.IsNullOrWhiteSpace(task.Category));
        Assert.False(string.IsNullOrWhiteSpace(task.Description));
    }
}
