#:project ../../src/CodeGraph.Client/CodeGraph.Client.csproj
#:property PublishAot=false
// Copy to the scratch folder and run: dotnet run name.cs. In a copy outside this repo, #:project takes the
// absolute path of CodeGraph.Client.csproj. The graph: CODEGRAPH_DB, else codegraph.client.json, else the
// worktree's .codegraph/graph.db. Read-only; filter and count here; print at most 40 lines.
using CodeGraph.Client;
using CodeGraph.Contracts;

using var graph = CodeGraphClient.Open();

var callers = await graph.CallersAsync(args.Length > 0 ? args[0] : "Type.Member", depth: 1, limit: int.MaxValue);
Console.WriteLine($"{callers.Hits.Total} callers of {callers.Target.FullName}");
foreach (var hit in callers.Hits.Items.Take(10))
{
    Console.WriteLine($"  {hit.Symbol.FullName}  {hit.Symbol.File}:{hit.Symbol.StartLine}");
}
