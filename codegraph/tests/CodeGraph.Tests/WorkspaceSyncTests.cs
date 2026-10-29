using CodeGraph.Indexer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace CodeGraph.Tests;

/// <summary>Watch mode keeps the loaded solution in step with the disk without an MSBuild reload.</summary>
public sealed class WorkspaceSyncTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "codegraph-sync");

    [Fact]
    public async Task Replaces_the_text_of_an_edited_document()
    {
        var solution = Solution(("A.cs", "class A { }"));

        var updated = WorkspaceSync.Apply(solution, [Path.Combine(Root, "A.cs")], _ => "class A { void M() { } }");

        var document = updated.GetDocumentIdsWithFilePath(Path.Combine(Root, "A.cs")).Select(updated.GetDocument).Single();
        var text = await document!.GetTextAsync(TestContext.Current.CancellationToken);
        Assert.Equal("class A { void M() { } }", text.ToString());
    }

    [Fact]
    public void Adds_a_new_file_to_the_project_whose_directory_holds_it()
    {
        var solution = Solution(("A.cs", "class A { }"));
        var path = Path.Combine(Root, "Sub", "B.cs");

        var updated = WorkspaceSync.Apply(solution, [path], _ => "class B { }");

        var document = Assert.Single(updated.GetDocumentIdsWithFilePath(path).Select(updated.GetDocument));
        Assert.Equal("Demo", document!.Project.Name);
    }

    [Fact]
    public void Ignores_a_new_file_outside_every_project()
    {
        var solution = Solution(("A.cs", "class A { }"));
        var path = Path.Combine(Path.GetTempPath(), "elsewhere", "B.cs");

        var updated = WorkspaceSync.Apply(solution, [path], _ => "class B { }");

        Assert.Empty(updated.GetDocumentIdsWithFilePath(path));
    }

    [Fact]
    public void Removes_a_deleted_document()
    {
        var solution = Solution(("A.cs", "class A { }"), ("B.cs", "class B { }"));
        var path = Path.Combine(Root, "B.cs");

        var updated = WorkspaceSync.Apply(solution, [path], _ => null);

        Assert.Empty(updated.GetDocumentIdsWithFilePath(path));
        Assert.Single(updated.Projects.Single().Documents);
    }

    [Theory]
    [InlineData("src/App/Program.cs", true)]
    [InlineData("src/App/obj/Debug/App.AssemblyInfo.cs", false)]
    [InlineData("src/App/bin/Debug/x.cs", false)]
    [InlineData("src/App/App.csproj", false)]
    [InlineData("README.md", false)]
    public void Recognises_source_files(string path, bool expected)
    {
        Assert.Equal(expected, WorkspaceSync.IsSourceFile(path.Replace('/', Path.DirectorySeparatorChar)));
    }

    [Theory]
    [InlineData("src/App/App.csproj", true)]
    [InlineData("Directory.Build.props", true)]
    [InlineData("App.slnx", true)]
    [InlineData("src/App/Program.cs", false)]
    public void Recognises_project_files(string path, bool expected)
    {
        Assert.Equal(expected, WorkspaceSync.IsProjectFile(path));
    }

    private static Solution Solution(params (string Name, string Source)[] files)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var project = ProjectInfo.Create(
            projectId,
            VersionStamp.Default,
            name: "Demo",
            assemblyName: "Demo",
            language: LanguageNames.CSharp,
            filePath: Path.Combine(Root, "Demo.csproj"),
            documents: files.Select(file => DocumentInfo.Create(
                DocumentId.CreateNewId(projectId),
                name: file.Name,
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(file.Source), VersionStamp.Default)),
                filePath: Path.Combine(Root, file.Name))));
        return workspace.AddProject(project).Solution;
    }
}
