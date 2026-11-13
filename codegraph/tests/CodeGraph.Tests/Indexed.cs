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
        EmbeddingTexts = indexer.EmbeddingTexts;
        Indexer = indexer;
    }

    /// <summary>The indexer itself, for its compiler report: language versions, parse errors, unmapped shapes.</summary>
    public RoslynIndexer Indexer { get; }

    /// <summary>Wraps an indexer another harness has already driven.</summary>
    public static Indexed From(RoslynIndexer indexer) => new(indexer);

    public IReadOnlyCollection<GraphNode> Nodes { get; }

    public IReadOnlyCollection<GraphEdge> Edges { get; }

    public IReadOnlyCollection<EdgeOrigin> Origins { get; }

    public IReadOnlyCollection<SearchDocument> SearchDocuments { get; }

    public IReadOnlyDictionary<string, string> EmbeddingTexts { get; }

    /// <summary>The path a source file named <paramref name="name"/> gets in the adhoc project.</summary>
    public static string PathOf(string name) => Path.Combine(Root, name);

    /// <summary>Indexes one source file, named <c>Test.cs</c>.</summary>
    public static Task<Indexed> OfAsync(string source) => OfAsync(("Test.cs", source));

    /// <summary>Indexes one source file as a project whose LangVersion is <paramref name="version"/>: how a repo on an older C# is read.</summary>
    public static Task<Indexed> OfAsync(LanguageVersion version, string source) => OfAsync(walk: null, version, ("Test.cs", source));

    public static Task<Indexed> OfAsync(params (string Name, string Source)[] files) => OfAsync(walk: null, files);

    /// <summary>Indexes the project but walks only the files named in <paramref name="walk"/> (all when null).</summary>
    public static Task<Indexed> OfAsync(IReadOnlyCollection<string>? walk, params (string Name, string Source)[] files) => OfAsync(walk, version: null, files);

    public static Task<Indexed> OfAsync(IReadOnlyCollection<string>? walk, LanguageVersion? version, params (string Name, string Source)[] files) => OfAsync(walk, version, withReferences: true, files);

    /// <summary>Indexes without framework references, which is what an unrestored project looks like to the indexer.</summary>
    public static Task<Indexed> Unrestored(string source) => OfAsync(walk: null, version: null, withReferences: false, ("Test.cs", source));

    private static async Task<Indexed> OfAsync(IReadOnlyCollection<string>? walk, LanguageVersion? version, bool withReferences, params (string Name, string Source)[] files)
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
            metadataReferences: withReferences ? FrameworkReferences : [],
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable),
            // Default is what a project without <LangVersion> gets from this Roslyn: its latest major.
            parseOptions: new CSharpParseOptions(version ?? LanguageVersion.Default));

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
