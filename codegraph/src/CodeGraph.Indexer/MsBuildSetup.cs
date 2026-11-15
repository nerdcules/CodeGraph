using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Build.Locator;

namespace CodeGraph.Indexer;

/// <summary>
/// Finds the MSBuild that loads the solution. MSBuildLocator only offers SDKs this process's runtime can load, so a
/// machine with only a newer SDK (the .NET 11 SDK under an indexer running on .NET 10) has none; without this,
/// <see cref="MSBuildLocator.RegisterDefaults"/> throws and the process dies with an unhandled exception.
/// </summary>
internal static class MsBuildSetup
{
    /// <summary>Registers MSBuild and returns null, or returns what to tell the user when no usable SDK is installed.</summary>
    public static string? Register()
    {
        if (MSBuildLocator.QueryVisualStudioInstances().Any())
        {
            MSBuildLocator.RegisterDefaults();
            return null;
        }

        return Explain(Environment.Version.Major, InstalledSdks(), SelfContained());
    }

    internal static string Explain(int runtimeMajor, IReadOnlyList<string> sdks, bool selfContained)
    {
        var found = sdks.Count == 0
            ? "no .NET SDK was found (dotnet --list-sdks lists none)"
            : $"the SDKs installed are {string.Join(", ", sdks)}";
        var message =
            $"codegraph-index cannot load the solution: it runs on .NET {runtimeMajor} and needs a .NET SDK no newer " +
            $"than {runtimeMajor}, and {found}. Install the .NET {runtimeMajor} SDK: " +
            $"https://dotnet.microsoft.com/download/dotnet/{runtimeMajor}.0";
        // A framework-dependent build can roll forward onto a newer runtime and use that runtime's SDK; a
        // self-contained one (the release archives) carries its own runtime and cannot.
        return selfContained || sdks.Count == 0
            ? message
            : message + ". Or set DOTNET_ROLL_FORWARD=LatestMajor, so it runs on the newest .NET installed and uses its SDK.";
    }

    /// <summary>The versions <c>dotnet --list-sdks</c> prints, or none when dotnet cannot be run.</summary>
    private static IReadOnlyList<string> InstalledSdks()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("dotnet", "--list-sdks")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
            {
                return [];
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split(' ', 2)[0])
                .ToList();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>True when the runtime ships inside the app's own folder, as in the self-contained release archives.</summary>
    private static bool SelfContained() =>
        Path.GetFullPath(RuntimeEnvironment.GetRuntimeDirectory())
            .StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase);
}
