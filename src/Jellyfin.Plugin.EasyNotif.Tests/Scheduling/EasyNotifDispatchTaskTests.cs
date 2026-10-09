using Jellyfin.Plugin.EasyNotif.Alerts;
using Jellyfin.Plugin.EasyNotif.Scheduling;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
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
        var triggers = new EasyNotifDispatchTask(Mock.Of<IDispatchService>(), Mock.Of<ISeriesNotifier>(), Mock.Of<IAdminAlertService>(), NullLogger<EasyNotifDispatchTask>.Instance).GetDefaultTriggers().ToList();

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

        await new EasyNotifDispatchTask(dispatch.Object, Mock.Of<ISeriesNotifier>(), Mock.Of<IAdminAlertService>(), NullLogger<EasyNotifDispatchTask>.Instance).ExecuteAsync(progress.Object, CancellationToken.None);

        dispatch.Verify(d => d.RunDueAsync(It.IsAny<CancellationToken>()), Times.Once);
        progress.Verify(p => p.Report(100), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_FlushesSeriesAfterDispatch_AndAFlushFailureDoesNotEscape()
    {
        var calls = new List<string>();
        var dispatch = new Mock<IDispatchService>();
        dispatch.Setup(d => d.RunDueAsync(It.IsAny<CancellationToken>())).Callback(() => calls.Add("dispatch")).Returns(Task.CompletedTask);
        var series = new Mock<ISeriesNotifier>();
        series.Setup(s => s.FlushAsync(It.IsAny<CancellationToken>())).Callback(() => calls.Add("series")).ThrowsAsync(new IOException("disk"));
        var progress = new Mock<IProgress<double>>();

        await new EasyNotifDispatchTask(dispatch.Object, series.Object, Mock.Of<IAdminAlertService>(), NullLogger<EasyNotifDispatchTask>.Instance)
            .ExecuteAsync(progress.Object, CancellationToken.None);

        Assert.Equal(["dispatch", "series"], calls);
        progress.Verify(p => p.Report(100), Times.Once);
    }

    [Fact]
    public void Identity_IsStable()
    {
        var task = new EasyNotifDispatchTask(Mock.Of<IDispatchService>(), Mock.Of<ISeriesNotifier>(), Mock.Of<IAdminAlertService>(), NullLogger<EasyNotifDispatchTask>.Instance);

        Assert.Equal("EasyNotifDispatch", task.Key);
        Assert.False(string.IsNullOrWhiteSpace(task.Name));
        Assert.False(string.IsNullOrWhiteSpace(task.Category));
        Assert.False(string.IsNullOrWhiteSpace(task.Description));
    }
}
