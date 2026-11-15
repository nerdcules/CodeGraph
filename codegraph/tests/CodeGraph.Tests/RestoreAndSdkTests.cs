using CodeGraph.Indexer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace CodeGraph.Tests;

/// <summary>
/// What the indexer says when it cannot see a project's whole compilation: packages never restored (a fresh clone),
/// or no SDK its runtime can load. Both used to pass quietly or crash; the Windows run of 2026-11-14 found them.
/// </summary>
public sealed class RestoreAndSdkTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"))).FullName;

    public void Dispose() => TempDb.TryDelete(_dir);

    private const string UsesAPackage = "using Some.Unrestored.Package;\nnamespace N; class C { Widget? W; void M() { } }";

    [Fact]
    public async Task A_project_whose_packages_were_never_restored_is_named_when_its_types_go_missing()
    {
        var indexer = await IndexAsync(UsesAPackage, restored: false);

        var note = Assert.Single(indexer.Notes);
        Assert.StartsWith("Demo: ", note);
        Assert.Contains("not restored", note);
        Assert.Contains("dotnet restore", note);
    }

    [Fact]
    public async Task A_restored_project_with_a_missing_type_gets_no_restore_note()
    {
        var indexer = await IndexAsync(UsesAPackage, restored: true);

        Assert.True(indexer.CompileErrors > 0);
        Assert.Empty(indexer.Notes);
    }

    [Fact]
    public async Task An_unrestored_project_that_compiles_gets_no_restore_note()
    {
        var indexer = await IndexAsync("namespace N; class C { void M() { } }", restored: false);

        Assert.Equal(0, indexer.CompileErrors);
        Assert.Empty(indexer.Notes);
    }

    [Fact]
    public void A_project_without_a_file_on_disk_is_not_called_unrestored()
    {
        Assert.False(RoslynIndexer.NotRestored(null));
        Assert.False(RoslynIndexer.NotRestored(Path.Combine(_dir, "Missing.csproj")));
    }

    [Fact]
    public void No_loadable_sdk_says_which_sdk_to_install_and_how_to_roll_forward()
    {
        var message = MsBuildSetup.Explain(10, ["11.0.100"], selfContained: false);

        Assert.Contains("runs on .NET 10", message);
        Assert.Contains("the SDKs installed are 11.0.100", message);
        Assert.Contains("https://dotnet.microsoft.com/download/dotnet/10.0", message);
        Assert.Contains("DOTNET_ROLL_FORWARD=LatestMajor", message);
    }

    [Fact]
    public void A_self_contained_indexer_is_not_told_to_roll_forward()
    {
        Assert.DoesNotContain("DOTNET_ROLL_FORWARD", MsBuildSetup.Explain(10, ["11.0.100"], selfContained: true));
    }

    [Fact]
    public void No_sdk_at_all_says_so()
    {
        var message = MsBuildSetup.Explain(10, [], selfContained: false);

        Assert.Contains("no .NET SDK was found", message);
        Assert.DoesNotContain("DOTNET_ROLL_FORWARD", message);
    }

    private async Task<RoslynIndexer> IndexAsync(string source, bool restored)
    {
        var projectFile = Path.Combine(_dir, "Demo.csproj");
        File.WriteAllText(projectFile, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        if (restored)
        {
            Directory.CreateDirectory(Path.Combine(_dir, "obj"));
            File.WriteAllText(Path.Combine(_dir, "obj", "project.assets.json"), "{}");
        }

        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var project = ProjectInfo.Create(
            projectId, VersionStamp.Default, "Demo", "Demo", LanguageNames.CSharp,
            filePath: projectFile,
            documents:
            [
                DocumentInfo.Create(DocumentId.CreateNewId(projectId), "Test.cs",
                    loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source), VersionStamp.Default)),
                    filePath: Path.Combine(_dir, "Test.cs")),
            ],
            metadataReferences: Indexed.FrameworkReferences,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var solution = workspace.AddProject(project).Solution;
        var indexer = new RoslynIndexer(_ => { });
        await indexer.IndexProjectAsync(solution.GetProject(projectId)!, solution, CancellationToken.None);
        return indexer;
    }
}
