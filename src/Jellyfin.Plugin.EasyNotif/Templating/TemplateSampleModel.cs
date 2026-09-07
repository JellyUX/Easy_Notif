namespace Jellyfin.Plugin.EasyNotif.Templating;

/// <summary>
/// Canned demo models for the admin template editor preview, one per base template id. The keys
/// mirror what the real composers pass so a preview looks like a real email.
/// </summary>
public static class TemplateSampleModel
{
    /// <summary>Returns the sample model for a base template id.</summary>
    /// <param name="baseId">The base id (<c>newsletter</c> or <c>weekly-recap</c>).</param>
    /// <returns>The model.</returns>
    public static IReadOnlyDictionary<string, object?> For(string baseId)
        => baseId == "weekly-recap" ? WeeklyRecap() : Newsletter();

    private static Dictionary<string, object?> Newsletter() => new()
    {
        ["heading"] = "New on Lili serveur",
        ["summary"] = "3 additions",
        ["isEmpty"] = false,
        ["hasMovies"] = true,
        ["hasSeries"] = true,
        ["unsubscribeUrl"] = "https://media.example.org/EasyNotif/u/sample-token?lang=en",
        ["movies"] = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["title"] = "Dune",
                ["year"] = 2021,
                ["genres"] = "Science Fiction, Adventure",
                ["overview"] = "A noble family becomes embroiled in a war for control over the galaxy's most valuable asset.",
                ["posterUrl"] = "https://media.example.org/sample-poster.jpg",
                ["detailUrl"] = "https://media.example.org/web/#/details?id=sample",
                ["noLink"] = false
            }
        },
        ["series"] = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["title"] = "The Bear",
                ["episodesLabel"] = "4 new episodes",
                ["seasonsLabel"] = "Season 3",
                ["overview"] = "A young chef from the fine dining world returns home to run a sandwich shop.",
                ["posterUrl"] = "https://media.example.org/sample-poster-2.jpg",
                ["detailUrl"] = "https://media.example.org/web/#/details?id=sample-2",
                ["noLink"] = false
            }
        }
    };

    private static Dictionary<string, object?> WeeklyRecap() => new()
    {
        ["greeting"] = "Hi Alex",
        ["heading"] = "Your week on Lili serveur",
        ["quietWeek"] = false,
        ["hasWatched"] = true,
        ["yearTotal"] = 42,
        ["yearCompleted"] = 31,
        ["partialSince"] = "2026-03-15",
        ["unsubscribeUrl"] = "https://media.example.org/EasyNotif/u/sample-token?lang=en",
        ["watched"] = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["title"] = "Heat",
                ["seriesTitle"] = null,
                ["isSeries"] = false,
                ["isSingle"] = true,
                ["episodeCount"] = 1,
                ["when"] = "2026-06-12",
                ["completed"] = true
            },
            new()
            {
                ["title"] = "The Wire",
                ["seriesTitle"] = null,
                ["isSeries"] = true,
                ["isSingle"] = false,
                ["episodeCount"] = 3,
                ["when"] = "2026-06-14",
                ["completed"] = true
            }
        }
    };
}
