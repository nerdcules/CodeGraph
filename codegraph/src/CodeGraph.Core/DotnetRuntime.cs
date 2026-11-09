namespace CodeGraph.Core;

/// <summary>Where the .NET runtime running this process lives, for handing to a child apphost.</summary>
public static class DotnetRuntime
{
    /// <summary>
    /// The .NET root this process runs on: <c>&lt;root&gt;/shared/Microsoft.NETCore.App/&lt;version&gt;/</c>, three
    /// levels up. An apphost looks for .NET in DOTNET_ROOT or the machine's default location, not on PATH, so
    /// under a version manager (mise, asdf) or a user-local install it needs this passed explicitly.
    /// </summary>
    public static string? Root()
    {
        var runtime = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory().TrimEnd('/', '\\'));
        var root = runtime.Parent?.Parent?.Parent;
        return root is not null && Directory.Exists(Path.Combine(root.FullName, "shared")) ? root.FullName : null;
    }
}
