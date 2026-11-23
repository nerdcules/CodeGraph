using System.Text.Json.Serialization;

namespace CodeGraph.Contracts;

/// <summary>
/// Source-generated JSON for every contract type, so the Native AOT CLI serialises without reflection. camelCase,
/// nulls left out. Query cells are registered by their runtime types (string, long, double, bool).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(Symbol))]
[JsonSerializable(typeof(Page<Symbol>))]
[JsonSerializable(typeof(Page<Hit>))]
[JsonSerializable(typeof(Outline))]
[JsonSerializable(typeof(Traversal))]
[JsonSerializable(typeof(Impact))]
[JsonSerializable(typeof(TestList))]
[JsonSerializable(typeof(Snippet))]
[JsonSerializable(typeof(QueryResult))]
[JsonSerializable(typeof(Schema))]
[JsonSerializable(typeof(Status))]
[JsonSerializable(typeof(ClientConfig))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(bool))]
public sealed partial class ContractsJson : JsonSerializerContext;

/// <summary>
/// <c>codegraph.client.json</c>, found from the working directory upwards: which graph a script or the CLI opens
/// when no path is passed and <c>CODEGRAPH_DB</c> is not set. A relative <see cref="Db"/> is relative to the file.
/// </summary>
public sealed record ClientConfig(string? Db);
