using System.Text.Json;
using CodeGraph.Client;
using CodeGraph.Contracts;
using CodeGraph.Core;

namespace CodeGraph.Tests;

/// <summary>What the client adds to the store: pages, projects, relative paths, a guarded query, and how it finds a graph.</summary>
[Collection(ShopCollection.Name)]
public sealed class ClientTests(ShopFixture shop)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lists_default_to_25_items_and_say_how_many_there_are()
    {
        using var client = CodeGraphClient.Open(shop.DbPath);

        var all = await client.FindAsync(new SymbolQuery("") { Limit = int.MaxValue }, Cancel);
        var page = await client.FindAsync(new SymbolQuery(""), Cancel);
        var three = await client.FindAsync(new SymbolQuery("") { Limit = 3 }, Cancel);
        var two = await client.FindAsync(new SymbolQuery("") { Limit = 2, Offset = 1 }, Cancel);

        Assert.Equal(25, new SymbolQuery("").Limit);
        Assert.InRange(all.Total, 6, 1000);
        Assert.False(all.Truncated);
        Assert.Equal(Math.Min(25, all.Total), page.Items.Count);
        Assert.Equal(all.Total > 25, page.Truncated);
        Assert.Equal(all.Items.Take(3), three.Items);
        Assert.Equal(all.Total, three.Total);
        Assert.True(three.Truncated);
        Assert.Equal(all.Items.Skip(1).Take(2), two.Items);
        Assert.Equal(all.Total, two.Total);
        Assert.Equal(1 + Page.DefaultLimit, Page<int>.Of(Enumerable.Range(0, 26).ToArray(), Page.DefaultLimit).Total);
        Assert.True(Page<int>.Of(Enumerable.Range(0, 26).ToArray(), Page.DefaultLimit).Truncated);
    }

    [Fact]
    public async Task Symbols_name_their_project_and_query_cells_are_plain_values()
    {
        using var client = CodeGraphClient.Open(shop.DbPath);

        var save = (await client.FindAsync("SqlRepository.Save", match: "Exact", cancellationToken: Cancel)).Items.Single();
        Assert.Equal("Demo", save.Project);
        Assert.Equal("Method", save.Kind);
        Assert.Equal("Public", save.Accessibility);

        var result = await client.QueryAsync("SELECT kind, count(*) AS n, bool_or(is_test) AS t, NULL AS nothing FROM nodes GROUP BY kind ORDER BY kind", 2, cancellationToken: Cancel);
        Assert.Equal(["kind", "n", "t", "nothing"], result.Columns);
        Assert.Equal(2, result.Items.Count);
        Assert.True(result.Total > 2);
        Assert.True(result.Truncated);
        Assert.IsType<string>(result.Items[0][0]);
        Assert.IsType<long>(result.Items[0][1]);
        Assert.IsType<bool>(result.Items[0][2]);
        Assert.Null(result.Items[0][3]);
    }

    [Theory]
    [InlineData("DELETE FROM nodes")]
    [InlineData("INSERT INTO meta VALUES ('k', 'v')")]
    [InlineData("SELECT 1; DELETE FROM nodes")]
    [InlineData("COPY nodes TO 'nodes.csv'")]
    [InlineData("SELECT * FROM read_text('/etc/hostname')")]
    [InlineData("ATTACH 'other.db'")]
    [InlineData("SET enable_external_access = true")]
    [InlineData("INSTALL httpfs")]
    public async Task A_query_can_only_read_the_graph(string sql)
    {
        using var client = CodeGraphClient.Open(shop.DbPath);
        var before = await client.QueryAsync("SELECT count(*) FROM nodes", cancellationToken: Cancel);

        await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => client.QueryAsync(sql, cancellationToken: Cancel));

        Assert.Equal(before.Items[0][0], (await client.QueryAsync("SELECT count(*) FROM nodes", cancellationToken: Cancel)).Items[0][0]);
        Assert.False(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "nodes.csv")));
    }

    [Fact]
    public async Task A_query_that_runs_too_long_is_stopped()
    {
        using var client = CodeGraphClient.Open(shop.DbPath);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => client.QueryAsync(
            "SELECT count(*) FROM range(100000000) a, range(100000000) b", timeoutSeconds: 1, cancellationToken: Cancel));

        Assert.InRange(watch.Elapsed.TotalSeconds, 0.9, 10);
    }

    [Fact]
    public async Task Status_and_schema_describe_the_graph()
    {
        using var client = CodeGraphClient.Open(shop.DbPath);

        var status = await client.StatusAsync(Cancel);
        var schema = await client.SchemaAsync(Cancel);

        Assert.Equal(shop.DbPath, status.Db);
        Assert.True(status.Nodes > 0 && status.Edges > 0);
        Assert.Contains("csharp", status.Languages);
        Assert.Contains(schema.Tables, t => t.Name == "nodes" && t.Columns.Any(c => c.Name == "full_name"));
        Assert.Contains(schema.Tables, t => t.Name == "edges" && t.Columns.Select(c => c.Name).SequenceEqual(["from_id", "to_id", "kind"]));
        Assert.Contains("Calls", schema.EdgeKinds);
    }

    [Fact]
    public void File_projects_agree_between_the_snapshot_and_sql()
    {
        using var sql = GraphStore.OpenReader(shop.DbPath, null, TimeSpan.FromSeconds(5));
        using var memory = GraphStore.OpenReader(shop.DbPath, shop.Snapshot, TimeSpan.FromSeconds(5));

        var fromSql = sql.FileProjects();
        Assert.NotEmpty(fromSql);
        Assert.Equal(fromSql.OrderBy(p => p.Key), memory.FileProjects().OrderBy(p => p.Key));
    }

    [Fact]
    public void The_graph_is_found_from_the_argument_the_environment_a_config_file_or_the_worktree()
    {
        var temp = Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"));
        try
        {
            var repo = Path.Combine(temp, "repo");
            var deep = Path.Combine(repo, "src", "deep");
            Directory.CreateDirectory(deep);
            Directory.CreateDirectory(Path.Combine(repo, ".git"));

            Assert.Equal(Path.GetFullPath("x.db"), CodeGraphClient.Locate("x.db", "/env.db", null, deep));
            Assert.Equal("/env.db", CodeGraphClient.Locate(null, "/env.db", null, deep));
            Assert.Equal(Worktree.DefaultDbPath(deep), CodeGraphClient.Locate(null, null, null, deep));

            File.WriteAllText(Path.Combine(repo, CodeGraphClient.ConfigFileName),
                JsonSerializer.Serialize(new ClientConfig("graphs/g.db"), ContractsJson.Default.ClientConfig));
            Assert.Equal(Path.Combine(repo, "graphs", "g.db"), CodeGraphClient.Locate(null, null, null, deep));
            Assert.Equal("/env.db", CodeGraphClient.Locate(null, "/env.db", null, deep));
        }
        finally
        {
            TempDb.TryDelete(temp);
        }
    }

    [Fact]
    public void Contract_json_is_camel_case_and_leaves_out_nulls()
    {
        var page = new Page<Symbol>([new Symbol("M:A.B", "Method", "B", "A.B", null, "a.cs", 1, 2, false, null, "Public", false, false, false, false, false, "csharp", null)], 1, false);

        var json = JsonSerializer.Serialize(page, ContractsJson.Default.PageSymbol);

        Assert.StartsWith("{\"items\":[{\"id\":\"M:A.B\",\"kind\":\"Method\"", json);
        Assert.DoesNotContain("signature", json);
        Assert.EndsWith("\"total\":1,\"truncated\":false}", json);
    }
}
