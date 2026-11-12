using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeGraph.Core;
using CodeGraph.Core.Live;

namespace CodeGraph.Indexer.Lsp;

/// <summary>
/// The live tools for a language without Roslyn, answered by a language server: positions and ids are joined
/// through the graph (a node's file and line give the server a position; a location the server returns maps
/// back to the innermost node holding it). Same pipe requests as <see cref="LiveService"/>; data_flow stays C#-only.
/// </summary>
internal sealed partial class LspLiveService(string target, string dbPath, string language, string root, LspServer? server, ReindexWatch watch, Action<string> log) : IAsyncDisposable
{
    private readonly TaskCompletionSource<LspClient?> _client = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _edits = new(1, 1);

    /// <summary>Starts the language server; live requests wait for it (graph-only when there is none).</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (server is null)
        {
            _client.TrySetResult(null);
            return;
        }

        try
        {
            var clock = Stopwatch.StartNew();
            var client = await LspClient.StartAsync(server.Command, server.Display, root, server.InitializationOptions, log, cancellationToken);
            // A server answers references for a project only once it has loaded it, and it loads a project when a file
            // of it opens: open one indexed source file and wait for its first diagnostics, the sign the project is in.
            await WarmUpAsync(client, cancellationToken);
            log($"live: {client.Name} ready in {clock.Elapsed.TotalSeconds:F1}s");
            _client.TrySetResult(client);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log($"live: {server.Display} did not start ({e.Message}); live tools are off, graph tools still work");
            _client.TrySetResult(null);
        }
    }

    private async Task WarmUpAsync(LspClient client, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 120 && watch.LastIndexedAt is null; attempt++)
        {
            await Task.Delay(500, cancellationToken); // the first index tells us which files the workspace has
        }

        string? first;
        try
        {
            using var store = GraphStore.Open(dbPath, readOnly: true, TimeSpan.FromSeconds(10));
            first = store.IndexedFiles(language).Keys.Where(f => LspServer.LanguageId(f) is not ("plaintext" or "html") && File.Exists(f)).Order(StringComparer.Ordinal).FirstOrDefault();
        }
        catch (Exception e) when (e is FileNotFoundException or IOException or DuckDB.NET.Data.DuckDBException)
        {
            return;
        }

        if (first is not null)
        {
            await client.EnsureOpenAsync(first, LspServer.LanguageId(first), cancellationToken);
            await client.WaitForDiagnosticsAsync(LspClient.UriOf(first), null, TimeSpan.FromSeconds(60), cancellationToken);
        }
    }

    /// <summary>After a re-index: the server re-reads the saved files it has open, so its answers match the graph.</summary>
    public async Task RefreshAsync(IReadOnlyList<string> saved, CancellationToken cancellationToken)
    {
        if (!_client.Task.IsCompleted || await _client.Task is not { } client)
        {
            return;
        }

        foreach (var path in saved.Where(File.Exists))
        {
            await client.RefreshAsync(path, LspServer.LanguageId(path), cancellationToken);
            await client.NotifyAsync("textDocument/didSave", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = LspClient.UriOf(path) } }, cancellationToken);
        }
    }

    public async Task<LiveResponse> HandleAsync(LiveRequest request, CancellationToken cancellationToken)
    {
        switch (request.Method)
        {
            case "ping":
                return LiveResponse.Ok(Status());
            case "flush":
                await watch.FlushAsync(cancellationToken);
                return LiveResponse.Ok(Status());
            case "data_flow":
                throw new LiveException($"data_flow needs Roslyn and answers for C# only; {language} has callers_of, callees_of and live_references");
        }

        var client = await _client.Task.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken)
            ?? throw new LiveException(server is null
                ? $"no language server for {language}; live tools are off (graph tools still work)"
                : $"{server.Display} is not running; see the daemon log");
        return request.Method switch
        {
            "symbol_at" => LiveResponse.Ok(await SymbolAtAsync(client, Params<LivePosition>(request), cancellationToken)),
            "references" => LiveResponse.Ok(await ReferencesAsync(client, Params<LiveService.ReferencesParams>(request), cancellationToken)),
            "diagnostics" => LiveResponse.Ok(await DiagnosticsAsync(client, Params<LiveService.DiagnosticsParams>(request), cancellationToken)),
            "check_change" => LiveResponse.Ok(await CheckChangeAsync(client, Params<LiveService.CheckChangeParams>(request), cancellationToken)),
            "rename_preview" => LiveResponse.Ok(await RenameAsync(client, Params<LiveService.RenameParams>(request), cancellationToken)),
            _ => throw new LiveException($"unknown method '{request.Method}'"),
        };
    }

    private LiveStatus Status()
    {
        int documents;
        try
        {
            using var store = GraphStore.Open(dbPath, readOnly: true, TimeSpan.FromSeconds(5));
            documents = store.IndexedFiles(language).Count;
        }
        catch (Exception e) when (e is FileNotFoundException or IOException or DuckDB.NET.Data.DuckDBException)
        {
            documents = 0;
        }

        using var self = Process.GetCurrentProcess();
        return new LiveStatus(Environment.ProcessId, target, dbPath, 1, documents, watch.StartedAt, watch.LastIndexedAt, watch.LastIndexSeconds,
            watch.Pending, watch.Indexing, Loading: watch.LastIndexedAt is null, WorkingSetBytes: self.WorkingSet64, ManagedBytes: GC.GetTotalMemory(false),
            IdleExitMinutes: watch.IdleExit?.TotalMinutes);
    }

    private async Task<LiveSymbol> SymbolAtAsync(LspClient client, LivePosition at, CancellationToken cancellationToken)
    {
        var file = Path.GetFullPath(at.File);
        await client.EnsureOpenAsync(file, LspServer.LanguageId(file), cancellationToken);
        var hover = await client.RequestAsync("textDocument/hover", Position(file, at.Line - 1, at.Column - 1), cancellationToken);
        var definition = await client.RequestAsync("textDocument/definition", Position(file, at.Line - 1, at.Column - 1), cancellationToken);
        var (definitionFile, definitionLine) = FirstLocation(definition);
        using var store = GraphStore.Open(dbPath, readOnly: true, TimeSpan.FromSeconds(10));
        var declared = definitionFile is null ? null : store.NodeAt(definitionFile, definitionLine);
        var enclosing = store.NodeAt(file, at.Line);
        var display = HoverText(hover) ?? declared?.FullName ?? "(nothing here)";
        return new LiveSymbol(declared?.Id, declared?.Kind.ToString().ToLowerInvariant() ?? "symbol", display,
            InSource: declared is not null, definitionFile, definitionFile is null ? null : definitionLine, enclosing?.Id, ExpressionType: null);
    }

    private async Task<LiveReferences> ReferencesAsync(LspClient client, LiveService.ReferencesParams request, CancellationToken cancellationToken)
    {
        using var store = GraphStore.Open(dbPath, readOnly: true, TimeSpan.FromSeconds(10));
        var node = Resolve(store, request.Symbol);
        var (file, line, column) = NamePosition(node);
        await client.EnsureOpenAsync(file, LspServer.LanguageId(file), cancellationToken);
        var parameters = Position(file, line, column);
        parameters["context"] = new JsonObject { ["includeDeclaration"] = false };
        var result = await client.RequestAsync("textDocument/references", parameters, cancellationToken, TimeSpan.FromSeconds(60)) as JsonArray ?? [];
        var references = new List<LiveReference>();
        var lines = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var location in result.OfType<JsonObject>())
        {
            var path = LspClient.PathOf(location["uri"]!.GetValue<string>());
            var at = location["range"]!["start"]!["line"]!.GetValue<int>();
            if (!lines.TryGetValue(path, out var text))
            {
                lines[path] = text = File.Exists(path) ? await File.ReadAllLinesAsync(path, cancellationToken) : [];
            }

            var enclosing = store.NodeAt(path, at + 1);
            references.Add(new LiveReference(path, at + 1, enclosing?.Id, enclosing?.FullName, at < text.Length ? text[at].Trim() : ""));
        }

        var ordered = references.OrderBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Line).ToList();
        return new LiveReferences(node.FullName, node.Id, InSource: true, ordered.Count, ordered.Take(Math.Clamp(request.Limit, 1, 500)).ToList());
    }

    private async Task<LiveDiagnostics> DiagnosticsAsync(LspClient client, LiveService.DiagnosticsParams request, CancellationToken cancellationToken)
    {
        var items = new List<LiveDiagnostic>();
        IEnumerable<string> files;
        if (request.File is { } one)
        {
            files = [Path.GetFullPath(one)];
        }
        else
        {
            // The whole workspace: servers report on open documents, so every indexed file is opened (up to a limit).
            using var store = GraphStore.Open(dbPath, readOnly: true, TimeSpan.FromSeconds(10));
            files = store.IndexedFiles(language).Keys.Where(f => LspServer.LanguageId(f) is not ("plaintext" or "html")).Order(StringComparer.Ordinal).Take(400).ToList();
        }

        foreach (var file in files)
        {
            await client.EnsureOpenAsync(file, LspServer.LanguageId(file), cancellationToken);
        }

        foreach (var file in files)
        {
            var published = await client.WaitForDiagnosticsAsync(LspClient.UriOf(file), version: null, TimeSpan.FromSeconds(request.File is null ? 2 : 10), cancellationToken);
            items.AddRange(Diagnostics(file, published));
        }

        var shown = items.Where(d => request.IncludeWarnings || d.Severity == "error").ToList();
        return new LiveDiagnostics(items.Count(d => d.Severity == "error"), items.Count(d => d.Severity == "warning"), shown.Take(Math.Clamp(request.Limit, 1, 500)).ToList());
    }

    private async Task<LiveCheck> CheckChangeAsync(LspClient client, LiveService.CheckChangeParams request, CancellationToken cancellationToken)
    {
        IReadOnlyList<LiveFileText> files = request.Files is { Count: > 0 } many ? many
            : request.File is not null && request.Text is not null ? [new LiveFileText(request.File, request.Text)]
            : throw new LiveException("check_change: pass file and text, or files");
        var clock = Stopwatch.StartNew();
        var edited = files.Select(f => Path.GetFullPath(f.File)).ToHashSet(StringComparer.Ordinal);
        // The files that depend on the edited ones, from the graph: callers, subclasses, importers. Their errors are
        // what a changed signature breaks, so they are watched too (opened, so the server reports on them).
        List<string> dependents;
        using (var store = GraphStore.Open(dbPath, readOnly: true, TimeSpan.FromSeconds(10)))
        {
            dependents = store.DependentFiles(edited).Where(f => !edited.Contains(f) && File.Exists(f) && LspServer.LanguageId(f) is not ("plaintext" or "html"))
                .Order(StringComparer.Ordinal).Take(60).ToList();
        }

        await _edits.WaitAsync(cancellationToken); // one what-if at a time: each edits the server's copy of the files
        try
        {
            var before = new List<LiveDiagnostic>();
            var after = new List<LiveDiagnostic>();
            foreach (var file in edited.Concat(dependents))
            {
                await client.EnsureOpenAsync(file, LspServer.LanguageId(file), cancellationToken);
            }

            foreach (var file in edited.Concat(dependents))
            {
                before.AddRange(Diagnostics(file, await client.WaitForDiagnosticsAsync(LspClient.UriOf(file), null, TimeSpan.FromSeconds(10), cancellationToken)));
            }

            var next = dependents.ToDictionary(f => f, f => client.NextDiagnosticsAsync(LspClient.UriOf(f), TimeSpan.FromSeconds(10), cancellationToken), StringComparer.Ordinal);
            foreach (var edit in files)
            {
                var file = Path.GetFullPath(edit.File);
                var version = await client.ChangeAsync(file, LspServer.LanguageId(file), edit.Text, cancellationToken);
                after.AddRange(Diagnostics(file, await client.WaitForDiagnosticsAsync(LspClient.UriOf(file), version, TimeSpan.FromSeconds(15), cancellationToken)));
            }

            foreach (var (file, published) in next)
            {
                after.AddRange(Diagnostics(file, await published));
            }

            // Put the server's copy back to what is on disk.
            var settled = dependents.Select(f => client.NextDiagnosticsAsync(LspClient.UriOf(f), TimeSpan.FromSeconds(5), cancellationToken)).ToList();
            foreach (var edit in files)
            {
                var file = Path.GetFullPath(edit.File);
                var version = await client.ChangeAsync(file, LspServer.LanguageId(file), File.Exists(file) ? await File.ReadAllTextAsync(file, cancellationToken) : "", cancellationToken);
                await client.WaitForDiagnosticsAsync(LspClient.UriOf(file), version, TimeSpan.FromSeconds(5), cancellationToken);
            }

            await Task.WhenAll(settled);

            static string Key(LiveDiagnostic d) => $"{d.File}|{d.Id}|{d.Message}";
            var errorsBefore = before.Where(d => d.Severity == "error").ToList();
            var errorsAfter = after.Where(d => d.Severity == "error").ToList();
            var beforeKeys = errorsBefore.Select(Key).ToHashSet(StringComparer.Ordinal);
            var afterKeys = errorsAfter.Select(Key).ToHashSet(StringComparer.Ordinal);
            return new LiveCheck(
                [$"{language}: {edited.Count} edited and {dependents.Count} dependent file(s), checked by {client.Name}"],
                errorsAfter.Where(d => !beforeKeys.Contains(Key(d))).ToList(),
                errorsBefore.Where(d => !afterKeys.Contains(Key(d))).ToList(),
                errorsBefore.Count, errorsAfter.Count, clock.Elapsed.TotalSeconds);
        }
        finally
        {
            _edits.Release();
        }
    }

    private async Task<LiveRename> RenameAsync(LspClient client, LiveService.RenameParams request, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        GraphNode node;
        using (var store = GraphStore.Open(dbPath, readOnly: true, TimeSpan.FromSeconds(10)))
        {
            node = Resolve(store, request.Symbol);
        }

        var (file, line, column) = NamePosition(node);
        await client.EnsureOpenAsync(file, LspServer.LanguageId(file), cancellationToken);
        var parameters = Position(file, line, column);
        parameters["newName"] = request.NewName;
        var edit = await client.RequestAsync("textDocument/rename", parameters, cancellationToken, TimeSpan.FromSeconds(60));
        var byFile = new Dictionary<string, List<(int StartLine, int StartCharacter, int EndLine, int EndCharacter, string Text)>>(StringComparer.Ordinal);
        void Add(string uri, JsonArray? edits)
        {
            var path = LspClient.PathOf(uri);
            var list = byFile.TryGetValue(path, out var existing) ? existing : byFile[path] = [];
            foreach (var e in edits?.OfType<JsonObject>() ?? [])
            {
                var range = e["range"]!;
                list.Add((range["start"]!["line"]!.GetValue<int>(), range["start"]!["character"]!.GetValue<int>(),
                    range["end"]!["line"]!.GetValue<int>(), range["end"]!["character"]!.GetValue<int>(), e["newText"]!.GetValue<string>()));
            }
        }

        if (edit?["documentChanges"] is JsonArray changes)
        {
            foreach (var change in changes.OfType<JsonObject>().Where(c => c["textDocument"] is not null))
            {
                Add(change["textDocument"]!["uri"]!.GetValue<string>(), change["edits"] as JsonArray);
            }
        }
        else if (edit?["changes"] is JsonObject perUri)
        {
            foreach (var (uri, edits) in perUri)
            {
                Add(uri, edits as JsonArray);
            }
        }

        var previews = new List<LiveFileEdit>();
        var total = 0;
        foreach (var (path, edits) in byFile.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var lines = File.Exists(path) ? await File.ReadAllLinesAsync(path, cancellationToken) : [];
            var changed = new List<LiveLineEdit>();
            foreach (var group in edits.Where(e => e.StartLine == e.EndLine && e.StartLine < lines.Length).GroupBy(e => e.StartLine).OrderBy(g => g.Key))
            {
                var text = lines[group.Key];
                // Apply right to left so earlier columns stay valid.
                foreach (var e in group.OrderByDescending(e => e.StartCharacter))
                {
                    text = text[..Math.Min(e.StartCharacter, text.Length)] + e.Text + text[Math.Min(e.EndCharacter, text.Length)..];
                }

                changed.Add(new LiveLineEdit(group.Key + 1, lines[group.Key].Trim(), text.Trim()));
            }

            total += edits.Count;
            previews.Add(new LiveFileEdit(path, changed));
        }

        return new LiveRename(node.FullName, node.Id, request.NewName, total, previews, [], clock.Elapsed.TotalSeconds);
    }

    /// <summary>A graph id, or failing that an exact name, resolved to a node declared in this workspace.</summary>
    private GraphNode Resolve(GraphStore store, string symbol)
    {
        var node = store.GetNode(symbol)
            ?? store.FindSymbol(symbol, nameMatch: "Exact", limit: 2, language: language) switch
            {
                [var only] => only,
                [] => null,
                _ => throw new LiveException($"'{symbol}' names more than one declaration; pass the id from find_symbol"),
            };
        if (node is null)
        {
            throw new LiveException($"no declaration '{symbol}' in the graph; pass an id from find_symbol");
        }

        if (node.Kind == NodeKind.External || node.FilePath is null)
        {
            throw new LiveException($"{node.FullName} is not declared in this workspace; the language server finds references from a declaration in source");
        }

        return node;
    }

    /// <summary>Where a node's name sits (zero-based), so the server resolves the declaration rather than a decorator or keyword.</summary>
    private static (string File, int Line, int Column) NamePosition(GraphNode node)
    {
        var lines = File.ReadAllLines(node.FilePath!);
        var name = node.MemberKind == MemberKind.Constructor ? "constructor" : node.Name.TrimStart('#');
        var pattern = new Regex($@"(?<![\w$#]){Regex.Escape(name)}(?![\w$])");
        var first = Math.Max(0, node.StartLine - 1);
        for (var i = first; i < lines.Length && i <= Math.Min(node.EndLine - 1, first + 15); i++)
        {
            if (pattern.Match(lines[i]) is { Success: true } match)
            {
                var column = match.Index > 0 && lines[i][match.Index - 1] == '#' ? match.Index - 1 : match.Index;
                return (node.FilePath!, i, column);
            }
        }

        return (node.FilePath!, first, 0);
    }

    private static JsonObject Position(string file, int line, int character) => new()
    {
        ["textDocument"] = new JsonObject { ["uri"] = LspClient.UriOf(file) },
        ["position"] = new JsonObject { ["line"] = Math.Max(0, line), ["character"] = Math.Max(0, character) },
    };

    private static (string? File, int Line) FirstLocation(JsonNode? result)
    {
        var location = result switch
        {
            JsonArray { Count: > 0 } array => array[0],
            JsonObject single => single,
            _ => null,
        };
        var uri = location?["uri"] ?? location?["targetUri"];
        var range = location?["range"] ?? location?["targetSelectionRange"] ?? location?["targetRange"];
        return uri is null || range is null ? (null, 0) : (LspClient.PathOf(uri.GetValue<string>()), range["start"]!["line"]!.GetValue<int>() + 1);
    }

    private static string? HoverText(JsonNode? hover)
    {
        var contents = hover?["contents"];
        var text = contents switch
        {
            JsonObject markup when markup["value"] is { } value => value.GetValue<string>(),
            JsonArray parts => string.Join("\n", parts.Select(p => p is JsonObject o ? o["value"]?.GetValue<string>() : p?.GetValue<string>())),
            JsonValue plain => plain.GetValue<string>(),
            _ => null,
        };
        return text is null ? null : CodeFence().Replace(text, "").Trim();
    }

    private static IEnumerable<LiveDiagnostic> Diagnostics(string file, JsonArray published) =>
        published.OfType<JsonObject>().Select(d => new LiveDiagnostic(
            (d["severity"]?.GetValue<int>() ?? 1) switch { 1 => "error", 2 => "warning", 3 => "info", _ => "hint" },
            d["code"] is { } code ? code.ToJsonString().Trim('"') : d["source"]?.GetValue<string>() ?? "lsp",
            d["message"]?.GetValue<string>() ?? "",
            file,
            d["range"]?["start"]?["line"]?.GetValue<int>() + 1));

    private static T Params<T>(LiveRequest request) =>
        request.Params is { } p
            ? p.Deserialize<T>(LivePipe.Json) ?? throw new LiveException($"{request.Method}: invalid parameters")
            : throw new LiveException($"{request.Method}: parameters are required");

    [GeneratedRegex(@"```\w*")]
    private static partial Regex CodeFence();

    public async ValueTask DisposeAsync()
    {
        if (_client.Task.IsCompleted && await _client.Task is { } client)
        {
            await client.DisposeAsync();
        }
    }
}
