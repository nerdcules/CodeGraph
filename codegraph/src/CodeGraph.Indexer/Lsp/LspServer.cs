using System.Text.Json.Nodes;

namespace CodeGraph.Indexer.Lsp;

/// <summary>
/// Which language server answers the live tools for a language. TypeScript's ships with the extractor
/// (typescript-language-server on the bundled TypeScript); the others must be on PATH or in an installer's usual folder (<see cref="ToolLocator"/>). CODEGRAPH_LSP overrides
/// the command for any language ("jdtls -data /tmp/ws", say).
/// </summary>
internal sealed record LspServer(IReadOnlyList<string> Command, JsonObject? InitializationOptions, string Display)
{
    public static LspServer? For(string language, string root, Action<string> log)
    {
        if (Environment.GetEnvironmentVariable("CODEGRAPH_LSP") is { Length: > 0 } configured)
        {
            var parts = configured.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new LspServer(parts, null, parts[0]);
        }

        switch (language)
        {
            case Core.Languages.TypeScript or Core.Languages.JavaScript:
                var cli = TypeScriptExtractor.FindCli();
                var package = cli is null ? null : Path.GetDirectoryName(cli)!;
                var server = package is null ? null : Path.Combine(package, "node_modules", "typescript-language-server", "lib", "cli.mjs");
                if (server is null || !File.Exists(server))
                {
                    log("live: typescript-language-server is not installed beside the extractor (npm ci in extractors/typescript)");
                    return null;
                }

                return new LspServer(["node", server, "--stdio"], new JsonObject
                {
                    // The bundled TypeScript 6, as the extractor uses: a workspace on TypeScript 7 has no tsserver.js.
                    ["tsserver"] = new JsonObject { ["path"] = Path.Combine(package!, "node_modules", "typescript", "lib", "tsserver.js") },
                }, "typescript-language-server");
            case Core.Languages.Java or "kotlin" or "scala":
                return OnPath("jdtls", ["jdtls", "-data", Path.Combine(Path.GetTempPath(), "codegraph-jdtls", Hash(root))], log);
            case "python":
                return OnPath("pyright-langserver", ["pyright-langserver", "--stdio"], log);
            case "go":
                return OnPath("gopls", ["gopls"], log);
            case "rust":
                return OnPath("rust-analyzer", ["rust-analyzer"], log);
            default:
                return null;
        }
    }

    private static LspServer? OnPath(string tool, string[] command, Action<string> log)
    {
        if (ToolLocator.Find(tool) is not { } found)
        {
            log($"live: {tool} is not on PATH; live tools are off for this language (graph tools still work)");
            return null;
        }

        return new LspServer([found, .. command.Skip(1)], null, tool);
    }

    private static string Hash(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary>The LSP languageId of a file.</summary>
    public static string LanguageId(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".ts" or ".mts" or ".cts" => "typescript",
        ".tsx" => "typescriptreact",
        ".js" or ".mjs" or ".cjs" => "javascript",
        ".jsx" => "javascriptreact",
        ".java" => "java",
        ".kt" or ".kts" => "kotlin",
        ".scala" => "scala",
        ".py" => "python",
        ".go" => "go",
        ".rs" => "rust",
        ".html" => "html",
        _ => "plaintext",
    };
}
