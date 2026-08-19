using System.Diagnostics;
using System.Runtime.CompilerServices;
using CodeGraph.Core;
using CodeGraph.Indexer;
using Microsoft.Build.Locator;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.Error.WriteLine("usage: codegraph-index <solution.sln|.slnx|project.csproj> [--db <path>]");
    Console.Error.WriteLine("       default db: <solution dir>/.codegraph/graph.db");
    return 1;
}

var target = Path.GetFullPath(args[0]);
if (!File.Exists(target))
{
    Console.Error.WriteLine($"not found: {target}");
    return 1;
}

var dbIndex = Array.IndexOf(args, "--db");
var dbPath = dbIndex >= 0 && dbIndex + 1 < args.Length
    ? Path.GetFullPath(args[dbIndex + 1])
    : Path.Combine(Path.GetDirectoryName(target)!, ".codegraph", "graph.db");

// MSBuildLocator must run before the JIT touches any Microsoft.CodeAnalysis.MSBuild type,
// which is why the actual work lives in a separate, non-inlined method.
MSBuildLocator.RegisterDefaults();
return await RunAsync(target, dbPath);

[MethodImpl(MethodImplOptions.NoInlining)]
static async Task<int> RunAsync(string target, string dbPath)
{
    var stopwatch = Stopwatch.StartNew();
    var indexer = new RoslynIndexer(message => Console.Error.WriteLine(message));

    await indexer.IndexAsync(target, CancellationToken.None);

    using var store = new GraphStore(dbPath);
    store.Clear();
    store.Write(indexer.Nodes, indexer.Edges);

    var (nodes, edges) = store.Count();
    Console.Error.WriteLine($"indexed {nodes} nodes, {edges} edges in {stopwatch.Elapsed.TotalSeconds:F1}s → {dbPath}");
    return 0;
}
