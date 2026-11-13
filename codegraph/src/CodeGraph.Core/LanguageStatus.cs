namespace CodeGraph.Core;

/// <summary>
/// The per-language lines of <c>index_status</c>: which compiler each language was read with, the versions the
/// projects use, and the warnings that mean the graph is missing code. Read from the graph's meta rows, so any
/// route that records <see cref="LanguageMetaKeys"/> shows up the same way.
/// </summary>
public static class LanguageStatus
{
    /// <summary>
    /// One line per language in the graph: the compiler it was read with, the versions the projects use, and the
    /// warnings that mean the graph is missing code: compile errors, code newer than the compiler, unmapped shapes.
    /// </summary>
    public static IEnumerable<string> Lines(IReadOnlyDictionary<string, string> meta)
    {
        var languages = meta.Keys.Select(MetaKeys.LanguageOf).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (var language in languages)
        {
            string? Get(string key) => meta.GetValueOrDefault(MetaKeys.Language(language, key)) is { Length: > 0 } value ? value : null;

            var parts = new List<string>();
            if (Get(LanguageMetaKeys.Compiler) is { } compiler)
            {
                parts.Add(Get(LanguageMetaKeys.NewestLanguageVersion) is { } newest ? $"{compiler} (up to {newest})" : compiler);
            }
            else if (Get(LanguageMetaKeys.Tool) is { } tool)
            {
                parts.Add(tool);
            }

            if (Get(LanguageMetaKeys.LanguageVersions) is { } versions)
            {
                parts.Add($"code uses {versions}");
            }

            if (Get(LanguageMetaKeys.CompileErrors) is { } errors && errors != "0")
            {
                parts.Add($"{errors} compile error(s)");
            }

            yield return $"{language,-10}: {(parts.Count > 0 ? string.Join("; ", parts) : "(no compiler facts recorded; re-index to add them)")}";
            if (Get(LanguageMetaKeys.LanguageVersionErrors) is { } ahead && ahead != "0")
            {
                yield return $"  \u26a0 {ahead} error(s) say the code needs a newer language version than it was read with; declarations under them are missing. First: {Get(LanguageMetaKeys.FirstLanguageVersionError)}";
            }

            if (Get(LanguageMetaKeys.Unmapped) is { } unmapped)
            {
                yield return $"  \u26a0 shapes this indexer has no mapping for (a newer language feature): {unmapped}";
            }
        }
    }
}
