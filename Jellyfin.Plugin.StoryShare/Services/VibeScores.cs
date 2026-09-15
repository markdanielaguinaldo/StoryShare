using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.StoryShare.Services;

/// <summary>Deterministic, local-only playful scores for one library item.</summary>
internal sealed record VibeScore(string Label, int Value, string Verdict);

internal static class VibeScores
{
    public static IReadOnlyList<VibeScore> For(BaseItem item)
    {
        var genres = new HashSet<string>(item.Genres ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var minutes = item.RunTimeTicks is > 0 ? TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalMinutes : 100d;
        var rating = item.CommunityRating ?? 6f;
        var variance = StableVariance(item.Id.ToString());

        return new[]
        {
            Make("CHAOS", 25 + Weigh(genres, ("Action", 22), ("Adventure", 14), ("Thriller", 18), ("Crime", 12), ("Horror", 24)) + (minutes < 110 ? 6 : 0) + variance, "Take a breath", "Buckle up", "Secure loose objects"),
            Make("TEARS", 22 + Weigh(genres, ("Drama", 24), ("Romance", 22), ("Animation", 10)) + (rating >= 8 ? 10 : rating >= 7 ? 5 : 0) - variance, "Dry eyes", "Feelings possible", "Hydrate first"),
            Make("SNACKS REQUIRED", 25 + Runtime(minutes, 8, 18, 28) + Weigh(genres, ("Comedy", 12), ("Adventure", 10), ("Family", 16)) + variance, "Optional popcorn", "Stock the couch", "Bring snacks"),
            Make("PLOT TWISTS", 20 + Weigh(genres, ("Mystery", 28), ("Thriller", 22), ("Crime", 14), ("Science Fiction", 12), ("Sci-Fi", 12)) + (rating >= 8 ? 5 : 0) - variance, "Straightforward-ish", "Keep watching", "Trust nobody"),
            Make("COUCH-LOCK", 25 + Runtime(minutes, 10, 24, 34) + Weigh(genres, ("Drama", 12), ("Comedy", 10), ("Family", 10)) + (item.ParentId != Guid.Empty ? 7 : 0) + variance, "Easy escape", "Settle in", "Cancel plans")
        };
    }

    private static VibeScore Make(string label, int raw, string low, string moderate, string extreme)
    {
        var value = Math.Clamp(raw, 15, 95);
        return new(label, value, value >= 75 ? extreme : value >= 55 ? moderate : value >= 35 ? "Moderate vibes" : low);
    }

    private static int Weigh(HashSet<string> genres, params (string Genre, int Score)[] weights) => weights.Where(weight => genres.Contains(weight.Genre)).Sum(weight => weight.Score);
    private static int Runtime(double minutes, int shortScore, int mediumScore, int longScore) => minutes >= 150 ? longScore : minutes >= 120 ? mediumScore : minutes >= 90 ? shortScore : 0;
    private static int StableVariance(string id) => string.IsNullOrEmpty(id) ? 0 : (id.Aggregate(17, (hash, c) => unchecked((hash * 31) + c)) % 11) - 5;
}
