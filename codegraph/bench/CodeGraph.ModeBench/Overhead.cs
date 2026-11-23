using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeGraph.Contracts;

namespace CodeGraph.ModeBench;

/// <summary>
/// Static overhead per mode, measured in tokens with the pinned model: the first API call's input of a one-word
/// prompt, with and without the part being measured. Text is measured as a prompt with the text appended minus the
/// same prompt without it, so the figure is the model's own token count (plus the two newlines that join them).
/// </summary>
internal static class Overhead
{
    private const string Prompt = "Reply with the single word OK.";

    public static async Task<long> TextTokensAsync(BenchConfig config, string text, CancellationToken cancellationToken)
    {
        var without = await FirstContextAsync(config, Prompt, cancellationToken);
        var with = await FirstContextAsync(config, Prompt + "\n\n" + text, cancellationToken);
        return with - without;
    }

    private static async Task<long> FirstContextAsync(BenchConfig config, string prompt, CancellationToken cancellationToken)
    {
        var bare = Path.Combine(config.WorkRoot, "tokens-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path.Combine(bare, ".claude"));
        await File.WriteAllTextAsync(Path.Combine(bare, ".claude", "settings.json"), """{"autoMemoryEnabled": false, "permissions": {"defaultMode": "dontAsk"}}""", cancellationToken);
        try
        {
            var result = await ClaudeRunner.RunAsync(config, new ClaudeLaunch(prompt, CodeGraphMode.Direct, bare, 1, TimeSpan.FromMinutes(3), LoadMcp: false, ToolsOverride: ""), null, cancellationToken);
            return result.FirstContextTokens ?? throw new InvalidOperationException($"no usage in the session's output: {result.Error}");
        }
        finally
        {
            Directory.Delete(bare, recursive: true);
        }
    }

    /// <summary>First context of a mode's profile, optionally without its CLAUDE.md, its MCP server, or with other tools.</summary>
    private static async Task<long> ProfileContextAsync(BenchConfig config, string mode, bool claudeMd, bool mcp, string? tools, CancellationToken cancellationToken)
    {
        var copy = Path.Combine(config.WorkRoot, $"overhead-{mode}-{Guid.NewGuid():n}");
        Profiles.Render(config, mode, copy);
        if (!claudeMd)
        {
            File.Delete(Path.Combine(copy, "CLAUDE.md"));
        }

        try
        {
            var result = await ClaudeRunner.RunAsync(config, new ClaudeLaunch(Prompt, mode, copy, 1, TimeSpan.FromMinutes(3), LoadMcp: mcp, ToolsOverride: tools), null, cancellationToken);
            return result.FirstContextTokens ?? throw new InvalidOperationException($"no usage in the session's output: {result.Error}");
        }
        finally
        {
            Directory.Delete(copy, recursive: true);
        }
    }

    public static async Task<int> MeasureAsync(BenchConfig config, string outDir, string? compileError, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outDir);
        var rows = new List<(string Mode, string Part, long Tokens, string How)>();
        void Add(string mode, string part, long tokens, string how)
        {
            rows.Add((mode, part, tokens, how));
            Console.WriteLine($"{mode,-9} {part,-44} {tokens,7}  {how}");
        }

        foreach (var mode in CodeGraphMode.All)
        {
            var full = await ProfileContextAsync(config, mode, claudeMd: true, mcp: true, tools: null, cancellationToken);
            var noMcp = await ProfileContextAsync(config, mode, claudeMd: true, mcp: false, tools: null, cancellationToken);
            var bare = await ProfileContextAsync(config, mode, claudeMd: false, mcp: false, tools: null, cancellationToken);
            Add(mode, "first context, whole profile", full, $"tools {ClaudeRunner.ToolsFor(mode)}");
            Add(mode, "MCP server (codegraph)", full - noMcp, "with minus without the server");
            Add(mode, "CLAUDE.md snippet", noMcp - bare, "with minus without CLAUDE.md");
        }

        // Claude Code defers MCP tools behind ToolSearch; without ToolSearch every schema loads at the start.
        var upfront = await ProfileContextAsync(config, CodeGraphMode.Direct, claudeMd: true, mcp: true, tools: "", cancellationToken);
        var upfrontBare = await ProfileContextAsync(config, CodeGraphMode.Direct, claudeMd: true, mcp: false, tools: "", cancellationToken);
        Add(CodeGraphMode.Direct, "MCP tool schemas loaded up front", upfront - upfrontBare, "no ToolSearch: with minus without the server");

        var doc = Profiles.Fill(await File.ReadAllTextAsync(config.LiteDoc, cancellationToken),
            Profiles.Values(config, CodeGraphMode.Lite, "/run", "/run/scratch"));
        Add(CodeGraphMode.Lite, "codegraph-lite.md (text)", await TextTokensAsync(config, doc, cancellationToken), "text in a prompt; a Read result adds line numbers");
        var help = await CliHelpAsync(config, cancellationToken);
        Add(CodeGraphMode.LiteCli, "codegraph --help (text)", await TextTokensAsync(config, help, cancellationToken), "text in a prompt");
        if (compileError is not null)
        {
            Add(CodeGraphMode.Lite, $"compile error output ({Path.GetFileName(compileError)})",
                await TextTokensAsync(config, await File.ReadAllTextAsync(compileError, cancellationToken), cancellationToken), "text in a prompt");
        }

        var json = JsonSerializer.Serialize(new
        {
            model = config.Model,
            measuredAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            rows = rows.Select(r => new { r.Mode, r.Part, r.Tokens, r.How }),
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(outDir, "overhead.json"), json, cancellationToken);
        var md = new StringBuilder($"| mode | part | tokens | how |\n|---|---|---:|---|\n");
        foreach (var r in rows)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"| {r.Mode} | {r.Part} | {r.Tokens:N0} | {r.How} |");
        }

        await File.WriteAllTextAsync(Path.Combine(outDir, "overhead.md"), md.ToString(), cancellationToken);
        return 0;
    }

    public static async Task<string> CliHelpAsync(BenchConfig config, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(config.Cli, "--help") { RedirectStandardOutput = true };
        using var process = Process.Start(info)!;
        var help = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return help;
    }
}
