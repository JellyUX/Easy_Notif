using Jellyfin.Plugin.EasyNotif.Logging;
using Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Logging;

/// <summary>
/// Covers <see cref="EasyNotifLog"/>: masking at every level except Debug, the WARN/ERROR relay to
/// the standard logger, <see cref="EasyNotifLog.Tail(int)"/>'s file selection and truncation, and
/// graceful degradation when the log directory cannot be created.
/// </summary>
public sealed class EasyNotifLogTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "enotif-log-tests-" + Guid.NewGuid());
    private readonly List<EasyNotifLog> _logs = [];

    private string LogsDir => Path.Combine(_tempDir, "Jellyfin.Plugin.EasyNotif", "logs");

    private (EasyNotifLog Log, CapturingLogger<EasyNotifLog> Relay) Build(int retentionDays = 30)
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(_tempDir);

        var configManager = new Mock<IServerConfigurationManager>();
        configManager.Setup(c => c.Configuration).Returns(new ServerConfiguration { ActivityLogRetentionDays = retentionDays });

        var relay = new CapturingLogger<EasyNotifLog>();
        var log = new EasyNotifLog(paths.Object, configManager.Object, relay);
        _logs.Add(log);
        return (log, relay);
    }

    private string CurrentLogFileText()
    {
        var file = Directory.GetFiles(LogsDir, "easynotif-*.log").Single();
        return File.ReadAllText(file);
    }

    [Fact]
    public void Info_WithAnEmailField_MasksIt()
    {
        var (log, _) = Build();

        log.Info("email.sent", new Dictionary<string, object?> { ["to"] = "alice.recipient@example.org" });
        log.Dispose();

        var text = CurrentLogFileText();
        Assert.DoesNotContain("alice.recipient@example.org", text, StringComparison.Ordinal);
        Assert.Contains("a***t@example.org", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Debug_WithAnEmailField_WritesItUnmasked()
    {
        var (log, _) = Build();

        log.Debug("recap.build", new Dictionary<string, object?> { ["to"] = "alice.recipient@example.org" });
        log.Dispose();

        Assert.Contains("alice.recipient@example.org", CurrentLogFileText(), StringComparison.Ordinal);
    }

    [Fact]
    public void WarnAndError_AreRelayedToTheStandardLogger_InfoAndDebugAreNot()
    {
        var (log, relay) = Build();

        log.Debug("a", null);
        log.Info("b", null);
        log.Warn("c", null);
        log.Error("d", null);

        Assert.Equal(2, relay.Entries.Count);
        Assert.Contains(relay.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("[EasyNotif] c", StringComparison.Ordinal));
        Assert.Contains(relay.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("[EasyNotif] d", StringComparison.Ordinal));
    }

    [Fact]
    public void Tail_ReturnsTheLastLinesInOrder_AndTruncatesCleanly()
    {
        var (log, _) = Build();
        for (var i = 0; i < 5; i++)
        {
            log.Info("tick", new Dictionary<string, object?> { ["i"] = i });
        }

        log.Dispose();

        var last3 = log.Tail(3);
        Assert.Equal(3, last3.Count);
        Assert.Contains("i=2", last3[0], StringComparison.Ordinal);
        Assert.Contains("i=4", last3[2], StringComparison.Ordinal);

        Assert.Equal(5, log.Tail(100).Count);
    }

    [Fact]
    public void Tail_WhenTheLogsDirectoryDoesNotExist_ReturnsEmpty()
    {
        var (log, _) = Build();

        Assert.Empty(log.Tail(10));
    }

    [Fact]
    public void Tail_WithSeveralDatedFiles_PicksTheMostRecent()
    {
        var (log, _) = Build();
        log.Info("warmup");
        log.Dispose();

        Directory.CreateDirectory(LogsDir);
        File.WriteAllText(Path.Combine(LogsDir, "easynotif-20200101.log"), "old line\n");
        File.WriteAllText(Path.Combine(LogsDir, "easynotif-20301231.log"), "future line\n");

        var (fresh, _) = Build();
        var tail = fresh.Tail(10);

        Assert.Contains(tail, l => l.Contains("future line", StringComparison.Ordinal));
        Assert.DoesNotContain(tail, l => l.Contains("old line", StringComparison.Ordinal));
    }

    [Fact]
    public void Construction_WhenTheLogsDirectoryCannotBeCreated_DegradesWithoutThrowing()
    {
        // A file already sitting where the plugin data directory needs to be forces
        // Directory.CreateDirectory to throw IOException.
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(Path.Combine(_tempDir, "Jellyfin.Plugin.EasyNotif"), "blocking file");

        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(_tempDir);
        var configManager = new Mock<IServerConfigurationManager>();
        configManager.Setup(c => c.Configuration).Returns(new ServerConfiguration());
        var relay = new CapturingLogger<EasyNotifLog>();

        var log = new EasyNotifLog(paths.Object, configManager.Object, relay);
        _logs.Add(log);

        log.Info("should.be.dropped");
        log.Debug("also.dropped");
        Assert.Empty(log.Tail(10));

        var beforeCount = relay.Entries.Count;
        log.Warn("still.reaches.relay");
        log.Error("this.too");
        Assert.Equal(beforeCount + 2, relay.Entries.Count);
        Assert.Contains(relay.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Could not open the dedicated log file", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(45, 45)]
    public void ClampRetentionDays_NeverGoesBelowOne(int input, int expected)
    {
        Assert.Equal(expected, EasyNotifLog.ClampRetentionDays(input));
    }

    public void Dispose()
    {
        foreach (var log in _logs)
        {
            log.Dispose();
        }

        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
