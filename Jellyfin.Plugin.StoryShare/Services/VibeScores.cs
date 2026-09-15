using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.StoryShare.Services;

/// <summary>Deterministic, local-only playful scores for one library item.</summary>
internal sealed record VibeScore(
    string Label,
    int Value,
    string Verdict,
    IReadOnlyList<string> LowCaptions,
    IReadOnlyList<string> MidCaptions,
    IReadOnlyList<string> HighCaptions);

internal static class VibeScores
{
    public static IReadOnlyList<VibeScore> For(BaseItem item, IReadOnlyList<int>? overrides = null)
    {
        var genres = new HashSet<string>(item.Genres ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var minutes = item.RunTimeTicks is > 0 ? TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalMinutes : 100d;
        var rating = item.CommunityRating ?? 6f;
        var variance = StableVariance(item.Id.ToString());

        var scores = new[]
        {
            Make("EXCITEMENT", 25 + Weigh(genres, ("Action", 22), ("Adventure", 14), ("Thriller", 18), ("Crime", 12), ("Horror", 24)) + (minutes < 110 ? 6 : 0) + variance,
                new[] { "Take a breath", "Easy does it", "Calm before the storm" }, new[] { "Buckle up", "Things are heating up", "Expect fireworks" }, new[] { "Hold on tight", "Maximum excitement", "Secure loose objects" }),
            Make("TEARS", 22 + Weigh(genres, ("Drama", 24), ("Romance", 22), ("Animation", 10)) + (rating >= 8 ? 10 : rating >= 7 ? 5 : 0) - variance,
                new[] { "Dry eyes", "Tissues optional", "All clear so far" }, new[] { "Feelings possible", "Keep tissues nearby", "A little emotional" }, new[] { "Hydrate first", "Emotional damage", "Tissues recommended" }),
            Make("SNACKS REQUIRED", 25 + Runtime(minutes, 8, 18, 28) + Weigh(genres, ("Comedy", 12), ("Adventure", 10), ("Family", 16)) + variance,
                new[] { "Optional popcorn", "Light snack territory", "One bowl is enough" }, new[] { "Stock the couch", "Bring extra snacks", "Snack break advised" }, new[] { "Bring snacks", "Full pantry required", "Call for reinforcements" }),
            Make("PLOT TWISTS", 20 + Weigh(genres, ("Mystery", 28), ("Thriller", 22), ("Crime", 14), ("Science Fiction", 12), ("Sci-Fi", 12)) + (rating >= 8 ? 5 : 0) - variance,
                new[] { "Straightforward-ish", "No surprises yet", "Follow the thread" }, new[] { "Keep watching", "Stay alert", "Things may change" }, new[] { "Trust nobody", "Nothing is what it seems", "Plot armor required" }),
            Make("COUCH-LOCK", 25 + Runtime(minutes, 10, 24, 34) + Weigh(genres, ("Drama", 12), ("Comedy", 10), ("Family", 10)) + (item.ParentId != Guid.Empty ? 7 : 0) + variance,
                new[] { "Easy escape", "A comfy watch", "You can still get up" }, new[] { "Settle in", "Stay awhile", "Plans can wait" }, new[] { "Cancel plans", "Nobody move", "The couch wins" })
        };

        if (overrides is null || overrides.Count != scores.Length)
        {
            return scores;
        }

        return scores.Select((score, index) => WithValue(score, overrides[index])).ToArray();
    }

    private static VibeScore WithValue(VibeScore score, int value)
    {
        value = Math.Clamp(value, 0, 100);
        return score with { Value = value, Verdict = Pick(score.CaptionsFor(value)) };
    }

    private static VibeScore Make(string label, int raw, IReadOnlyList<string> low, IReadOnlyList<string> mid, IReadOnlyList<string> high)
    {
        var value = Math.Clamp(raw, 15, 95);
        var score = new VibeScore(label, value, string.Empty, low, mid, high);
        return score with { Verdict = Pick(score.CaptionsFor(value)) };
    }

    private static string Pick(IReadOnlyList<string> captions) => captions[Random.Shared.Next(captions.Count)];

    private static IReadOnlyList<string> CaptionsFor(this VibeScore score, int value) =>
        value >= 80 ? score.HighCaptions : value >= 50 ? score.MidCaptions : score.LowCaptions;

    private static int Weigh(HashSet<string> genres, params (string Genre, int Score)[] weights) => weights.Where(weight => genres.Contains(weight.Genre)).Sum(weight => weight.Score);
    private static int Runtime(double minutes, int shortScore, int mediumScore, int longScore) => minutes >= 150 ? longScore : minutes >= 120 ? mediumScore : minutes >= 90 ? shortScore : 0;
    private static int StableVariance(string id) => string.IsNullOrEmpty(id) ? 0 : (id.Aggregate(17, (hash, c) => unchecked((hash * 31) + c)) % 11) - 5;
}
