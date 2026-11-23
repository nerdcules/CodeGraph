using System.Diagnostics;
using System.Text.Json.Nodes;
using CodeGraph.Contracts;

namespace CodeGraph.Tests;

/// <summary>
/// CODEGRAPH_MODE on the real server: direct (or unset) advertises its tools and instructions as before; lite and
/// lite-cli advertise no tools capability and no instructions; anything else stops the server.
/// </summary>
[Collection(ShopCollection.Name)]
public sealed class ServerModeTests(ShopFixture shop)
{
    private async Task<(JsonObject? Initialize, JsonObject? Tools, int? Exit, string Stderr)> Serve(string? mode)
    {
        var dotnet = Path.Combine(CodeGraph.Core.DotnetRuntime.Root()!, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var info = new ProcessStartInfo(dotnet, [Path.Combine(AppContext.BaseDirectory, "codegraph-mcp.dll"), shop.DbPath])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.Environment["CODEGRAPH_TELEMETRY"] = "off";
        if (mode is null)
        {
            info.Environment.Remove(CodeGraphMode.Variable);
        }
        else
        {
            info.Environment[CodeGraphMode.Variable] = mode;
        }

        using var process = Process.Start(info)!;
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        async Task<JsonObject?> Rpc(int id, string method, string parameters)
        {
            await process.StandardInput.WriteLineAsync($$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{parameters}}}""");
            await process.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
            while (await process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken) is { } line)
            {
                if (JsonNode.Parse(line) is JsonObject reply && reply["id"]?.GetValue<int>() == id)
                {
                    return reply;
                }
            }

            return null;
        }

        var initialize = await Rpc(1, "initialize", """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}""");
        if (initialize is null)
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            return (null, null, process.ExitCode, await stderr);
        }

        await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        var tools = await Rpc(2, "tools/list", "{}");
        process.StandardInput.Close();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (initialize, tools, null, await stderr);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("direct")]
    public async Task Direct_advertises_the_tools_and_instructions(string? mode)
    {
        var (initialize, tools, _, _) = await Serve(mode);

        Assert.Equal(CodeGraph.Mcp.ServerInstructions.Text, initialize!["result"]!["instructions"]!.GetValue<string>());
        Assert.NotNull(initialize["result"]!["capabilities"]!["tools"]);
        var names = tools!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("impact_of", names);
        Assert.Contains("find_symbol", names);
    }

    [Theory]
    [InlineData("lite")]
    [InlineData("lite-cli")]
    public async Task The_lite_modes_advertise_nothing(string mode)
    {
        var (initialize, tools, _, stderr) = await Serve(mode);

        Assert.Null(initialize!["result"]!["instructions"]);
        Assert.Null(initialize["result"]!["capabilities"]!["tools"]);
        Assert.NotNull(tools!["error"]);
        Assert.Contains($"CODEGRAPH_MODE={mode}", stderr);
    }

    [Fact]
    public async Task An_unknown_mode_stops_the_server()
    {
        var (initialize, _, exit, stderr) = await Serve("lte");

        Assert.Null(initialize);
        Assert.Equal(1, exit);
        Assert.Contains("CODEGRAPH_MODE='lte' is not a mode", stderr);
    }
}
