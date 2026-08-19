using CodeGraph.Core;
using CodeGraph.Indexer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace CodeGraph.Tests;

/// <summary>
/// Runs the real indexer over C# source held in memory. Uses an <see cref="AdhocWorkspace"/> rather than
/// MSBuild so the tests need no restore and no SDK discovery, while still exercising the genuine
/// semantic model — calls resolve through symbols exactly as they do against a real solution.
/// </summary>
internal sealed class Indexed
{
    private static readonly IReadOnlyList<MetadataReference> FrameworkReferences =
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToArray();

    private Indexed(IReadOnlyCollection<GraphNode> nodes, IReadOnlyCollection<GraphEdge> edges)
    {
        Nodes = nodes;
        Edges = edges;
    }

    public IReadOnlyCollection<GraphNode> Nodes { get; }

    public IReadOnlyCollection<GraphEdge> Edges { get; }

    /// <summary>Indexes one source file, named <c>Test.cs</c>.</summary>
    public static Task<Indexed> OfAsync(string source) => OfAsync(("Test.cs", source));

    public static async Task<Indexed> OfAsync(params (string Name, string Source)[] files)
    {
        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var root = Path.Combine(Path.GetTempPath(), "codegraph-adhoc");

        var project = ProjectInfo.Create(
            projectId,
            VersionStamp.Default,
            name: "Demo",
            assemblyName: "Demo",
            language: LanguageNames.CSharp,
            filePath: Path.Combine(root, "Demo.csproj"),
            documents: files.Select(file => DocumentInfo.Create(
                DocumentId.CreateNewId(projectId),
                name: file.Name,
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(file.Source), VersionStamp.Default)),
                filePath: Path.Combine(root, file.Name))),
            metadataReferences: FrameworkReferences,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var solution = workspace.AddProject(project).Solution;
        var indexer = new RoslynIndexer(_ => { });
        await indexer.IndexProjectAsync(solution.GetProject(projectId)!, solution, CancellationToken.None);

        return new Indexed(indexer.Nodes, indexer.Edges);
    }

    public GraphNode Node(string id) =>
        Nodes.SingleOrDefault(n => n.Id == id)
        ?? throw new InvalidOperationException(
            $"no node '{id}'. Indexed ids:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", Nodes.Select(n => n.Id).Order())}");

    public bool Has(string id) => Nodes.Any(n => n.Id == id);

    public bool HasEdge(string fromId, string toId, EdgeKind kind) =>
        Edges.Contains(new GraphEdge(fromId, toId, kind));

    public IEnumerable<string> TargetsOf(string fromId, EdgeKind kind) =>
        Edges.Where(e => e.FromId == fromId && e.Kind == kind).Select(e => e.ToId);
}
