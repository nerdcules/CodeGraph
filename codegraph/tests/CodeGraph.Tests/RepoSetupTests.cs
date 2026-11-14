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
        Assert.Contains("angular.json", problem);
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
    public void Clients_default_to_claude_and_accept_lists_aliases_and_all()
    {
        Assert.Equal([McpClient.Claude], McpClient.Parse([], out _)!);
        Assert.Equal([McpClient.Claude, McpClient.Cursor], McpClient.Parse(["copilot-cli,cursor", "claude"], out _)!);
        Assert.Equal([McpClient.Roo, McpClient.Kilo, McpClient.Continue], McpClient.Parse(["roo-code,kilocode", "continue"], out _)!);
        Assert.Equal(McpClient.All, McpClient.Parse(["vscode", "all"], out _)!.OrderBy(c => McpClient.All.ToList().IndexOf(c)));
        Assert.Null(McpClient.Parse(["windsurf"], out var problem));
        Assert.Contains("unknown client 'windsurf'", problem);
    }

    [Fact]
    public void Copilot_means_every_place_GitHub_Copilot_reads()
    {
        Assert.Equal([McpClient.Claude, McpClient.VsCode, McpClient.VisualStudio, McpClient.JetBrains], McpClient.Parse(["copilot"], out _)!);
        Assert.Equal(RootHint.Absolute, McpClient.JetBrains.Root);
        Assert.True(McpClient.VisualStudio.BesideSolution);
    }

    [Fact]
    public void Kilo_gets_one_command_array_and_environment_and_keep_their_own_keys()
    {
        const string existing = """
            { "$schema": "https://kilo.ai/config.json", "model": "x",
              "mcp": { "codegraph": { "type": "local", "command": ["/old/codegraph-mcp"], "enabled": true,
                                      "environment": { "CODEGRAPH_EMBEDDINGS_URL": "http://localhost:11434/v1" } } } }
            """;
        var json = RepoSetup.MergeConfig(existing, current => RepoSetup.Shaped(
            RepoSetup.Entry(RepoSetup.Plain(current, EntryShape.Local), "/cg/codegraph-mcp", null, "/dotnet", live: false), current, EntryShape.Local), "mcp")!;
        var root = JsonNode.Parse(json)!;
        Assert.Equal("x", root["model"]!.GetValue<string>());
        var server = root["mcp"]!["codegraph"]!.AsObject();
        Assert.Equal("local", server["type"]!.GetValue<string>());
        Assert.Equal(["/cg/codegraph-mcp"], server["command"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.True(server["enabled"]!.GetValue<bool>());
        Assert.Equal("http://localhost:11434/v1", server["environment"]!["CODEGRAPH_EMBEDDINGS_URL"]!.GetValue<string>());
        Assert.Equal("/dotnet", server["environment"]!["DOTNET_ROOT"]!.GetValue<string>());
        Assert.False(server.ContainsKey("env"));

        // Same command, same env: nothing to write.
        Assert.Null(RepoSetup.MergeConfig(json, current => RepoSetup.Shaped(
            RepoSetup.Entry(RepoSetup.Plain(current, EntryShape.Local), "/cg/codegraph-mcp", null, "/dotnet", live: false), current, EntryShape.Local), "mcp"));
    }

    [Fact]
    public void Init_writes_each_clients_file_with_the_root_it_needs()
    {
        File.WriteAllText(Path.Combine(_root, ".git"), "gitdir: elsewhere");
        var solution = Touch("src", "App.slnx");
        var output = new StringWriter();
        var clients = McpClient.Parse(["all"], out _)!.Where(c => c != McpClient.Codex).ToList();
        Assert.True(RepoSetup.WriteClients(solution, "/cg/codegraph-mcp", null, null, live: false, clients, output), output.ToString());

        JsonObject Server(string file, string key) => JsonNode.Parse(File.ReadAllText(Path.Combine(_root, file)))![key]!["codegraph"]!.AsObject();
        string[] Args(JsonObject server) => (server["args"] as JsonArray ?? []).Select(a => a!.GetValue<string>()).ToArray();
        var root = _root.Replace('\\', '/');

        Assert.Empty(Args(Server(".mcp.json", "mcpServers")));
        Assert.Equal(["--root", "${workspaceFolder}"], Args(Server(Path.Combine(".vscode", "mcp.json"), "servers")));
        Assert.Equal(["--root", "${workspaceFolder}"], Args(Server(Path.Combine(".cursor", "mcp.json"), "mcpServers")));
        Assert.Equal(["--root", root], Args(Server(Path.Combine("src", ".vs", "mcp.json"), "servers")));
        Assert.Equal(["--root", root], Args(Server(Path.Combine(".github", "mcp.json"), "servers")));
        Assert.Empty(Args(Server(Path.Combine(".roo", "mcp.json"), "mcpServers")));
        Assert.Empty(Args(Server(Path.Combine(".continue", "mcpServers", "codegraph.json"), "mcpServers")));
        Assert.Empty(Args(Server(Path.Combine(".gemini", "settings.json"), "mcpServers")));
        Assert.Single(Server(Path.Combine(".kilo", "kilo.json"), "mcp")["command"]!.AsArray());
    }

    [Fact]
    public void VS_Code_gets_servers_a_type_and_the_workspace_folder()
    {
        var json = RepoSetup.MergeConfig(null, current => RepoSetup.Entry(current, "/cg/codegraph-mcp", null, null, live: false,
            RepoSetup.WorkspaceRootArgs, typed: true), McpClient.VsCode.ServersKey)!;
        var server = JsonNode.Parse(json)!["servers"]!["codegraph"]!.AsObject();
        Assert.Equal("stdio", server["type"]!.GetValue<string>());
        Assert.Equal(["--root", "${workspaceFolder}"], server["args"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Null(RepoSetup.MergeConfig(json, current => RepoSetup.Entry(current, "/cg/codegraph-mcp", null, null, live: false,
            RepoSetup.WorkspaceRootArgs, typed: true), McpClient.VsCode.ServersKey));
    }

    [Fact]
    public void An_explicit_db_wins_over_the_workspace_folder()
    {
        var entry = RepoSetup.Entry(null, "/cg/codegraph-mcp", "/data/app.db", null, live: false, RepoSetup.WorkspaceRootArgs);
        Assert.Equal(["/data/app.db"], entry["args"]!.AsArray().Select(a => a!.GetValue<string>()));
    }

    [Fact]
    public void Gemini_settings_keep_their_other_settings()
    {
        const string existing = """{ "theme": "Dracula", "mcpServers": { "other": { "command": "x" } } }""";
        var json = RepoSetup.MergeConfig(existing, current => RepoSetup.Entry(current, "/cg/codegraph-mcp", null, null, live: false))!;
        var root = JsonNode.Parse(json)!;
        Assert.Equal("Dracula", root["theme"]!.GetValue<string>());
        Assert.Equal("x", root["mcpServers"]!["other"]!["command"]!.GetValue<string>());
        Assert.Equal("/cg/codegraph-mcp", root["mcpServers"]!["codegraph"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void A_Codex_entry_is_read_from_codex_mcp_get_and_written_as_toml()
    {
        const string get = """
            { "name": "codegraph", "enabled": true,
              "transport": { "type": "stdio", "command": "/cg/codegraph-mcp", "args": [],
                             "env": { "CODEGRAPH_EMBEDDINGS_URL": "http://localhost:11434/v1" }, "env_vars": [], "cwd": null } }
            """;
        var existing = RepoSetup.CodexEntry(get)!;
        Assert.False(existing.ContainsKey("args"));
        var entry = RepoSetup.Entry(existing, "/cg/codegraph-mcp", null, "/dotnet", live: true);
        Assert.Equal(
            [
                "[mcp_servers.codegraph]",
                "command = \"/cg/codegraph-mcp\"",
                "env = { CODEGRAPH_EMBEDDINGS_URL = \"http://localhost:11434/v1\", DOTNET_ROOT = \"/dotnet\", CODEGRAPH_DAEMON = \"auto\" }",
            ],
            RepoSetup.CodexToml(entry));
        Assert.Null(RepoSetup.CodexEntry("not json"));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_damaged_graph_is_rebuilt_rather_than_crashing_the_indexer(bool full)
    {
        var project = WriteLibrary();
        Assert.Equal(0, (await RunIndexer(project)).Code);
        var db = Path.Combine(_repo, ".codegraph", "graph.db");

        // Overwrite most of the file past its header: every block checksum after the first fails.
        var random = new Random(7);
        using (var file = File.Open(db, FileMode.Open, FileAccess.ReadWrite))
        {
            var junk = new byte[4096];
            for (var offset = 4096L; offset < file.Length; offset += 4096)
            {
                random.NextBytes(junk);
                file.Position = offset;
                file.Write(junk);
            }
        }

        var (code, log) = await RunIndexer(project, full ? ["--full"] : []);

        Assert.True(code == 0, log);
        Assert.Contains("damaged", log);
        using var store = CodeGraph.Core.GraphStore.Open(db, readOnly: true, TimeSpan.FromSeconds(5));
        Assert.NotNull(store.GetNode("M:Lib.Calc.Add(System.Int32,System.Int32)"));
    }

    [Fact]
    public async Task A_pass_that_walks_no_file_keeps_the_compile_errors_the_last_walk_found()
    {
        var project = WriteLibrary();
        File.WriteAllText(Path.Combine(_repo, "Broken.cs"), "namespace Lib; class Broken { Missing.Type? Field; }");
        var db = Path.Combine(_repo, ".codegraph", "graph.db");
        var errors = CodeGraph.Core.MetaKeys.Language(CodeGraph.Core.Languages.CSharp, CodeGraph.Core.LanguageMetaKeys.CompileErrors);
        var notes = CodeGraph.Core.MetaKeys.Language(CodeGraph.Core.Languages.CSharp, CodeGraph.Core.LanguageMetaKeys.Notes);
        IReadOnlyDictionary<string, string> Meta()
        {
            using var store = CodeGraph.Core.GraphStore.Open(db, readOnly: true, TimeSpan.FromSeconds(5));
            return store.ReadMeta();
        }

        var (code, log) = await RunIndexer(project, "--full");
        Assert.True(code == 0, log);
        var afterWalk = Meta();
        Assert.NotEqual("0", afterWalk[errors]);

        (code, log) = await RunIndexer(project);
        Assert.True(code == 0, log);
        Assert.Contains("0 file(s) walked", log);
        Assert.Equal(afterWalk[errors], Meta()[errors]);
        Assert.Equal(afterWalk[notes], Meta()[notes]);
    }

    private string WriteLibrary()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        var project = Path.Combine(_repo, "Lib.csproj");
        File.WriteAllText(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_repo, "Calc.cs"), "namespace Lib; public static class Calc { public static int Add(int a, int b) => a + b; }");
        return project;
    }

    private static async Task<(int Code, string Log)> RunIndexer(string project, params string[] extra)
    {
        var dotnet = Path.Combine(CodeGraph.Core.DotnetRuntime.Root()!, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var info = new System.Diagnostics.ProcessStartInfo(dotnet, [Path.Combine(AppContext.BaseDirectory, "codegraph-index.dll"), project, .. extra])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await output + await error);
    }
}
