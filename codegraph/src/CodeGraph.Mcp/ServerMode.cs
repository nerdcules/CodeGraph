using Microsoft.Extensions.DependencyInjection;

namespace CodeGraph.Mcp;

internal static class ServerMode
{
    /// <summary>The server's tools in direct mode; none in the lite modes (CODEGRAPH_MODE).</summary>
    public static IMcpServerBuilder WithToolsWhen(this IMcpServerBuilder builder, bool advertise) =>
        advertise ? builder.WithToolsFromAssembly() : builder;
}
