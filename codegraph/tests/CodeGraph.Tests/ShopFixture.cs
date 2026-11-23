using CodeGraph.Core;
using CodeGraph.Core.Live;
using CodeGraph.Mcp;

namespace CodeGraph.Tests;

/// <summary>
/// A small repo indexed by the real indexer and written to a graph file, with its sources on disk so snippets
/// read real lines: an interface and its implementation, a service over it, a caller, and two tests. The parity
/// tests ask the MCP tools and <c>CodeGraphClient</c> the same questions about it.
/// </summary>
public sealed class ShopFixture : IAsyncLifetime
{
    public const string Source =
        """
        using System;

        namespace Shop
        {
            public interface IRepository
            {
                void Save(Order order);
                Order? Find(int id);
            }

            public sealed class Order
            {
                public int Id { get; init; }
                public decimal Total { get; set; }
            }

            public sealed class SqlRepository : IRepository
            {
                public void Save(Order order)
                {
                    Validate(order);
                }

                public Order? Find(int id) => null;

                private static void Validate(Order order)
                {
                    _ = order.Total;
                }
            }

            public sealed class OrderService(IRepository repository)
            {
                public void Place(Order order)
                {
                    repository.Save(order);
                }

                public Order? Lookup(int id) => repository.Find(id);
            }

            public sealed class Checkout(OrderService service)
            {
                public void Run()
                {
                    service.Place(new Order());
                }
            }
        }

        namespace Shop.Tests
        {
            [AttributeUsage(AttributeTargets.Method)]
            public sealed class FactAttribute : Attribute
            {
            }

            public class OrderServiceTests
            {
                [Fact]
                public void Place_saves()
                {
                    new OrderService(new SqlRepository()).Place(new Order());
                }

                [Fact]
                public void Repository_saves()
                {
                    new SqlRepository().Save(new Order());
                }
            }
        }
        """;

    public const string FileName = "ParityShop.cs";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "codegraph-tests", "shop-" + Guid.NewGuid().ToString("n"));

    public string DbPath => Path.Combine(_directory, ".codegraph", "graph.db");

    /// <summary>The folder the graph's file paths are relative to in answers (the adhoc project's folder).</summary>
    public string Root => Path.GetDirectoryName(Indexed.PathOf(FileName))!;

    public GraphSnapshot Snapshot { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var indexed = await Indexed.OfAsync((FileName, Source));
        Directory.CreateDirectory(Root);
        await File.WriteAllTextAsync(Indexed.PathOf(FileName), Source);
        using (var store = new GraphStore(DbPath))
        {
            store.Write(indexed.Nodes, indexed.Edges);
        }

        using var reader = GraphStore.Open(DbPath, readOnly: true, TimeSpan.FromSeconds(5));
        Snapshot = reader.LoadSnapshot();
    }

    /// <summary>The MCP tools over a read-only store, answering from the snapshot as the server does, or from SQL.</summary>
    public (CodeGraphTools Tools, GraphStore Store) Tools(bool snapshot)
    {
        var store = GraphStore.OpenReader(DbPath, snapshot ? Snapshot : null, TimeSpan.FromSeconds(5));
        var tools = new CodeGraphTools(store, new IndexFreshness(DbPath, TimeSpan.FromSeconds(3)), new Telemetry(null),
            new LiveClient(DbPath), new SemanticSearch(null), new GraphSnapshotCache(DbPath), new AnswerPaths(Root));
        return (tools, store);
    }

    public ValueTask DisposeAsync()
    {
        TempDb.TryDelete(_directory);
        return ValueTask.CompletedTask;
    }
}
