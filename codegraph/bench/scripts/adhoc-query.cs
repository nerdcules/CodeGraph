#:project ../../src/CodeGraph.Client/CodeGraph.Client.csproj
#:property PublishAot=false
// An ad hoc SQL question: the 10 most-called methods and how many distinct projects call each.
// dotnet run adhoc-query.cs
using CodeGraph.Client;
using CodeGraph.Contracts;

using var graph = CodeGraphClient.Open();
var result = await graph.QueryAsync(
    """
    WITH project_of AS (
        SELECT f.file_path, p.name AS project FROM edges e
        JOIN nodes p ON p.id = e.from_id AND p.kind = 'Project'
        JOIN nodes f ON f.id = e.to_id AND f.kind = 'File'
        WHERE e.kind = 'Contains')
    SELECT callee.full_name, count(*) AS calls, count(DISTINCT po.project) AS projects
    FROM edges e
    JOIN nodes callee ON callee.id = e.to_id AND callee.kind = 'Method'
    JOIN nodes caller ON caller.id = e.from_id
    LEFT JOIN project_of po ON po.file_path = caller.file_path
    WHERE e.kind = 'Calls'
    GROUP BY callee.full_name
    ORDER BY calls DESC, callee.full_name
    """, limit: 10);

Console.WriteLine($"{result.Total} called methods; top {result.Items.Count}:");
foreach (var row in result.Items)
{
    Console.WriteLine($"  {row[1],5} calls from {row[2],2} projects  {row[0]}");
}
