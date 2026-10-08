using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Media;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Scheduling;
using Jellyfin.Plugin.EasyNotif.Services;
using Jellyfin.Plugin.EasyNotif.Storage;
using Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.EasyNotif.Tests.Scheduling;

/// <summary>
/// Wires a <see cref="SeriesNotifier"/> to a real <see cref="SeriesNotifyStore"/> in a temp directory,
/// a simulated library, a settable clock and a sender that deduplicates by idempotency key like the
/// <c>sent-keys</c> registry. <see cref="Restart"/> rebuilds the store and notifier from disk.
/// </summary>
internal sealed class SeriesNotifierHarness : IDisposable
{
    public static readonly DateTime T0 = new(2026, 6, 15, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "enotif-seriesnotifier-tests-" + Guid.NewGuid());
    private readonly List<IDisposable> _disposables = [];

    public SeriesNotifierHarness()
    {
        Library
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                if (q.ItemIds is { Length: > 0 } ids)
                {
                    return Items.Where(i => ids.Contains(i.Id)).ToList<BaseItem>();
                }

                if (q.AncestorIds is { Length: > 0 } ancestors)
                {
                    return Items.Where(e => ancestors.Contains(e.SeriesId)).ToList<BaseItem>();
                }

                return Items.OrderByDescending(e => e.DateCreated).ToList<BaseItem>();
            });
        Access
            .Setup(a => a.VisibleTo(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>()))
            .Returns<Guid, IReadOnlyCollection<Guid>>((_, ids) => ids.Where(id => !Hidden.Contains(id)).ToHashSet());
        Preferences
            .Setup(p => p.GetContactable(It.IsAny<IReadOnlyCollection<Guid>?>(), It.IsAny<bool>()))
            .Returns<IReadOnlyCollection<Guid>?, bool>((ids, _) =>
                [.. Emails.Where(kv => ids is null || ids.Contains(kv.Key)).Select(kv => new Recipient(kv.Key, kv.Value))]);
        Preferences
            .Setup(p => p.GetOrCreate(It.IsAny<Guid>()))
            .Returns<Guid>(id => new UserPreference { UserId = id, EpisodesOptOut = OptedOut.Contains(id) });
        Quota.Setup(q => q.Snapshot()).Returns(() => new QuotaSnapshot(0, DailyToday, 3000, 100, false, QuotaOver));
        Sender
            .Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns<EmailMessage, CancellationToken>((message, _) =>
            {
                Attempts.Add(message);
                var result = NextResult?.Invoke(message) ?? new SendResult(true, "re_" + Attempts.Count, 200, null);
                if (result.Success && !SentKeys.Add(message.IdempotencyKey!))
                {
                    return Task.FromResult(new SendResult(false, null, 0, null) { Deduplicated = true });
                }

                if (result.Success)
                {
                    Sent.Add(message);
                }

                return Task.FromResult(result);
            });
        Restart();
    }

    public DateTime Now { get; set; } = T0;

    public List<Episode> Items { get; } = [];

    public HashSet<Guid> Hidden { get; } = [];

    public Dictionary<Guid, string> Emails { get; } = [];

    public HashSet<Guid> OptedOut { get; } = [];

    public bool QuotaOver { get; set; }

    public int DailyToday { get; set; }

    public Func<EmailMessage, SendResult>? NextResult { get; set; }

    public List<EmailMessage> Attempts { get; } = [];

    public List<EmailMessage> Sent { get; } = [];

    public HashSet<string> SentKeys { get; } = [];

    public Mock<ILibraryManager> Library { get; } = new();

    public Mock<IAccessFilter> Access { get; } = new();

    public Mock<IPreferenceService> Preferences { get; } = new();

    public Mock<IQuotaGuard> Quota { get; } = new();

    public Mock<IEmailSender> Sender { get; } = new();

    public Mock<ISendLog> SendLog { get; } = new();

    public FakeAnnouncedStore Announced { get; } = new();

    public FakeRetryQueueStore Retries { get; } = new();

    public FakeEasyNotifLog Log { get; } = new();

    public FakeSecretStore Secrets { get; } = new(resendApiKey: "re_key");

    public FakeConfigAccessor Config { get; } = new(new PluginConfiguration { FromEmail = "notif@example.org" });

    public FakeCampaignStore Campaigns { get; } = new();

    public Mock<IPosterCache> PosterCache { get; } = new();

    public TemplateStore Templates { get; } = TestTemplateStore.Create();

    public SeriesNotifyStore Store { get; private set; } = null!;

    public SeriesNotifier Notifier { get; private set; } = null!;

    public string FilePath => Path.Combine(_tempDir, "Jellyfin.Plugin.EasyNotif", "series-notify.json");

    /// <summary>Rebuilds the store from disk and a fresh notifier, as a server restart would.</summary>
    public void Restart()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(_tempDir);
        Store = new SeriesNotifyStore(paths.Object, new FileSystem(), NullLogger<SeriesNotifyStore>.Instance);
        _disposables.Add(Store);
        Notifier = new SeriesNotifier(
            Store,
            Library.Object,
            Announced,
            Access.Object,
            Preferences.Object,
            Retries,
            Campaigns,
            Sender.Object,
            Quota.Object,
            SendLog.Object,
            Config,
            Secrets,
            Templates,
            PosterCache.Object,
            new ServerLinkContext("test-system", "Test server"),
            Log,
            NullLogger<SeriesNotifier>.Instance,
            () => Now);
    }

    /// <summary>Subscribes a user with an address to a series.</summary>
    public Guid Follow(Guid seriesId, string email = "user@example.org")
    {
        var userId = Guid.NewGuid();
        Emails[userId] = email;
        Store.Subscribe(userId, seriesId, "bell", Now);
        return userId;
    }

    /// <summary>Adds an episode to the library, then queues it as its <c>ItemAdded</c> event would, at <see cref="Now"/>.</summary>
    public Episode Add(Guid seriesId, int season, int number, string seriesName = "Severance")
    {
        var episode = new Episode
        {
            Id = Guid.NewGuid(),
            SeriesId = seriesId,
            SeriesName = seriesName,
            ParentIndexNumber = season,
            IndexNumber = number,
            DateCreated = Now
        };
        Items.Add(episode);
        Store.Enqueue([(episode.Id, seriesId)], Now);
        return episode;
    }

    /// <summary>Removes an episode from the library, as its <c>ItemRemoved</c> event would.</summary>
    public void Remove(Episode episode)
    {
        Items.Remove(episode);
        Store.RecordRemoved(episode.Id, MediaKey.For(episode), Now);
    }

    /// <summary>Advances the clock by <paramref name="by"/>, one 15 minute tick at a time, flushing at each tick.</summary>
    public async Task RunTicksAsync(TimeSpan by)
    {
        var end = Now + by;
        while (Now < end)
        {
            Now = Now.AddMinutes(15) > end ? end : Now.AddMinutes(15);
            await Notifier.FlushAsync(CancellationToken.None);
        }
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
