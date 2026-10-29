using System.Text.RegularExpressions;

namespace CodeGraph.Core;

/// <summary>
/// One node's contribution to full-text search: its term frequencies and total term count. Built by the indexer
/// from the names, signature, doc comment and body identifiers of a declaration, queried by <see cref="GraphStore.Search"/>.
/// </summary>
public sealed record SearchDocument(string NodeId, IReadOnlyDictionary<string, int> Terms, int Length);

/// <summary>
/// The tokenizer shared by indexing and querying, so "where's the code that saves a workout" meets
/// <c>SaveWorkoutAsync</c> on equal terms. Identifiers split on case and digit boundaries, everything lower-cases,
/// common English and C# noise words drop out, and a light suffix stemmer folds plurals and tenses.
/// </summary>
public static partial class SearchText
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "of", "to", "in", "on", "at", "by", "for", "and", "or", "not", "is", "are", "was", "be",
        "it", "its", "this", "that", "these", "those", "with", "from", "as", "into", "than", "then", "if", "else",
        "do", "does", "where", "what", "which", "who", "how", "when", "code", "method", "function", "class",
        "var", "new", "return", "null", "true", "false", "void", "int", "string", "bool", "object", "public",
        "private", "protected", "internal", "static", "async", "await", "using", "namespace", "get", "set",
        "value", "self", "base", "out", "ref", "param", "returns", "summary", "see", "cref", "remarks",
    };

    /// <summary>Terms of <paramref name="text"/> after splitting, stop-word removal and stemming. Repeats are kept.</summary>
    public static IEnumerable<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (Match word in WordPattern().Matches(text))
        {
            var pieces = PiecePattern().Matches(word.Value);
            foreach (Match piece in pieces)
            {
                if (Normalize(piece.Value) is { } term)
                {
                    yield return term;
                }
            }

            // A compound identifier also counts as one term, so the query "SaveWorkout" hits it exactly.
            if (pieces.Count > 1 && Normalize(word.Value) is { } whole)
            {
                yield return whole;
            }
        }
    }

    /// <summary>Distinct query terms, in the order they first appear.</summary>
    public static IReadOnlyList<string> QueryTerms(string query) => Tokenize(query).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Builds a document from weighted parts: a name repeated three times outranks the same word buried in a body.
    /// Null when nothing survives tokenizing.
    /// </summary>
    public static SearchDocument? Document(string nodeId, IEnumerable<(string? Text, int Weight)> parts)
    {
        var terms = new Dictionary<string, int>(StringComparer.Ordinal);
        var length = 0;
        foreach (var (text, weight) in parts)
        {
            foreach (var term in Tokenize(text))
            {
                terms[term] = terms.GetValueOrDefault(term) + weight;
                length += weight;
            }
        }

        return length == 0 ? null : new SearchDocument(nodeId, terms, length);
    }

    private static string? Normalize(string piece)
    {
        var word = piece.ToLowerInvariant();
        if (word.Length < 2 || StopWords.Contains(word))
        {
            return null;
        }

        var term = Stem(word);
        return term.Length < 2 || StopWords.Contains(term) ? null : term;
    }

    // Just enough stemming for identifiers and prose to meet: plurals, -ing, -ed, and the silent e that
    // "compare"/"compared" and "save"/"saved" differ by. Applied identically on both sides, so consistency
    // matters more than linguistic accuracy.
    private static string Stem(string word)
    {
        if (word.Length > 4 && word.EndsWith("ies", StringComparison.Ordinal))
        {
            word = word[..^3] + "y";
        }
        else if (word.Length > 5 && word.EndsWith("ing", StringComparison.Ordinal))
        {
            word = word[..^3];
        }
        else if (word.Length > 4 && word.EndsWith("ed", StringComparison.Ordinal) && word[^3] != 'e')
        {
            word = word[..^2];
        }
        else if (word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal))
        {
            word = word[..^1];
        }

        return word.Length > 3 && word.EndsWith('e') ? word[..^1] : word;
    }

    [GeneratedRegex("[A-Za-z0-9_]+")]
    private static partial Regex WordPattern();

    // HTTPClient → HTTP, Client; parseJSON2 → parse, JSON, 2; save_workout → save, workout.
    [GeneratedRegex("[A-Z]+(?![a-z])|[A-Z][a-z]+|[a-z]+|[0-9]+")]
    private static partial Regex PiecePattern();
}
