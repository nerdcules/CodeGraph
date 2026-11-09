using System.Text.Json.Nodes;
using CodeGraph.Indexer;

namespace CodeGraph.Tests;

/// <summary><c>codegraph-index init</c>: finding the solution, merging .mcp.json, and the .gitignore check.</summary>
public sealed class RepoSetupTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"))).FullName;

    public void Dispose() => TempDb.TryDelete(_root);

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void A_file_is_taken_as_is()
    {
        var project = Touch("App.csproj");
        Assert.Equal(project, RepoSetup.FindTarget(project, out _));
    }

    [Fact]
    public void The_one_solution_in_the_folder_wins_over_projects()
    {
        Touch("App.csproj");
        var solution = Touch("App.sln");
        Assert.Equal(solution, RepoSetup.FindTarget(_root, out _));
    }

    [Fact]
    public void Slnx_beside_sln_of_the_same_name_is_one_solution()
    {
        Touch("App.sln");
        var slnx = Touch("App.slnx");
        Assert.Equal(slnx, RepoSetup.FindTarget(_root, out var problem));
        Assert.Null(problem);
    }

    [Fact]
    public void A_solution_below_the_folder_is_found_but_build_output_and_hidden_folders_are_not()
    {
        var solution = Touch("src", "App.slnx");
        Touch("bin", "Copy.sln");
        Touch(".worktrees", "other", "App.slnx");
        Assert.Equal(solution, RepoSetup.FindTarget(_root, out _));
    }

    [Fact]
    public void From_a_project_folder_inside_a_repo_the_repo_solution_wins()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        var solution = Touch("App.slnx");
        Touch("src", "Lib", "Lib.csproj");
        Assert.Equal(solution, RepoSetup.FindTarget(Path.Combine(_root, "src", "Lib"), out _));
    }

    [Fact]
    public void A_project_folder_outside_a_repo_falls_back_to_its_project()
    {
        var project = Touch("src", "Lib", "Lib.csproj");
        Assert.Equal(project, RepoSetup.FindTarget(Path.Combine(_root, "src", "Lib"), out _));
    }

    [Fact]
    public void Two_solutions_are_an_error_listing_both()
    {
        Touch("A.sln");
        Touch("B.sln");
        Assert.Null(RepoSetup.FindTarget(_root, out var problem));
        Assert.Contains("A.sln", problem);
        Assert.Contains("B.sln", problem);
    }

    [Fact]
    public void An_empty_folder_is_an_error()
    {
        Assert.Null(RepoSetup.FindTarget(_root, out var problem));
        Assert.Contains("no .slnx", problem);
    }

    [Fact]
    public void A_new_config_gets_just_the_command()
    {
        var json = RepoSetup.MergeConfig(null, current => RepoSetup.Entry(current, "/opt/cg/codegraph-mcp", null, null, live: false))!;
        var server = JsonNode.Parse(json)!["mcpServers"]!["codegraph"]!.AsObject();
        Assert.Equal("/opt/cg/codegraph-mcp", server["command"]!.GetValue<string>());
        Assert.False(server.ContainsKey("env"));
        Assert.False(server.ContainsKey("args"));
    }

    [Fact]
    public void Other_servers_and_hand_added_env_survive_and_args_of_an_old_launcher_go()
    {
        const string existing = """
            {
              // comments and trailing commas are allowed in hand-edited files
              "mcpServers": {
                "other": { "command": "x" },
                "codegraph": {
                  "command": "dotnet",
                  "args": ["run", "--project", "/src/CodeGraph.Mcp"],
                  "env": { "CODEGRAPH_EMBEDDINGS_URL": "http://localhost:11434/v1", "DOTNET_ROOT": "/old" },
                },
              },
            }
            """;
        var json = RepoSetup.MergeConfig(existing, current => RepoSetup.Entry(current, "/opt/cg/codegraph-mcp", null, "/new", live: true))!;
        var servers = JsonNode.Parse(json)!["mcpServers"]!.AsObject();
        Assert.Equal("x", servers["other"]!["command"]!.GetValue<string>());
        var server = servers["codegraph"]!.AsObject();
        Assert.False(server.ContainsKey("args"));
        Assert.Equal("http://localhost:11434/v1", server["env"]!["CODEGRAPH_EMBEDDINGS_URL"]!.GetValue<string>());
        Assert.Equal("/new", server["env"]!["DOTNET_ROOT"]!.GetValue<string>());
        Assert.Equal("auto", server["env"]!["CODEGRAPH_DAEMON"]!.GetValue<string>());
    }

    [Fact]
    public void A_rerun_with_nothing_new_changes_nothing_and_DOTNET_ROOT_is_cleared_once_not_needed()
    {
        var first = RepoSetup.MergeConfig(null, current => RepoSetup.Entry(current, "/cg/codegraph-mcp", null, "/dotnet", live: false))!;
        Assert.Null(RepoSetup.MergeConfig(first, current => RepoSetup.Entry(current, "/cg/codegraph-mcp", null, "/dotnet", live: false)));

        var cleared = RepoSetup.MergeConfig(first, current => RepoSetup.Entry(current, "/cg/codegraph-mcp", null, null, live: false))!;
        Assert.False(JsonNode.Parse(cleared)!["mcpServers"]!["codegraph"]!.AsObject().ContainsKey("env"));
    }

    [Fact]
    public void An_explicit_db_becomes_the_server_argument()
    {
        var json = RepoSetup.MergeConfig(null, current => RepoSetup.Entry(current, "/cg/codegraph-mcp", "/data/app.db", null, live: false))!;
        Assert.Equal("/data/app.db", JsonNode.Parse(json)!["mcpServers"]!["codegraph"]!["args"]![0]!.GetValue<string>());
    }

    [Fact]
    public void A_config_that_is_not_an_object_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => RepoSetup.MergeConfig("[]", current => RepoSetup.Entry(current, "c", null, null, false)));
    }

    [Theory]
    [InlineData(".codegraph/", true)]
    [InlineData("bin/\r\n/.codegraph\r\n", true)]
    [InlineData("# .codegraph/\n", false)]
    [InlineData("codegraph/\n", false)]
    public void Gitignore_lines_that_cover_the_graph(string gitignore, bool ignored) =>
        Assert.Equal(ignored, RepoSetup.IgnoresGraph(gitignore));
}

/// <summary>The indexer executable on a repo it has never seen: no .codegraph/ folder, no previous graph.</summary>
public sealed class FirstIndexTests : IDisposable
{
    private readonly string _repo = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"))).FullName;

    public void Dispose() => TempDb.TryDelete(_repo);

    [Fact]
    public async Task The_first_index_of_a_repo_creates_the_graph_folder()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        var project = Path.Combine(_repo, "Lib.csproj");
        File.WriteAllText(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_repo, "Calc.cs"), "namespace Lib; public static class Calc { public static int Add(int a, int b) => a + b; }");

        var dotnet = Path.Combine(CodeGraph.Core.DotnetRuntime.Root()!, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var info = new System.Diagnostics.ProcessStartInfo(dotnet, [Path.Combine(AppContext.BaseDirectory, "codegraph-index.dll"), project])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.True(process.ExitCode == 0, await output + await error);
        Assert.True(File.Exists(Path.Combine(_repo, ".codegraph", "graph.db")));
    }
}
