using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace CodeGraph.Indexer;

/// <summary>
/// Keeps a loaded <see cref="Solution"/> in step with the working tree without reloading it through MSBuild:
/// edited files get new text, new files join the project whose directory holds them, deleted files leave.
/// Roslyn's incremental compilations then re-bind only what changed, which is what makes watch mode cheap.
/// </summary>
public static class WorkspaceSync
{
    private static readonly string[] IgnoredSegments = ["bin", "obj", ".git", ".codegraph", ".vs", "node_modules"];

    /// <summary>Project and build files: a change to one of these needs a real reload, since globs and references may have moved.</summary>
    public static bool IsProjectFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".sln" or ".slnx" or ".props" or ".targets";

    /// <summary>C# source outside build output and tooling folders.</summary>
    public static bool IsSourceFile(string path) =>
        string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase)
        && !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => IgnoredSegments.Contains(segment, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Applies the current state of <paramref name="paths"/> on disk (via <paramref name="read"/>, which returns null
    /// for a file that no longer exists) to <paramref name="solution"/>. Returns the same solution when nothing applies.
    /// </summary>
    public static Solution Apply(Solution solution, IEnumerable<string> paths, Func<string, string?> read)
    {
        foreach (var path in paths)
        {
            var text = read(path);
            var documentIds = solution.GetDocumentIdsWithFilePath(path);

            if (text is null)
            {
                foreach (var id in documentIds)
                {
                    solution = solution.RemoveDocument(id);
                }

                continue;
            }

            if (documentIds.Length > 0)
            {
                foreach (var id in documentIds)
                {
                    solution = solution.WithDocumentText(id, SourceText.From(text), PreservationMode.PreserveIdentity);
                }

                continue;
            }

            // A new file: MSBuild would glob it into the project whose directory contains it, so do the same.
            // Multi-target projects open once per TFM with the same path, and the file belongs in each.
            foreach (var project in ProjectsContaining(solution, path))
            {
                solution = solution.AddDocument(DocumentId.CreateNewId(project.Id), Path.GetFileName(path), SourceText.From(text), filePath: path);
            }
        }

        return solution;
    }

    private static IReadOnlyList<Project> ProjectsContaining(Solution solution, string path)
    {
        var best = new List<Project>();
        var bestLength = -1;
        foreach (var project in solution.Projects.Where(p => p.Language == LanguageNames.CSharp && p.FilePath is not null))
        {
            var directory = Path.GetDirectoryName(project.FilePath)!;
            if (!path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (directory.Length > bestLength)
            {
                best.Clear();
                bestLength = directory.Length;
            }

            if (directory.Length == bestLength)
            {
                best.Add(project);
            }
        }

        return best;
    }
}
