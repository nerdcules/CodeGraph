# Analyze most called methods in CodeGraph
$dbPath = "C:\dev\GymX\.codegraph\graph.db"
$codeGraphPath = "C:\dev\CodeGraph\codegraph"

# Create a C# script to analyze the call graph
$csharp = @"
using System;
using System.Linq;
using System.Collections.Generic;
using CodeGraph.Core;

var dbPath = @"$dbPath";
var store = new GraphStore(dbPath);

// Get all methods
var allNodes = store.AllNodes();
var methods = allNodes.Where(n => n.Kind == NodeKind.Method).ToList();

Console.WriteLine("Total methods: {0}", methods.Count);
Console.WriteLine();

// Count callers for each method
var callStats = new List<(string Name, string FullName, int CallerCount, string Id, string Kind)>();

foreach (var method in methods)
{
    var callers = store.Callers(method.Id, 1); // Direct callers only
    callStats.Add((method.Name, method.FullName, callers.Count, method.Id, method.Kind.ToString()));
}

// Sort by caller count descending and take top 20
var topCalled = callStats
    .OrderByDescending(x => x.CallerCount)
    .Take(20)
    .ToList();

Console.WriteLine("Top 20 Most Called Methods:");
Console.WriteLine();
Console.WriteLine("{0,-60} {1,10}", "Method", "Callers");
Console.WriteLine(new string('-', 75));

foreach (var (name, fullName, count, id, kind) in topCalled)
{
    Console.WriteLine("{0,-60} {1,10}", fullName, count);
}
"@

# Write to a temp file and execute
$scriptFile = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "analyze-codegraph.csx")
$csharp | Set-Content $scriptFile

# Run using dotnet script or create a console app
cd $codeGraphPath
dotnet run --project src/CodeGraph.Indexer -- --analyze-calls 2>&1
