namespace CodeGraph.Mcp;

/// <summary>
/// What the server tells a model about itself at initialize. Claude Code shows a server's instructions to the model
/// only up to 4,096 characters; a test keeps <see cref="Text"/> under that. Every sentence here answers a call the
/// 19 November 2026 sessions did not need: they called find_symbol 17 times in 12 sessions only to get ids,
/// index_status in 10 of 12 sessions, and tests_for or covered_by after an impact_of that had listed the tests.
/// </summary>
public static class ServerInstructions
{
    public const int Limit = 4096;

    public const string Text =
        """
        codegraph answers structural questions about the indexed codebase from a compiler-built graph: prefer it to grepping or opening files.
        Give impact_of, covered_by, callers_of, tests_for and snippet a name when you know one (Save, or Service.Save): the tool answers when exactly one symbol has that exact name, and lists the candidates otherwise. Use find_symbol for a partial name, and search_code when you only know what the code does.
        One impact_of answers "what breaks if I change X, and which tests cover it": it lists the dependents and the tests that can reach X (static reach, so a run may execute fewer). Call covered_by or tests_for after it only when you need more tests. Then snippet on the two or three declarations that matter.
        Rows end in an id in [brackets]: an 8-character handle while the graph is in memory, else a Roslyn doc-comment id (C#) or a ts:/scip: id. Pass it back verbatim, without the brackets. File paths are relative to the root named in the answer's first line; join the two before you Read a file.
        An answer that starts with "⚠ STALE INDEX" is from a graph older than the working tree, and rows ending in STALE come from edited files. index_status is for when an answer looks wrong, not for every session.
        When codegraph-index --watch is running (live_status), symbol_at, live_references, diagnostics and check_change answer from the live compiler: a file position to an id, every use of a symbol including framework and NuGet members, compiler errors, and what a proposed file breaks, before you write it.
        """;
}
