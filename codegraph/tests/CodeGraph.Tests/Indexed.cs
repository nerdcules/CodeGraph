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
    internal static readonly IReadOnlyList<MetadataReference> FrameworkReferences =
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToArray();

    private static readonly string Root = Path.Combine(Path.GetTempPath(), "codegraph-adhoc");

    private Indexed(RoslynIndexer indexer)
    {
        Nodes = indexer.Nodes;
        Edges = indexer.Edges;
        Origins = indexer.Origins;
        SearchDocuments = indexer.SearchDocuments;
    }

    public IReadOnlyCollection<GraphNode> Nodes { get; }

    public IReadOnlyCollection<GraphEdge> Edges { get; }

    public IReadOnlyCollection<EdgeOrigin> Origins { get; }

    public IReadOnlyCollection<SearchDocument> SearchDocuments { get; }

    /// <summary>The path a source file named <paramref name="name"/> gets in the adhoc project.</summary>
    public static string PathOf(string name) => Path.Combine(Root, name);

    /// <summary>Indexes one source file, named <c>Test.cs</c>.</summary>
    public static Task<Indexed> OfAsync(string source) => OfAsync(("Test.cs", source));

    public static Task<Indexed> OfAsync(params (string Name, string Source)[] files) => OfAsync(walk: null, files);

    /// <summary>Indexes the project but walks only the files named in <paramref name="walk"/> (all when null).</summary>
    public static async Task<Indexed> OfAsync(IReadOnlyCollection<string>? walk, params (string Name, string Source)[] files)
    {
        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var root = Root;

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
        var paths = walk?.Select(PathOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await indexer.IndexProjectAsync(solution.GetProject(projectId)!, solution, CancellationToken.None, paths);

        return new Indexed(indexer);
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
