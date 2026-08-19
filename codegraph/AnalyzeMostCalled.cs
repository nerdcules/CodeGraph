using CodeGraph.Core;

var dbPath = @"C:\dev\GymX\.codegraph\graph.db";
var store = new GraphStore(dbPath);

// Find the most called methods by counting incoming call edges
var allNodes = store.AllNodes();

var callStats = new List<(string Name, string FullName, int CallerCount, string Id)>();

foreach (var node in allNodes.Where(n => n.Kind == NodeKind.Method))
{
    var callers = store.Callers(node.Id, 1); // Direct callers only
    callStats.Add((node.Name, node.FullName, callers.Count, node.Id));
}

// Sort by caller count descending
var topCalled = callStats.OrderByDescending(x => x.CallerCount).Take(20).ToList();

Console.WriteLine("Top 20 Most Called Methods in GymX:\n");
Console.WriteLine("{0,-60} {1,15} {2}", "Full Name", "Callers", "");
Console.WriteLine(new string('-', 80));

foreach (var (name, fullName, count, id) in topCalled)
{
    Console.WriteLine($"{fullName,-60} {count,15}");
}
