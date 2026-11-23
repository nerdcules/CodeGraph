using System.Text.Json.Nodes;
using CodeGraph.Contracts;
using CodeGraph.ModeBench;

namespace CodeGraph.Tests;

/// <summary>CODEGRAPH_MODE parses strictly, and a working copy switches between the three profiles cleanly.</summary>
public sealed class ModeTests
{
    [Theory]
    [InlineData(null, "direct")]
    [InlineData("", "direct")]
    [InlineData(" Lite ", "lite")]
    [InlineData("lite-cli", "lite-cli")]
    public void A_mode_parses(string? value, string mode) => Assert.Equal(mode, CodeGraphMode.Parse(value));

    [Fact]
    public void A_typo_is_not_a_mode() => Assert.Throws<ArgumentException>(() => CodeGraphMode.Parse("lte"));

    [Fact]
    public void A_working_copy_switches_between_modes_and_keeps_files_it_did_not_write()
    {
        var config = BenchConfig.Load();
        var copy = Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"));
        try
        {
            Directory.CreateDirectory(copy);
            File.WriteAllText(Path.Combine(copy, "mine.txt"), "kept");

            foreach (var mode in CodeGraphMode.All.Concat(CodeGraphMode.All.Reverse()))
            {
                var files = Profiles.Render(config, mode, copy);

                Assert.Equal(mode, Profiles.ModeOf(copy));
                Assert.Equal(mode == CodeGraphMode.Lite, File.Exists(Path.Combine(copy, "codegraph-lite.md")));
                Assert.Equal(mode != CodeGraphMode.Direct, File.Exists(Path.Combine(copy, "codegraph.client.json")));
                foreach (var file in files)
                {
                    Assert.DoesNotContain("{{", File.ReadAllText(Path.Combine(copy, file)));
                }

                var server = JsonNode.Parse(File.ReadAllText(Path.Combine(copy, ".mcp.json")))!["mcpServers"]!["codegraph"]!;
                Assert.Equal(mode, server["env"]![CodeGraphMode.Variable]!.GetValue<string>());
                Assert.Equal("off", server["env"]!["CODEGRAPH_TELEMETRY"]!.GetValue<string>());
                var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(copy, ".claude", "settings.json")))!;
                Assert.Equal("dontAsk", settings["permissions"]!["defaultMode"]!.GetValue<string>());
                Assert.False(settings["autoMemoryEnabled"]!.GetValue<bool>());
                Assert.InRange(File.ReadAllLines(Path.Combine(copy, "CLAUDE.md")).Length, 1, 15);
            }

            Assert.Equal("kept", File.ReadAllText(Path.Combine(copy, "mine.txt")));
            File.Delete(Path.Combine(copy, Profiles.Manifest));
            Assert.Throws<IOException>(() => Profiles.Render(config, CodeGraphMode.Lite, copy));
        }
        finally
        {
            TempDb.TryDelete(copy);
        }
    }

    [Fact]
    public void The_lite_doc_is_at_most_40_lines()
    {
        var config = BenchConfig.Load();
        Assert.InRange(File.ReadAllLines(config.LiteDoc).Length, 1, 40);
    }
}
