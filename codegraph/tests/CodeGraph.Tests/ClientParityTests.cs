using CodeGraph.Client;
using CodeGraph.Contracts;
using CodeGraph.Core;

namespace CodeGraph.Tests;

[CollectionDefinition(Name)]
public sealed class ShopCollection : ICollectionFixture<ShopFixture>
{
    public const string Name = "shop";
}

/// <summary>
/// For each of the six tools the MCP server had before the client (find_symbol, outline, callers_of, impact_of,
/// tests_for, snippet), the tool's answer and the ICodeGraph method agree on the same input: the same rows in the
/// same order, the same counts, the same unresolved names. The tools answer from the snapshot (as the server
/// does) and from SQL; the client reads the file through its own native DuckDB connection.
/// </summary>
[Collection(ShopCollection.Name)]
public sealed class ClientParityTests(ShopFixture shop)
{
    public static TheoryData<bool> Paths => [true, false];

    /// <summary>One answer row as the tools print it: kind, full name, extra column, path:lines, [handle].</summary>
    private sealed record Row(string Kind, string FullName, string? Extra, string? Location, string Id);

    private static List<Row> Rows(string answer) =>
        answer.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.EndsWith(']') && line.Contains("  ["))
            .Select(line =>
            {
                var parts = line.Split("  ");
                var id = parts[^1].Trim('[', ']');
                var location = parts.Length >= 4 && parts[^2].Contains(':') ? parts[^2] : null;
                var extraEnd = location is null ? parts.Length - 1 : parts.Length - 2;
                var extra = extraEnd > 2 ? string.Join("  ", parts[2..extraEnd]) : null;
                return new Row(parts[0], parts[1], extra, location, id);
            })
            .ToList();

    private static string Handle(string id) => NodeHandles.Encode(NodeHandles.Hash(id));

    private Row Expected(Symbol symbol, string? extra) => new(
        symbol.Kind.ToLowerInvariant(),
        symbol.FullName,
        extra,
        symbol.File is null ? null : $"{new AnswerPaths(shop.Root).Relative(symbol.File)}:{symbol.StartLine}-{symbol.EndLine}",
        Handle(symbol.Id));

    private CodeGraphClient Client() => CodeGraphClient.Open(shop.DbPath);

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task Find_symbol(bool snapshot)
    {
        var (tools, store) = shop.Tools(snapshot);
        using var _ = store;
        using var client = Client();
        foreach (var (text, kind, match) in new[] { ("Save", (string?)null, "Contains"), ("Order", "Type", "Contains"), ("Save", null, "Exact"), ("Repo", null, "StartsWith") })
        {
            var page = await client.FindAsync(new SymbolQuery(text) { Kind = kind, Match = match, Limit = 20 }, TestContext.Current.CancellationToken);
            var answer = tools.FindSymbol(text, kind: kind, nameMatch: match);

            Assert.NotEmpty(page.Items);
            Assert.Equal(page.Items.Select(s => Expected(s, s.Signature)), Rows(answer));
        }

        Assert.Empty((await client.FindAsync("Nope", cancellationToken: TestContext.Current.CancellationToken)).Items);
        Assert.StartsWith("no symbols match 'Nope'", tools.FindSymbol("Nope"));
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task Outline(bool snapshot)
    {
        var (tools, store) = shop.Tools(snapshot);
        using var _ = store;
        using var client = Client();
        foreach (var id in new[] { "T:Shop.SqlRepository", "T:Shop.OrderService", "T:Shop.IRepository" })
        {
            var outline = await client.OutlineAsync(id, int.MaxValue, TestContext.Current.CancellationToken);
            var answer = tools.Outline(id);

            Assert.StartsWith($"{outline.Target.Kind} {outline.Target.FullName}\n", answer);
            Assert.NotEmpty(outline.Children.Items);
            Assert.Equal(outline.Children.Items.Select(s => Expected(s, s.Signature)), Rows(answer));
        }
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task Callers_of(bool snapshot)
    {
        var (tools, store) = shop.Tools(snapshot);
        using var _ = store;
        using var client = Client();
        foreach (var (symbol, depth) in new[] { ("IRepository.Save", 3), ("Validate", 1), ("Validate", 4), ("OrderService.Place", 2) })
        {
            var walk = await client.CallersAsync(symbol, depth, int.MaxValue, TestContext.Current.CancellationToken);
            var answer = tools.CallersOf(symbol, depth);

            Assert.NotEmpty(walk.Hits.Items);
            Assert.StartsWith($"callers of {walk.Target.FullName}:", answer);
            Assert.Equal(walk.Hits.Items.Select(h => Expected(h.Symbol, $"depth {h.Depth}")), Rows(answer));
        }

        Assert.StartsWith("Shop.Checkout.Run(): no callers", tools.CallersOf("Checkout.Run"));
        Assert.Empty((await client.CallersAsync("Checkout.Run", cancellationToken: TestContext.Current.CancellationToken)).Hits.Items);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task Impact_of(bool snapshot)
    {
        var (tools, store) = shop.Tools(snapshot);
        using var _ = store;
        using var client = Client();
        foreach (var (symbol, depth) in new[] { ("IRepository.Save", 5), ("Validate", 5), ("T:Shop.Order", 2), ("Checkout.Run", 5) })
        {
            var impact = await client.ImpactAsync(symbol, depth, int.MaxValue, TestContext.Current.CancellationToken);
            var answer = tools.ImpactOf(symbol, depth);

            Assert.StartsWith($"impact of {impact.Target.FullName}: {impact.Dependents.Total} dependents, {impact.Tests.Total} guarding tests", answer);
            var expected = impact.Dependents.Items.Concat(impact.Tests.Items).Select(h => Expected(h.Symbol, $"depth {h.Depth}"));
            Assert.Equal(expected, Rows(answer));
        }

        Assert.Equal(2, (await client.ImpactAsync("IRepository.Save", cancellationToken: TestContext.Current.CancellationToken)).Tests.Total);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task Tests_for(bool snapshot)
    {
        var (tools, store) = shop.Tools(snapshot);
        using var _ = store;
        using var client = Client();
        foreach (var symbol in new[] { "SqlRepository.Save", "OrderService.Place" })
        {
            var list = await client.TestsAsync(symbol, int.MaxValue, TestContext.Current.CancellationToken);
            var answer = tools.TestsFor(symbol);

            Assert.NotEmpty(list.Tests.Items);
            Assert.Equal(list.Tests.Items.Select(s => Expected(s, s.Signature)), Rows(answer));
        }

        // A test that calls an implementation does not test the interface member directly (impact_of has that reach).
        foreach (var symbol in new[] { "Validate", "IRepository.Save" })
        {
            Assert.Empty((await client.TestsAsync(symbol, cancellationToken: TestContext.Current.CancellationToken)).Tests.Items);
            Assert.StartsWith("no test calls this member directly", tools.TestsFor(symbol));
        }
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task Snippet(bool snapshot)
    {
        var (tools, store) = shop.Tools(snapshot);
        using var _ = store;
        using var client = Client();
        foreach (var (symbol, context) in new[] { ("Validate", 0), ("OrderService.Place", 2), ("T:Shop.Order", 0) })
        {
            var snippet = await client.SnippetAsync(symbol, context, TestContext.Current.CancellationToken);
            var answer = await tools.Snippet(symbol, context, TestContext.Current.CancellationToken);

            Assert.NotNull(snippet.Lines);
            var expected = new[] { $"// {new AnswerPaths(shop.Root).Relative(snippet.File)}:{snippet.StartLine}-{snippet.EndLine}" }
                .Concat(snippet.Lines.Select((line, i) => $"{snippet.StartLine + i}: {line}"));
            // The tool's text ends its lines with the platform's newline (\r\n on Windows).
            Assert.Equal(expected, answer.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n'));
        }
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task Names_that_resolve_to_nothing_or_to_several_agree(bool snapshot)
    {
        var (tools, store) = shop.Tools(snapshot);
        using var _ = store;
        using var client = Client();
        var cancel = TestContext.Current.CancellationToken;

        var ambiguous = await Assert.ThrowsAsync<AmbiguousSymbolException>(() => client.CallersAsync("Save", cancellationToken: cancel));
        Assert.Equal(2, ambiguous.Candidates.Count);
        foreach (var answer in new[] { tools.CallersOf("Save"), tools.ImpactOf("Save"), tools.TestsFor("Save"), await tools.Snippet("Save", cancellationToken: cancel) })
        {
            Assert.StartsWith("2 symbols are named Save; pass the id of one:", answer);
            Assert.Equal(ambiguous.Candidates.Select(s => Expected(s, s.Signature)), Rows(answer));
        }

        await Assert.ThrowsAsync<SymbolNotFoundException>(() => client.ImpactAsync("Nope", cancellationToken: cancel));
        foreach (var answer in new[] { tools.CallersOf("Nope"), tools.ImpactOf("Nope"), tools.TestsFor("Nope"), await tools.Snippet("Nope", cancellationToken: cancel) })
        {
            Assert.StartsWith("no node has the id 'Nope' and no symbol has that exact name.", answer);
        }

        // outline takes an id only, as it did before the client.
        Assert.StartsWith("no node with id 'SqlRepository'", tools.Outline("SqlRepository"));
    }
}
