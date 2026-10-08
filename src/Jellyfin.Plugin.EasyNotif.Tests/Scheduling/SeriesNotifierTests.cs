using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Scheduling;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Scheduling;

/// <summary>
/// Covers <see cref="SeriesNotifier"/>: inert without a subscription, startup catch-up, the one-tick
/// metadata deferral, the access filter, the quota, the retry gate shared with the campaigns
/// (DEC-35), and the quota budget (PERF-04): one email per user and per flush, three per UTC day,
/// none into the reserve of a campaign due today.
/// </summary>
public sealed class SeriesNotifierTests : IDisposable
{
    private static readonly Guid Series = Guid.NewGuid();
    private readonly SeriesNotifierHarness _h = new();

    [Fact]
    public async Task WithoutSubscription_FlushSendsNothingAndWritesNothing()
    {
        _h.Items.Add(new Episode { Id = Guid.NewGuid(), SeriesId = Series, IndexNumber = 1, ParentIndexNumber = 1, DateCreated = _h.Now });

        await _h.RunTicksAsync(TimeSpan.FromHours(7));

        Assert.Empty(_h.Attempts);
        Assert.False(File.Exists(_h.FilePath));
    }

    [Fact]
    public async Task CatchUp_QueuesRecentEpisodesOfFollowedSeries_AtTheFirstFlush()
    {
        _h.Items.Add(new Episode { Id = Guid.NewGuid(), SeriesId = Series, SeriesName = "Severance", ParentIndexNumber = 1, IndexNumber = 2, DateCreated = _h.Now.AddHours(-1) });
        _h.Follow(Series);
        _h.Now = _h.Now.AddHours(5);
        _h.Items.Add(new Episode { Id = Guid.NewGuid(), SeriesId = Series, SeriesName = "Severance", ParentIndexNumber = 2, IndexNumber = 1, DateCreated = _h.Now.AddHours(-3) });
        _h.Items.Add(new Episode { Id = Guid.NewGuid(), SeriesId = Series, SeriesName = "Severance", ParentIndexNumber = 1, IndexNumber = 1, DateCreated = _h.Now.AddDays(-2) });

        await _h.RunTicksAsync(TimeSpan.FromMinutes(45));

        // S01E02 predates the subscription, S01E01 the 24 hour window.
        var mail = Assert.Single(_h.Sent);
        Assert.Contains("S02E01", mail.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("S01E0", mail.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingMetadata_HoldsTheBatchOneTick_ThenSendsTheFallbackLabel()
    {
        _h.Follow(Series);
        var episode = _h.Add(Series, 1, 1);
        episode.IndexNumber = null;

        await _h.RunTicksAsync(TimeSpan.FromMinutes(30));
        Assert.Empty(_h.Attempts);
        Assert.Equal(1, _h.Store.Pending().Single().Attempts);

        await _h.RunTicksAsync(TimeSpan.FromMinutes(15));
        Assert.Contains("Nouvel épisode", Assert.Single(_h.Sent).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HiddenEpisode_IsNeverSent_OthersAre()
    {
        _h.Follow(Series);
        _h.Add(Series, 1, 1);
        var hidden = _h.Add(Series, 1, 2);
        _h.Hidden.Add(hidden.Id);

        await _h.RunTicksAsync(TimeSpan.FromMinutes(30));

        var mail = Assert.Single(_h.Sent);
        Assert.Contains("S01E01", mail.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("S01E02", mail.Text, StringComparison.Ordinal);
        Assert.False(_h.Store.HasPending);
    }

    [Fact]
    public async Task QuotaOver_PostponesWithoutLoss()
    {
        _h.Follow(Series);
        _h.Add(Series, 1, 1);
        _h.QuotaOver = true;

        await _h.RunTicksAsync(TimeSpan.FromHours(1));
        Assert.Empty(_h.Attempts);
        Assert.True(_h.Store.HasPending);

        _h.QuotaOver = false;
        await _h.RunTicksAsync(TimeSpan.FromMinutes(15));
        Assert.Single(_h.Sent);
    }

    [Fact]
    public async Task TransientFailure_IsRetriedAfterTheBackoff_WithTheSameKey()
    {
        var user = _h.Follow(Series);
        _h.Add(Series, 1, 1);
        _h.NextResult = _ => new SendResult(false, null, 503, "unavailable");

        await _h.RunTicksAsync(TimeSpan.FromMinutes(30));
        var entry = Assert.Single(_h.Retries.Entries);
        Assert.Equal(SeriesNotifier.RetryPrefix + user.ToString("N"), entry.KeyPrefix);
        Assert.Equal(_h.Now.AddMinutes(15), entry.NextAttemptUtc);
        Assert.True(_h.Store.HasPending);

        _h.NextResult = null;
        await _h.RunTicksAsync(TimeSpan.FromMinutes(15));

        Assert.Equal(2, _h.Attempts.Count);
        Assert.Equal(_h.Attempts[0].IdempotencyKey, _h.Attempts[1].IdempotencyKey);
        Assert.Single(_h.Sent);
        Assert.Empty(_h.Retries.Entries);
        Assert.False(_h.Store.HasPending);
    }

    [Fact]
    public async Task TransientFailures_AreAbandonedAfterFiveAttempts()
    {
        _h.Follow(Series);
        _h.Add(Series, 1, 1);
        _h.NextResult = _ => new SendResult(false, null, 0, "timeout");

        await _h.RunTicksAsync(TimeSpan.FromHours(20));

        Assert.Equal(5, _h.Attempts.Count);
        Assert.Empty(_h.Retries.Entries);
        Assert.False(_h.Store.HasPending);
        Assert.Contains(_h.Log.Entries, e => e.EventType == "series.retry-abandoned");
    }

    [Fact]
    public async Task Rejection_IsNotRetried()
    {
        _h.Follow(Series);
        _h.Add(Series, 1, 1);
        _h.NextResult = _ => new SendResult(false, null, 422, "invalid");

        await _h.RunTicksAsync(TimeSpan.FromHours(2));

        Assert.Single(_h.Attempts);
        Assert.Empty(_h.Retries.Entries);
        Assert.False(_h.Store.HasPending);
    }

    [Fact]
    public async Task UserWithoutAddress_IsSkipped_AndTheEpisodeLeavesTheQueue()
    {
        var user = _h.Follow(Series);
        _h.Emails.Remove(user);
        _h.Add(Series, 1, 1);

        await _h.RunTicksAsync(TimeSpan.FromMinutes(30));

        Assert.Empty(_h.Attempts);
        Assert.False(_h.Store.HasPending);
    }

    [Fact]
    public async Task EachUserHasTheirOwnWindow()
    {
        var other = Guid.NewGuid();
        var early = _h.Follow(Series, "early@example.org");
        _h.Add(Series, 1, 1);
        await _h.RunTicksAsync(TimeSpan.FromMinutes(15));

        // The second user also follows another series, which keeps receiving episodes.
        var late = _h.Follow(other, "late@example.org");
        _h.Store.Subscribe(late, Series, "bell", _h.Now);
        _h.Add(other, 1, 1, "Andor");
        await _h.RunTicksAsync(TimeSpan.FromMinutes(15));

        Assert.Equal(["early@example.org"], _h.Sent.Select(m => m.To));
        Assert.Single(_h.Store.Pending(), p => p.SeriesId == Series && p.Notified.Contains(early));

        await _h.RunTicksAsync(TimeSpan.FromMinutes(30));
        var mail = Assert.Single(_h.Sent, m => m.To == "late@example.org");
        Assert.Contains("Severance", mail.Text, StringComparison.Ordinal);
        Assert.Contains("Andor", mail.Text, StringComparison.Ordinal);
        Assert.False(_h.Store.HasPending);
    }

    /// <inheritdoc/>
    [Fact]
    public async Task OneFlush_SendsOneEmailPerUser_AcrossTheirSeries()
    {
        var user = _h.Follow(Series);
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        _h.Store.Subscribe(user, second, "bell", _h.Now);
        _h.Store.Subscribe(user, third, "bell", _h.Now);
        _h.Add(Series, 1, 1);
        _h.Add(Series, 1, 2);
        _h.Add(second, 2, 1, "Andor");
        _h.Add(second, 2, 2, "Andor");
        _h.Add(third, 1, 1, "Slow Horses");

        await _h.RunTicksAsync(TimeSpan.FromMinutes(30));

        var mail = Assert.Single(_h.Sent);
        Assert.Contains("Andor", mail.Text, StringComparison.Ordinal);
        Assert.Contains("Slow Horses", mail.Text, StringComparison.Ordinal);
        Assert.False(_h.Store.HasPending);
    }

    [Fact]
    public async Task FourthEmailOfTheDay_IsHeldUntilTheNextUtcDay()
    {
        _h.Follow(Series);
        for (var number = 1; number <= 3; number++)
        {
            _h.Add(Series, 1, number);
            await _h.RunTicksAsync(TimeSpan.FromMinutes(30));
        }

        Assert.Equal(SeriesNotifier.MaxPerUserPerDay, _h.Sent.Count);

        _h.Add(Series, 1, 4);
        _h.Add(Series, 1, 5);
        await _h.RunTicksAsync(TimeSpan.FromHours(6));
        Assert.Equal(3, _h.Sent.Count);
        Assert.True(_h.Store.HasPending);
        Assert.Contains(_h.Log.Entries, e => e.EventType == "series.flush" && (int?)e.Fields?["capped"] == 1);

        _h.Now = _h.Now.Date.AddDays(1).AddMinutes(-15);
        await _h.RunTicksAsync(TimeSpan.FromMinutes(15));

        Assert.Equal(4, _h.Sent.Count);
        Assert.Contains("S01E04", _h.Sent[3].Text, StringComparison.Ordinal);
        Assert.Contains("S01E05", _h.Sent[3].Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(8, 60, false)]
    [InlineData(8, 59, true)]
    [InlineData(20, 99, true)]
    public async Task ReserveOfACampaignDueToday_StopsTheSeriesEmails(int campaignInHours, int dailyToday, bool sent)
    {
        _h.Campaigns.Campaigns.Add(new Campaign { Id = "newsletter", Type = CampaignType.Newsletter, Enabled = true, NextRunUtc = _h.Now.AddHours(campaignInHours) });
        _h.DailyToday = dailyToday;
        _h.Follow(Series);
        _h.Add(Series, 1, 1);

        await _h.RunTicksAsync(TimeSpan.FromMinutes(30));

        Assert.Equal(sent ? 1 : 0, _h.Sent.Count);
        Assert.Equal(!sent, _h.Store.HasPending);
        if (!sent)
        {
            Assert.Contains(_h.Log.Entries, e => e.EventType == "series.skipped" && (string?)e.Fields?["reason"] == "quota-reserve");
        }
    }

    public void Dispose() => _h.Dispose();
}
