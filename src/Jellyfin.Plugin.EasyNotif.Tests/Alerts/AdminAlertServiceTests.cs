using Jellyfin.Plugin.EasyNotif.Alerts;
using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Alerts;

/// <summary>
/// Covers <see cref="AdminAlertService"/> and <see cref="AdminAlertMail"/>: one incident produces a
/// digest, two the same day only one, a new day a new one; a refused send leaves the day open and
/// raises no incident about itself; no address, no digest; the mail renders in French and English.
/// </summary>
public sealed class AdminAlertServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "enotif-alerts-tests-" + Guid.NewGuid());
    private readonly IncidentStore _incidents;
    private readonly FakeConfigAccessor _config = new(new PluginConfiguration { AlertEmail = "admin@example.org", SchedulerTimeZone = "Europe/Paris" });
    private readonly Mock<IEmailSender> _sender = new();
    private readonly List<EmailMessage> _sent = [];
    private DateTime _now = new(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc);
    private SendResult _result = new(true, "re_1", 200, null);

    public AdminAlertServiceTests()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(_tempDir);
        _incidents = new IncidentStore(paths.Object, new FileSystem(), NullLogger<IncidentStore>.Instance, () => _now);
        _sender.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((m, _) =>
            {
                _sent.Add(m);
                if (!_result.Success)
                {
                    // What the real sender does on a refusal: an email.failed error in the plugin log.
                    _incidents.Record("email.failed", new Dictionary<string, object?> { ["error"] = _result.Error });
                }
            })
            .ReturnsAsync(() => _result);
    }

    private AdminAlertService Service() => new(_incidents, _config, _sender.Object, new FakeEasyNotifLog(), () => _now);

    [Fact]
    public async Task OneIncident_SendsOneDigest_ToTheAlertAddress()
    {
        _incidents.Record("dispatch.error", new Dictionary<string, object?> { ["campaignId"] = "newsletter" });

        Assert.True(await Service().RunAsync(CancellationToken.None));

        var mail = Assert.Single(_sent);
        Assert.Equal("admin@example.org", mail.To);
        Assert.Contains("Campagne en erreur", mail.Html, StringComparison.Ordinal);
        Assert.Contains("campaignId=newsletter", mail.Html, StringComparison.Ordinal);
        Assert.StartsWith("alert:", mail.IdempotencyKey, StringComparison.Ordinal);
        Assert.Equal(_now, _incidents.LastDigestUtc);
    }

    [Fact]
    public async Task TwoIncidentsTheSameDay_SendOneDigest_TheNextDaySendsAgain()
    {
        _incidents.Record("drain.lost", null);
        await Service().RunAsync(CancellationToken.None);

        _now = _now.AddHours(3);
        _incidents.Record("quota.threshold", null);
        Assert.False(await Service().RunAsync(CancellationToken.None));
        Assert.Single(_sent);

        // 22:30 UTC on the 9th is already 00:30 on the 10th in Paris (UTC+2): a new local day.
        _now = new DateTime(2026, 10, 9, 22, 30, 0, DateTimeKind.Utc);
        Assert.True(await Service().RunAsync(CancellationToken.None));
        Assert.Equal(2, _sent.Count);
        Assert.Contains("80 % d'une limite", _sent[1].Html, StringComparison.Ordinal);
        Assert.DoesNotContain("Lot perdu", _sent[1].Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusedSend_LeavesTheDayOpen_AndRaisesNoIncidentAboutItself()
    {
        _incidents.Record("email.failed", new Dictionary<string, object?> { ["error"] = "API key is invalid" });
        _result = new SendResult(false, null, 401, "API key is invalid");

        Assert.False(await Service().RunAsync(CancellationToken.None));
        Assert.Null(_incidents.LastDigestUtc);
        Assert.Single(_incidents.All());

        // The key is repaired: the next tick, the same day, sends the digest.
        _now = _now.AddMinutes(15);
        _result = new SendResult(true, "re_2", 200, null);
        Assert.True(await Service().RunAsync(CancellationToken.None));
        Assert.Equal(2, _sent.Count);
        Assert.NotEqual(_sent[0].IdempotencyKey, _sent[1].IdempotencyKey);
        Assert.Equal(_now, _incidents.LastDigestUtc);
    }

    [Fact]
    public async Task NoAddress_OrNoNewIncident_SendsNothing()
    {
        Assert.False(await Service().RunAsync(CancellationToken.None));

        _config.Config.AlertEmail = null;
        _incidents.Record("drain.lost", null);
        Assert.False(await Service().RunAsync(CancellationToken.None));
        Assert.Empty(_sent);
    }

    [Theory]
    [InlineData("fr", "Easy Notif - 3 incident(s) à vérifier", "Envoi refusé ou en échec")]
    [InlineData("en", "Easy Notif - 3 incident(s) to check", "Send refused or failed")]
    public void Mail_GroupsByType_InEachLanguage(string lang, string subject, string label)
    {
        var t = new DateTime(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc);
        var (actualSubject, html) = AdminAlertMail.Build(lang, [
            new Incident(t, "email.failed", "error=<script>"),
            new Incident(t.AddMinutes(5), "email.failed", "error=second"),
            new Incident(t.AddMinutes(1), "drain.lost", null)
        ]);

        Assert.Equal(subject, actualSubject);
        Assert.Contains(label, html, StringComparison.Ordinal);
        Assert.Contains("<td>2</td><td>2026-10-09 08:00</td><td>2026-10-09 08:05</td><td>error=second</td>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.DoesNotContain((char)0x2014, html); // no em dash in user-facing text (R11)
    }

    public void Dispose()
    {
        _incidents.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
