using Jellyfin.Plugin.EasyNotif.Media;
using Jellyfin.Plugin.EasyNotif.Scheduling;
using Jellyfin.Plugin.EasyNotif.Storage;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Scheduling;

/// <summary>
/// The acceptance scenarios of CODE-13 (audit TEST-03), run tick by tick against the real store:
/// one episode gives one mail, a burst gives one mail, an upgrade gives none, a restart inside the
/// window still gives one, a deleted episode gives none, a continuous stream flushes at the maximum
/// wait.
/// </summary>
public sealed class SeriesNotificationScenarioTests : IDisposable
{
    private static readonly Guid Series = Guid.NewGuid();
    private readonly SeriesNotifierHarness _h = new();

    [Fact]
    public async Task OneEpisode_GivesOneMail_AfterTheQuietPeriod()
    {
        _h.Follow(Series);
        _h.Add(Series, 2, 5);

        await _h.RunTicksAsync(TimeSpan.FromMinutes(15));
        Assert.Empty(_h.Sent);

        await _h.RunTicksAsync(TimeSpan.FromHours(3));
        var mail = Assert.Single(_h.Sent);
        Assert.Equal("Nouveaux épisodes : Severance", mail.Subject);
        Assert.Contains("S02E05", mail.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TenEpisodesInFiveMinutes_GiveOneMail()
    {
        _h.Follow(Series);
        for (var n = 1; n <= 10; n++)
        {
            _h.Add(Series, 1, n);
            _h.Now = _h.Now.AddSeconds(30);
        }

        await _h.RunTicksAsync(TimeSpan.FromHours(3));

        var mail = Assert.Single(_h.Sent);
        Assert.Contains("10 nouveaux épisodes (S01E01 à S01E10)", mail.Text, StringComparison.Ordinal);
        Assert.False(_h.Store.HasPending);
    }

    [Fact]
    public async Task Upgrade_RemovedThenAdded_GivesNoMail()
    {
        _h.Follow(Series);
        var old = _h.Add(Series, 1, 1);
        await _h.RunTicksAsync(TimeSpan.FromHours(1));
        Assert.Single(_h.Sent);

        _h.Remove(old);
        _h.Add(Series, 1, 1);
        await _h.RunTicksAsync(TimeSpan.FromHours(3));

        Assert.Single(_h.Sent);
        Assert.False(_h.Store.HasPending);
    }

    [Fact]
    public async Task Upgrade_AddedBeforeTheOldFileIsRemoved_GivesNoMail()
    {
        // A move fires ItemAdded for the new id before ItemRemoved for the old one (measured in phase 05).
        _h.Follow(Series);
        var old = _h.Add(Series, 1, 1);
        await _h.RunTicksAsync(TimeSpan.FromHours(1));

        _h.Add(Series, 1, 1);
        _h.Remove(old);
        await _h.RunTicksAsync(TimeSpan.FromHours(3));

        Assert.Single(_h.Sent);
    }

    [Fact]
    public async Task Upgrade_OfAnEpisodeAnnouncedBeforeTheQueueExisted_GivesNoMail()
    {
        _h.Follow(Series);
        var replaced = _h.Add(Series, 3, 4);
        _h.Announced.Record(AnnouncedStore.SeriesChannel, [MediaKey.For(replaced)], _h.Now.AddDays(-30));

        await _h.RunTicksAsync(TimeSpan.FromHours(3));

        Assert.Empty(_h.Attempts);
    }

    [Fact]
    public async Task SecondCopyAlreadyInTheLibrary_GivesNoMail()
    {
        _h.Follow(Series);
        var first = _h.Add(Series, 1, 1);
        first.DateCreated = _h.Now.AddDays(-10);
        _h.Store.Drop([first.Id]);

        _h.Add(Series, 1, 1);
        await _h.RunTicksAsync(TimeSpan.FromHours(3));

        Assert.Empty(_h.Attempts);
    }

    [Fact]
    public async Task RestartDuringTheWindow_GivesOneMail_AndTheCatchUpDoesNotResendIt()
    {
        _h.Follow(Series);
        _h.Add(Series, 1, 1);
        await _h.RunTicksAsync(TimeSpan.FromMinutes(15));

        _h.Restart();
        await _h.RunTicksAsync(TimeSpan.FromHours(1));
        Assert.Single(_h.Sent);

        // The episode is still within the 24 hour catch-up window after a second restart.
        _h.Restart();
        await _h.RunTicksAsync(TimeSpan.FromHours(3));
        Assert.Single(_h.Attempts);
    }

    [Fact]
    public async Task EpisodeDeletedBeforeTheFlush_GivesNoMail()
    {
        _h.Follow(Series);
        var episode = _h.Add(Series, 1, 1);
        await _h.RunTicksAsync(TimeSpan.FromMinutes(15));

        _h.Remove(episode);
        await _h.RunTicksAsync(TimeSpan.FromHours(3));

        Assert.Empty(_h.Attempts);
    }

    [Fact]
    public async Task ContinuousStream_IsFlushedAtTheMaximumWait()
    {
        _h.Follow(Series);
        var start = _h.Now;
        DateTime? firstMail = null;
        for (var n = 1; n <= 24; n++)
        {
            _h.Add(Series, 1, n);
            await _h.RunTicksAsync(TimeSpan.FromMinutes(20));
            if (firstMail is null && _h.Sent.Count > 0)
            {
                firstMail = _h.Now;
            }
        }

        Assert.NotNull(firstMail);
        Assert.InRange(firstMail.Value - start, SeriesNotifier.MaxWait, SeriesNotifier.MaxWait + TimeSpan.FromMinutes(20));
    }

    /// <inheritdoc/>
    public void Dispose() => _h.Dispose();
}
