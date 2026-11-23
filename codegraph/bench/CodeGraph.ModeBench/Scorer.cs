using System.Text.RegularExpressions;

namespace CodeGraph.ModeBench;

/// <summary>How an answer compares with the truth: precision and recall on the items it lists, and their F1.</summary>
internal sealed record Score(double Precision, double Recall, double F1, int AnswerItems, int TruthItems, int Matched, double? CountAccuracy, bool HasAnswerBlock);

/// <summary>
/// Reads the items after the ANSWER line and matches them with the truth. Names match when they are equal without
/// parameter lists and generic arguments, or when the answer is a qualified tail of the truth (Type.Method for
/// Ns.Type.Method). Files match on the path relative to the repo, ignoring a :line suffix. Signatures match with
/// whitespace collapsed. Projects match by name; for "Project: N" items the share of matched projects whose count
/// is right is reported apart from the score. An empty truth scores 1 when the answer lists nothing (or NONE).
/// </summary>
internal static partial class Scorer
{
    public static IReadOnlyList<string> AnswerItems(string? answer, out bool hasBlock)
    {
        hasBlock = false;
        if (string.IsNullOrWhiteSpace(answer))
        {
            return [];
        }

        var lines = answer.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        var start = Array.FindLastIndex(lines, l => Clean(l).Equals("ANSWER", StringComparison.OrdinalIgnoreCase)
                                                    || Clean(l).Equals("ANSWER:", StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            return [];
        }

        hasBlock = true;
        return lines.Skip(start + 1)
            .Select(Clean)
            .Where(l => l.Length > 0 && !l.StartsWith("```", StringComparison.Ordinal))
            .Where(l => !l.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static string Clean(string line)
    {
        var text = line.Trim();
        text = ListMarker().Replace(text, "");
        return text.Trim().Trim('`', '*', ',', ';').Trim();
    }

    public static Score Rate(string? answer, Truth truth, string match, string repoRoot)
    {
        var items = AnswerItems(answer, out var hasBlock);
        if (truth.Items.Count == 0)
        {
            var right = hasBlock && items.Count == 0;
            return new Score(right ? 1 : 0, right ? 1 : 0, right ? 1 : 0, items.Count, 0, 0, null, hasBlock);
        }

        var answerKeys = items.Select(i => Key(i, match, repoRoot)).Where(k => k.Length > 0).Distinct().ToList();
        var truthKeys = truth.Items.Select(i => Key(i, match, repoRoot)).Distinct().ToList();
        var unmatched = truthKeys.ToHashSet();
        var matchedPairs = new List<(string Answer, string Truth)>();
        foreach (var a in answerKeys)
        {
            var hit = unmatched.FirstOrDefault(t => Matches(a, t, match));
            if (hit is not null)
            {
                unmatched.Remove(hit);
                matchedPairs.Add((a, hit));
            }
        }

        var matched = matchedPairs.Count;
        var precision = answerKeys.Count == 0 ? 0 : (double)matched / answerKeys.Count;
        var recall = (double)matched / truthKeys.Count;
        var f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);

        double? countAccuracy = null;
        if (truth.Counts is not null && matched > 0)
        {
            var counts = items.Select(i => (Key: Key(i, match, repoRoot), Count: CountOf(i))).GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First().Count);
            var byKey = truth.Counts.ToDictionary(kv => Key(kv.Key, match, repoRoot), kv => kv.Value);
            countAccuracy = (double)matchedPairs.Count(p => counts.GetValueOrDefault(p.Answer) == byKey.GetValueOrDefault(p.Truth)) / matched;
        }

        return new Score(precision, recall, f1, answerKeys.Count, truthKeys.Count, matched, countAccuracy, hasBlock);
    }

    internal static string Key(string item, string match, string repoRoot) => match switch
    {
        "file" => FileKey(item, repoRoot),
        "signature" => Whitespace().Replace(item.Trim().TrimEnd(';', '{').Trim(), " ").ToLowerInvariant(),
        "project" => item.Split(':')[0].Trim().ToLowerInvariant(),
        _ => NameKey(item),
    };

    private static string NameKey(string item)
    {
        var text = item.Split('(')[0];
        // Generic arguments and arity markers: List<T>, GetItemById<T>, GetItemById``1.
        while (Generic().IsMatch(text))
        {
            text = Generic().Replace(text, "");
        }

        text = text.Replace("``1", "", StringComparison.Ordinal).Replace("`1", "", StringComparison.Ordinal);
        // An answer may name a member "M:Ns.Type.Method" or append " (Project)" or " — file:line".
        if (text.Length > 2 && text[1] == ':' && char.IsUpper(text[0]))
        {
            text = text[2..];
        }

        return text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "";
    }

    private static string FileKey(string item, string repoRoot)
    {
        var text = item.Replace('\\', '/').Trim();
        var root = repoRoot.Replace('\\', '/').TrimEnd('/') + "/";
        if (text.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            text = text[root.Length..];
        }

        text = LineSuffix().Replace(text, "");
        return text.TrimStart('.', '/').ToLowerInvariant();
    }

    private static bool Matches(string answer, string truth, string match) => match switch
    {
        "name" => answer == truth || (answer.Contains('.') && truth.EndsWith("." + answer, StringComparison.Ordinal)),
        "file" => answer == truth || truth.EndsWith("/" + answer, StringComparison.Ordinal) || answer.EndsWith("/" + truth, StringComparison.Ordinal),
        _ => answer == truth,
    };

    private static int? CountOf(string item) =>
        item.Split(':') is [_, var count, ..] && int.TryParse(Digits().Match(count).Value, out var n) ? n : null;

    [GeneratedRegex(@"^(\d+[.)]\s+|[-*•]\s+)")]
    private static partial Regex ListMarker();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"<[^<>]*>")]
    private static partial Regex Generic();

    [GeneratedRegex(@"(:\d+(-\d+)?)+$")]
    private static partial Regex LineSuffix();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}
