using Jellyfin.Plugin.EasyNotif.Alerts;
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
/// Covers <see cref="EasyNotifLog"/>: masking at every level, the WARN/ERROR relay to
/// the standard logger, <see cref="EasyNotifLog.Tail(int)"/>'s file selection and truncation, and
/// graceful degradation when the log directory cannot be created.
/// </summary>
public sealed class EasyNotifLogTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "enotif-log-tests-" + Guid.NewGuid());
    private readonly List<EasyNotifLog> _logs = [];

    private string LogsDir => Path.Combine(_tempDir, "Jellyfin.Plugin.EasyNotif", "logs");

    private (EasyNotifLog Log, CapturingLogger<EasyNotifLog> Relay) Build(int retentionDays = 30, IIncidentStore? incidents = null)
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(_tempDir);

        var configManager = new Mock<IServerConfigurationManager>();
        configManager.Setup(c => c.Configuration).Returns(new ServerConfiguration { ActivityLogRetentionDays = retentionDays });

        var relay = new CapturingLogger<EasyNotifLog>();
        var log = new EasyNotifLog(paths.Object, configManager.Object, relay, incidents ?? Mock.Of<IIncidentStore>());
        _logs.Add(log);
        return (log, relay);
    }

    private string CurrentLogFileText()
    {
        var file = Directory.GetFiles(LogsDir, "easynotif-*.log").Single();
        return File.ReadAllText(file);
    }

    [Fact]
    public void WarnAndError_AreForwardedToTheIncidentStore_Masked_InfoIsNot()
    {
        var incidents = new Mock<IIncidentStore>();
        var (log, _) = Build(incidents: incidents.Object);

        log.Info("email.sent", new Dictionary<string, object?> { ["to"] = "alice.recipient@example.org" });
        log.Warn("quota.threshold", null);
        log.Error("email.failed", new Dictionary<string, object?> { ["error"] = "rejected alice.recipient@example.org" });
        log.Dispose();

        incidents.Verify(s => s.Record("email.sent", It.IsAny<IReadOnlyDictionary<string, object?>?>()), Times.Never);
        incidents.Verify(s => s.Record("quota.threshold", null), Times.Once);
        incidents.Verify(
            s => s.Record("email.failed", It.Is<IReadOnlyDictionary<string, object?>?>(f => !((string)f!["error"]!).Contains("alice.recipient", StringComparison.Ordinal))),
            Times.Once);
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
    public void Debug_WithAnEmailField_MasksIt()
    {
        var (log, _) = Build();

        log.Debug("recap.build", new Dictionary<string, object?> { ["to"] = "alice.recipient@example.org" });
        log.Dispose();

        var text = CurrentLogFileText();
        Assert.DoesNotContain("alice.recipient@example.org", text, StringComparison.Ordinal);
        Assert.Contains("a***t@example.org", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Debug_WithAnAddressInFreeText_MasksIt()
    {
        var (log, _) = Build();

        log.Debug("email.failed", new Dictionary<string, object?> { ["error"] = "erreur pour a.b@c.d" });
        log.Dispose();

        var text = CurrentLogFileText();
        Assert.DoesNotContain("a.b@c.d", text, StringComparison.Ordinal);
        Assert.Contains("erreur pour a***b@c.d", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Error_WithAddressesInFreeText_MasksEveryOne()
    {
        var (log, _) = Build();

        log.Error("email.failed", new Dictionary<string, object?> { ["error"] = "Invalid `to` field: alice.recipient@example.org, bob.other@example.net." });
        log.Dispose();

        var text = CurrentLogFileText();
        Assert.DoesNotContain("alice.recipient@", text, StringComparison.Ordinal);
        Assert.DoesNotContain("bob.other@", text, StringComparison.Ordinal);
        Assert.Contains("a***t@example.org, b***r@example.net.", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Debug")]
    [InlineData("Info")]
    [InlineData("Warn")]
    public void UserIdField_IsShortenedAtEveryLevel(string level)
    {
        var (log, _) = Build();
        var userId = Guid.Parse("8f2c1a6e-41b0-4d0b-9a57-0c3f1e2d4b6a");
        var fields = new Dictionary<string, object?> { ["userId"] = userId, ["itemId"] = "kept-as-is" };

        switch (level)
        {
            case "Debug": log.Debug("unsubscribe.action", fields); break;
            case "Info": log.Info("unsubscribe.action", fields); break;
            default: log.Warn("unsubscribe.action", fields); break;
        }

        log.Dispose();

        var text = CurrentLogFileText();
        Assert.Contains("userId=8f2c1a6e ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("41b0", text, StringComparison.Ordinal);
        Assert.Contains("itemId=kept-as-is", text, StringComparison.Ordinal);
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
    public void Tail50_OnATenMegabyteFile_ReturnsTheExactLastLines_ReadingOnlyTheEnd()
    {
        var (log, _) = Build();
        log.Dispose();
        Directory.CreateDirectory(LogsDir);
        var path = Path.Combine(LogsDir, "easynotif-20991231.log");
        var expected = new List<string>();
        using (var writer = new StreamWriter(path, append: false, new System.Text.UTF8Encoding(false)) { NewLine = "\r\n" })
        {
            for (var i = 0; writer.BaseStream.Length < 10L * 1024 * 1024; i++)
            {
                var line = $"{i:D7} [INF] série épisode « {new string('x', i % 200)} » ✓";
                writer.WriteLine(line);
                expected.Add(line);
                writer.Flush();
            }
        }

        var tail = log.Tail(50);
        Assert.Equal(expected.TakeLast(50), tail);

        using var counting = new CountingStream(File.OpenRead(path));
        Assert.Equal(expected.TakeLast(50), EasyNotifLog.ReadLastLines(counting, 50));
        Assert.True(counting.BytesRead <= 2 * 64 * 1024, $"read {counting.BytesRead} bytes");
    }

    [Theory]
    [InlineData("", 3, new string[0])]
    [InlineData("a", 3, new[] { "a" })]
    [InlineData("a\nb\n", 3, new[] { "a", "b" })]
    [InlineData("a\r\nb\r\nc", 2, new[] { "b", "c" })]
    [InlineData("a\n\nb\n", 2, new[] { "", "b" })]
    [InlineData("﻿a\nb\n", 5, new[] { "a", "b" })]
    [InlineData("a\nb\n", 0, new string[0])]
    public void ReadLastLines_HandlesTerminatorsAndShortFiles(string content, int count, string[] expected)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));

        Assert.Equal(expected, EasyNotifLog.ReadLastLines(stream, count));
    }

    [Fact]
    public void ReadLastLines_KeepsAMultiByteCharacterSplitAcrossTwoBlocks()
    {
        // 64 Kio - 1 ASCII bytes then "é" (2 bytes): the character straddles the block boundary.
        var first = new string('a', (64 * 1024) - 1) + "é";
        var bytes = System.Text.Encoding.UTF8.GetBytes(first + "\nlast\n");
        using var stream = new MemoryStream(bytes);

        Assert.Equal(new[] { first, "last" }, EasyNotifLog.ReadLastLines(stream, 2));
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
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

        var log = new EasyNotifLog(paths.Object, configManager.Object, relay, Mock.Of<IIncidentStore>());
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
