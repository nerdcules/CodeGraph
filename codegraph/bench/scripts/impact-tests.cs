#:project ../../src/CodeGraph.Client/CodeGraph.Client.csproj
#:property PublishAot=false
// What breaks if a member changes, and which tests cover it: counts by project and depth, then the tests.
// dotnet run impact-tests.cs -- <symbol> [depth]
using CodeGraph.Client;
using CodeGraph.Contracts;

using var graph = CodeGraphClient.Open();
var symbol = args.Length > 0 ? args[0] : throw new ArgumentException("usage: impact-tests.cs <symbol> [depth]");
var depth = args.Length > 1 ? int.Parse(args[1]) : 5;

var impact = await graph.ImpactAsync(symbol, depth, limit: int.MaxValue);
Console.WriteLine($"impact of {impact.Target.FullName} (depth {impact.Depth}): {impact.Dependents.Total} dependents, {impact.Tests.Total} tests");
foreach (var group in impact.Dependents.Items.GroupBy(h => h.Symbol.Project ?? "?").OrderByDescending(g => g.Count()).Take(10))
{
    Console.WriteLine($"  {group.Key}: {group.Count()} (nearest depth {group.Min(h => h.Depth)})");
}

Console.WriteLine("tests, nearest first:");
foreach (var test in impact.Tests.Items.Take(15))
{
    Console.WriteLine($"  d{test.Depth}  {test.Symbol.FullName}");
}

if (impact.Tests.Total > 15)
{
    Console.WriteLine($"  … {impact.Tests.Total - 15} more");
}
