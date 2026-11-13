using System.Text.Json;
using CodeGraph.Indexer;

namespace CodeGraph.Tests;

/// <summary>
/// The committed chart of tested language and SDK versions (docs/images/language-versions.svg) is drawn from the
/// code by tools/corpus/versions_chart.py. This keeps it honest: every version the build pins must be in it, so a
/// Roslyn, TypeScript or indexer bump without a redraw fails here.
/// </summary>
public sealed class LanguageVersionChartTests
{
    private static string? RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")) && Directory.Exists(Path.Combine(dir.FullName, "docs")))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    [Fact]
    public void The_chart_names_every_version_the_build_pins()
    {
        var root = RepoRoot();
        Assert.SkipUnless(root is not null, "not running from a source checkout");
        var chart = File.ReadAllText(Path.Combine(root!, "docs", "images", "language-versions.svg"));
        const string Redraw = "; redraw it: python3 tools/corpus/versions_chart.py --out docs/images/language-versions.svg";

        Assert.True(chart.Contains(RoslynIndexer.CompilerVersion, StringComparison.Ordinal), $"the chart does not name {RoslynIndexer.CompilerVersion}{Redraw}");
        Assert.True(chart.Contains($"up to {RoslynIndexer.NewestLanguageVersion}", StringComparison.Ordinal), $"the chart does not say up to {RoslynIndexer.NewestLanguageVersion}; set NEWEST_CSHARP in versions_chart.py{Redraw}");
        foreach (var indexer in ScipIndexers.All)
        {
            Assert.True(chart.Contains($"{indexer.Tool} {indexer.Tested}", StringComparison.Ordinal), $"the chart does not name {indexer.Tool} {indexer.Tested}{Redraw}");
        }

        using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "extractors", "typescript", "package.json")));
        var dependencies = package.RootElement.GetProperty("dependencies");
        Assert.True(chart.Contains($"typescript {dependencies.GetProperty("typescript").GetString()}", StringComparison.Ordinal), "the chart does not name the bundled TypeScript" + Redraw);
        Assert.True(chart.Contains($"@angular/compiler {dependencies.GetProperty("@angular/compiler").GetString()}", StringComparison.Ordinal), "the chart does not name the bundled Angular compiler" + Redraw);
    }
}
