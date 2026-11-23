#:project ../../src/CodeGraph.Client/CodeGraph.Client.csproj
#:property PublishAot=false
// Transitive callers of a member, grouped by the project that declares them.
// dotnet run callers-by-project.cs -- <symbol> [depth]
using CodeGraph.Client;
using CodeGraph.Contracts;

using var graph = CodeGraphClient.Open();
var symbol = args.Length > 0 ? args[0] : throw new ArgumentException("usage: callers-by-project.cs <symbol> [depth]");
var depth = args.Length > 1 ? int.Parse(args[1]) : 3;

var callers = await graph.CallersAsync(symbol, depth, limit: int.MaxValue);
Console.WriteLine($"{callers.Hits.Total} callers of {callers.Target.FullName} within {callers.Depth} hops");
foreach (var project in callers.Hits.Items.GroupBy(h => h.Symbol.Project ?? "?").OrderByDescending(g => g.Count()))
{
    var direct = project.Count(h => h.Depth == 1);
    Console.WriteLine($"  {project.Key}: {project.Count()} ({direct} direct, {project.Count(h => h.Symbol.IsTest)} tests)");
}
