using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Email;

/// <summary>
/// Covers <see cref="SendLog"/>: append and read-back, status update by Resend id, the 500-entry
/// cap, resilience to a single corrupt line, the last-webhook cursor, and R15 (a raw address is
/// never written).
/// </summary>
public sealed class SendLogTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "enotif-sendlog-tests-" + Guid.NewGuid());
    private readonly List<SendLog> _logs = [];
    private DateTimeOffset _now = new(2026, 6, 15, 9, 0, 0, TimeSpan.Zero);

    private string DataDir => Path.Combine(_tempDir, "Jellyfin.Plugin.EasyNotif");

    private string FilePath => Path.Combine(DataDir, "send-log.jsonl");

    private SendLog Build()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(_tempDir);
        var log = new SendLog(paths.Object, new FileSystem(), NullLogger<SendLog>.Instance, () => _now);
        _logs.Add(log);
        return log;
    }

    private static SendLogEntry Entry(string? resendId = "re_1", string status = "sent") => new()
    {
        Ts = new DateTime(2026, 6, 15, 9, 0, 0, DateTimeKind.Utc),
        Context = "test",
        ToMasked = "a***e@example.org",
        Subject = "Hello",
        ResendId = resendId,
        Status = status
    };

    [Fact]
    public void Append_ThenReadBackFromANewInstance()
    {
        Build().Append(Entry());

        var recent = Build().Recent(10);

        var stored = Assert.Single(recent);
        Assert.Equal("re_1", stored.ResendId);
        Assert.Equal("sent", stored.Status);
    }

    [Fact]
    public void UpdateStatus_KnownId_ReturnsTrue_AndChangesTheStatus()
    {
        var log = Build();
        log.Append(Entry());

        var updated = log.UpdateStatus("re_1", "email.delivered");

        Assert.True(updated);
        Assert.Equal("email.delivered", log.Recent(1)[0].Status);
    }

    [Fact]
    public void UpdateStatus_UnknownId_ReturnsFalse_AndLeavesTheFile()
    {
        var log = Build();
        log.Append(Entry());
        var before = File.ReadAllText(FilePath);

        var updated = log.UpdateStatus("re_unknown", "email.bounced");

        Assert.False(updated);
        Assert.Equal(before, File.ReadAllText(FilePath));
    }

    [Fact]
    public void Append_CapsAtThe500MostRecentEntries()
    {
        var log = Build();
        for (var i = 0; i < 501; i++)
        {
            log.Append(Entry(resendId: "re_" + i));
        }

        var all = log.Recent(1000);
        Assert.Equal(500, all.Count);
        Assert.DoesNotContain(all, e => e.ResendId == "re_0");
        Assert.Contains(all, e => e.ResendId == "re_500");
    }

    [Fact]
    public void Construction_SkipsACorruptLine_KeepsTheRest()
    {
        Directory.CreateDirectory(DataDir);
        var good1 = System.Text.Json.JsonSerializer.Serialize(Entry(resendId: "re_a"));
        var good2 = System.Text.Json.JsonSerializer.Serialize(Entry(resendId: "re_b"));
        File.WriteAllText(FilePath, good1 + "\n{ broken line\n" + good2);

        var recent = Build().Recent(10);

        Assert.Equal(2, recent.Count);
        Assert.Contains(recent, e => e.ResendId == "re_a");
        Assert.Contains(recent, e => e.ResendId == "re_b");
    }

    [Fact]
    public void LastWebhook_ReturnsTheMostRecentStatusUpdate()
    {
        var log = Build();
        log.Append(Entry(resendId: "re_1"));
        log.Append(Entry(resendId: "re_2"));

        _now = _now.AddMinutes(5);
        log.UpdateStatus("re_1", "email.sent");
        _now = _now.AddMinutes(5);
        log.UpdateStatus("re_2", "email.delivered");

        var (utc, type) = log.LastWebhook();
        Assert.Equal(_now.UtcDateTime, utc);
        Assert.Equal("email.delivered", type);
    }

    [Fact]
    public void Append_NeverWritesARawAddress()
    {
        const string RawAddress = "someone.private@gmail.com";
        var log = Build();

        log.Append(Entry() with { ToMasked = Jellyfin.Plugin.EasyNotif.Util.EmailMasker.Mask(RawAddress) });

        Assert.DoesNotContain(RawAddress, File.ReadAllText(FilePath), StringComparison.Ordinal);
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
