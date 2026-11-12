using System.Text;
using CodeGraph.Core;
using Scip;

namespace CodeGraph.Indexer;

/// <summary>
/// Turns a SCIP index (https://github.com/sourcegraph/scip) into a graph batch, so any language with a SCIP
/// indexer (scip-java for Java, Kotlin and Scala, scip-python, scip-go, scip-typescript, rust-analyzer, …) gets
/// the graph tools. Definitions become nodes; a reference becomes an edge from the innermost definition whose
/// enclosing range holds it (Calls to methods and members, References to types); <c>is_implementation</c>
/// relationships become Inherits, Implements and Overrides. Ids are <c>scip:</c> plus the SCIP symbol.
/// Coarser than a native extractor: calls and reads are told apart by the target's kind alone.
/// </summary>
internal static class ScipImport
{
    private const int DefinitionRole = (int)SymbolRole.Definition;
    private const int ImportRole = (int)SymbolRole.Import;
    private const int TestRole = (int)SymbolRole.Test;

    /// <summary>Java and Kotlin test and entry-point annotations, read from the source line above a definition.</summary>
    private static readonly System.Text.RegularExpressions.Regex TestAnnotation =
        new(@"^\s*@(Test|ParameterizedTest|RepeatedTest|TestFactory|TestTemplate|org\.junit\.[\w.]*Test)\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex EntryAnnotation =
        new(@"^\s*@(RequestMapping|GetMapping|PostMapping|PutMapping|DeleteMapping|PatchMapping|Bean|Scheduled|EventListener|KafkaListener|RabbitListener|JmsListener|PostConstruct|PreDestroy|Override)\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static GraphBatch Read(string path, Action<string> log)
    {
        Scip.Index index;
        using (var stream = File.OpenRead(path))
        {
            index = Scip.Index.Parser.ParseFrom(stream);
        }

        var tool = index.Metadata?.ToolInfo is { } toolInfo ? $"{toolInfo.Name} {toolInfo.Version}".Trim() : "scip";
        var root = index.Metadata?.ProjectRoot is { Length: > 0 } uri && Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile
            ? Path.GetFullPath(parsed.LocalPath)
            : Path.GetDirectoryName(Path.GetFullPath(path))!;
        var batch = new GraphBatch { Tool = tool, Root = root, Target = Path.GetFullPath(path) };
        var projectName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var projectId = $"scip:project:{projectName}";
        var frameworkId = $"scip:framework:{projectName}";

        // Symbol information across documents: kinds decide node shapes and relationship meanings.
        var info = new Dictionary<string, SymbolInformation>(StringComparer.Ordinal);
        foreach (var symbol in index.Documents.SelectMany(d => d.Symbols).Concat(index.ExternalSymbols))
        {
            info.TryAdd(symbol.Symbol, symbol);
        }

        var languages = new Dictionary<string, int>(StringComparer.Ordinal);
        var defined = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        var definitions = new List<(Document Document, string File, List<(Span Span, string Id, bool IsTest)> Scopes)>();

        // Pass 1: every definition, so references in any document can tell source from external.
        foreach (var document in index.Documents)
        {
            var file = Path.GetFullPath(Path.Combine(root, document.RelativePath));
            var language = LanguageOf(document, tool);
            languages[language] = languages.GetValueOrDefault(language) + 1;
            batch.Files.Add(file);
            var fileId = $"scip:file:{document.RelativePath}";
            batch.Nodes.Add(new GraphNode(fileId, NodeKind.File, Path.GetFileName(file), document.RelativePath, null, file, 1, 1, false, Language: language));
            Edge(batch, projectId, fileId, EdgeKind.Contains, file);

            var lines = ReadLines(file, document.Text);
            var scopes = new List<(Span, string, bool)>();
            foreach (var occurrence in document.Occurrences)
            {
                if ((occurrence.SymbolRoles & DefinitionRole) == 0 || ScipSymbol.Parse(occurrence.Symbol) is not { } symbol)
                {
                    continue;
                }

                var symbolInfo = info.GetValueOrDefault(occurrence.Symbol);
                if (Shape(symbol, symbolInfo) is not { } shape)
                {
                    continue;
                }

                var range = RangeOf(occurrence);
                var span = EnclosingOf(occurrence) ?? range;
                var id = "scip:" + occurrence.Symbol;
                var annotations = Annotations(lines, range.StartLine);
                var isTest = shape.Kind == NodeKind.Method
                    && ((occurrence.SymbolRoles & TestRole) != 0 || annotations.Any(a => TestAnnotation.IsMatch(a)) || LooksLikeTest(symbol.Name, document.RelativePath, language));
                var signature = symbolInfo?.SignatureDocumentation?.Text is { Length: > 0 } text ? Collapse(text)
                    : symbolInfo?.DisplayName is { Length: > 0 } display ? display
                    : null;
                var declaration = range.StartLine < lines.Length ? lines[range.StartLine] : "";
                var node = new GraphNode(id, shape.Kind, shape.Name, symbol.FullName, signature, file, span.StartLine + 1, span.EndLine + 1, isTest,
                    shape.TypeKind, AccessibilityOf(declaration, range.StartCharacter, shape.Name, language, symbol.Parent is not null),
                    IsStatic: Modifier(declaration, range.StartCharacter, "static"), IsAbstract: Modifier(declaration, range.StartCharacter, "abstract"),
                    IsAsync: Modifier(declaration, range.StartCharacter, "async"), IsOverride: Modifier(declaration, range.StartCharacter, "override"),
                    MemberKind: shape.MemberKind, Language: language);
                if (!defined.TryAdd(occurrence.Symbol, node))
                {
                    continue; // a second definition (a partial, a forward declaration) adds nothing
                }

                batch.Nodes.Add(node);
                scopes.Add((span, id, isTest));
                var parent = symbol.Parent is { } parentSymbol && defined.ContainsKey(parentSymbol) ? "scip:" + parentSymbol : fileId;
                Edge(batch, parent, id, EdgeKind.Contains, file);
                if (shape.Kind == NodeKind.Method && (annotations.Any(a => EntryAnnotation.IsMatch(a)) || symbol.Name == "main"))
                {
                    Edge(batch, frameworkId, id, EdgeKind.InvokedByFramework, file);
                }

                if (SearchText.Document(id, [(shape.Name, 3), (symbol.FullName, 1), (signature, 2), (string.Join(" ", symbolInfo?.Documentation ?? []), 2)]) is { } search)
                {
                    batch.Search.Add(search);
                }

                batch.EmbeddingTexts[id] = $"{shape.Kind} {symbol.FullName}\n{signature}\n{string.Join("\n", symbolInfo?.Documentation ?? [])}";
            }

            // Innermost first: of two scopes holding a reference, the later-starting and earlier-ending one.
            scopes.Sort((a, b) => b.Item1.StartLine != a.Item1.StartLine ? b.Item1.StartLine.CompareTo(a.Item1.StartLine) : a.Item1.EndLine.CompareTo(b.Item1.EndLine));
            definitions.Add((document, file, scopes));
        }

        batch.Language = languages.Count == 0 ? "unknown" : languages.MaxBy(l => l.Value).Key;
        var projectFile = batch.Target!;
        batch.Nodes.Add(new GraphNode(projectId, NodeKind.Project, projectName, projectName, tool, projectFile, 0, 0, false, Language: batch.Language));
        batch.Nodes.Add(new GraphNode(frameworkId, NodeKind.Project, "framework", $"{projectName}.framework", null, projectFile, 0, 0, false, Language: batch.Language));
        Edge(batch, projectId, frameworkId, EdgeKind.Contains, projectFile);

        // Pass 2: references, from the innermost enclosing definition (or the file) to the symbol.
        var externals = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (document, file, scopes) in definitions)
        {
            var fileId = $"scip:file:{document.RelativePath}";
            foreach (var occurrence in document.Occurrences)
            {
                if ((occurrence.SymbolRoles & (DefinitionRole | ImportRole)) != 0 || occurrence.Symbol.StartsWith("local ", StringComparison.Ordinal))
                {
                    continue;
                }

                var at = RangeOf(occurrence);
                var (owner, ownerIsTest) = scopes.FirstOrDefault(s => s.Span.Contains(at)) is { Id: not null } scope ? (scope.Id, scope.IsTest) : (fileId, false);
                if (defined.TryGetValue(occurrence.Symbol, out var target))
                {
                    var kind = target.Kind == NodeKind.Type ? EdgeKind.References : EdgeKind.Calls;
                    if (target.Id == owner || target.Kind == NodeKind.File)
                    {
                        continue;
                    }

                    Edge(batch, owner, target.Id, kind, file);
                    if (ownerIsTest && kind == EdgeKind.Calls)
                    {
                        Edge(batch, target.Id, owner, EdgeKind.TestedBy, file);
                    }
                }
                else if (ScipSymbol.Parse(occurrence.Symbol) is { IsMethod: true } external)
                {
                    var id = "scip:" + occurrence.Symbol;
                    if (externals.Add(id))
                    {
                        batch.Nodes.Add(new GraphNode(id, NodeKind.External, external.Name, external.Package is "." ? external.FullName : $"{external.Package}.{external.FullName}",
                            null, null, 0, 0, false, Language: batch.Language));
                    }

                    Edge(batch, owner, id, EdgeKind.CallsExternal, file);
                }
            }
        }

        // Relationships: what a type extends or implements, what a member overrides or implements.
        foreach (var (symbol, symbolInfo) in info)
        {
            if (!defined.TryGetValue(symbol, out var source))
            {
                continue;
            }

            foreach (var relationship in symbolInfo.Relationships.Where(r => r.IsImplementation))
            {
                if (!defined.TryGetValue(relationship.Symbol, out var target) || target.Id == source.Id)
                {
                    continue;
                }

                var kind = source.Kind == NodeKind.Type
                    ? (target.TypeKind == TypeKind.Interface ? EdgeKind.Implements : EdgeKind.Inherits)
                    : (ParentKind(relationship.Symbol, defined) == TypeKind.Interface ? EdgeKind.Implements : EdgeKind.Overrides);
                Edge(batch, source.Id, target.Id, kind, source.FilePath!);
            }
        }

        log($"scip: {index.Documents.Count} document(s) from {tool}, {defined.Count} definitions, {externals.Count} external symbols");
        return batch;
    }

    private static TypeKind ParentKind(string symbol, Dictionary<string, GraphNode> defined) =>
        ScipSymbol.Parse(symbol)?.Parent is { } parent && defined.TryGetValue(parent, out var node) ? node.TypeKind : TypeKind.None;

    private static void Edge(GraphBatch batch, string from, string to, EdgeKind kind, string file)
    {
        var edge = new GraphEdge(from, to, kind);
        batch.Edges.Add(edge);
        batch.Origins.Add(new EdgeOrigin(edge, file));
    }

    private sealed record NodeShape(NodeKind Kind, TypeKind TypeKind, MemberKind MemberKind, string Name);

    private static NodeShape? Shape(ScipSymbol symbol, SymbolInformation? info)
    {
        var kind = info?.Kind ?? SymbolInformation.Types.Kind.UnspecifiedKind;
        var name = symbol.Name;
        switch (kind)
        {
            case SymbolInformation.Types.Kind.Class or SymbolInformation.Types.Kind.Object or SymbolInformation.Types.Kind.SingletonClass:
                return new(NodeKind.Type, TypeKind.Class, MemberKind.Ordinary, name);
            case SymbolInformation.Types.Kind.Struct or SymbolInformation.Types.Kind.Union:
                return new(NodeKind.Type, TypeKind.Struct, MemberKind.Ordinary, name);
            case SymbolInformation.Types.Kind.Interface or SymbolInformation.Types.Kind.Trait or SymbolInformation.Types.Kind.Protocol or SymbolInformation.Types.Kind.TypeClass:
                return new(NodeKind.Type, TypeKind.Interface, MemberKind.Ordinary, name);
            case SymbolInformation.Types.Kind.Enum:
                return new(NodeKind.Type, TypeKind.Enum, MemberKind.Ordinary, name);
            case SymbolInformation.Types.Kind.TypeAlias:
                return new(NodeKind.Type, TypeKind.TypeAlias, MemberKind.Ordinary, name);
            case SymbolInformation.Types.Kind.Attribute when symbol.Suffix == ScipSuffix.Type:
                return new(NodeKind.Type, TypeKind.Annotation, MemberKind.Ordinary, name);
            case SymbolInformation.Types.Kind.Constructor:
                return new(NodeKind.Method, TypeKind.None, MemberKind.Constructor, "constructor");
            case SymbolInformation.Types.Kind.Property or SymbolInformation.Types.Kind.StaticProperty or SymbolInformation.Types.Kind.Getter or SymbolInformation.Types.Kind.Setter or SymbolInformation.Types.Kind.Accessor:
                return new(NodeKind.Property, TypeKind.None, MemberKind.Ordinary, name);
            case SymbolInformation.Types.Kind.Field or SymbolInformation.Types.Kind.StaticField or SymbolInformation.Types.Kind.Constant or SymbolInformation.Types.Kind.EnumMember
                or SymbolInformation.Types.Kind.Variable or SymbolInformation.Types.Kind.StaticVariable or SymbolInformation.Types.Kind.StaticDataMember:
                return symbol.Suffix == ScipSuffix.Term ? new(NodeKind.Field, TypeKind.None, MemberKind.Ordinary, name) : null;
            case SymbolInformation.Types.Kind.Parameter or SymbolInformation.Types.Kind.TypeParameter or SymbolInformation.Types.Kind.SelfParameter
                or SymbolInformation.Types.Kind.ThisParameter or SymbolInformation.Types.Kind.File or SymbolInformation.Types.Kind.Package
                or SymbolInformation.Types.Kind.Namespace or SymbolInformation.Types.Kind.Module or SymbolInformation.Types.Kind.PackageObject:
                return null;
        }

        // No kind (older indexers): the descriptor suffix says enough.
        return symbol.Suffix switch
        {
            ScipSuffix.Type => new(NodeKind.Type, TypeKind.Class, MemberKind.Ordinary, name),
            ScipSuffix.Method when name is "<init>" or "<constructor>" or "__init__" => new(NodeKind.Method, TypeKind.None, MemberKind.Constructor, "constructor"),
            ScipSuffix.Method => new(NodeKind.Method, TypeKind.None, MemberKind.Ordinary, name),
            ScipSuffix.Term => new(NodeKind.Field, TypeKind.None, MemberKind.Ordinary, name),
            _ => null,
        };
    }

    /// <summary>
    /// SCIP records no modifiers, so they are read off the declaration's own line, before the name: enough for
    /// dead-code analysis to tell private from public. Java without a modifier is package-private (Internal);
    /// Python marks private with a leading underscore and Go with a lower-case first letter.
    /// </summary>
    private static Accessibility AccessibilityOf(string line, int nameColumn, string name, string language, bool isMember)
    {
        var before = nameColumn > 0 && nameColumn <= line.Length ? line[..nameColumn] : line;
        if (System.Text.RegularExpressions.Regex.IsMatch(before, @"\bprivate\b") || name.StartsWith('#'))
        {
            return Accessibility.Private;
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(before, @"\bprotected\b"))
        {
            return Accessibility.Protected;
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(before, @"\b(public|export)\b"))
        {
            return Accessibility.Public;
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(before, @"\binternal\b"))
        {
            return Accessibility.Internal;
        }

        return language switch
        {
            Languages.Java => Accessibility.Internal,
            "python" => name.StartsWith('_') && !name.StartsWith("__", StringComparison.Ordinal) ? Accessibility.Private : Accessibility.Public,
            "go" => name.Length > 0 && char.IsLower(name[0]) ? Accessibility.Internal : Accessibility.Public,
            // TypeScript and Kotlin members are public by default; an unexported top-level TypeScript declaration is module-private.
            Languages.TypeScript or Languages.JavaScript when !isMember => Accessibility.Internal,
            _ => Accessibility.Public,
        };
    }

    private static bool Modifier(string line, int nameColumn, string modifier)
    {
        var before = nameColumn > 0 && nameColumn <= line.Length ? line[..nameColumn] : line;
        return System.Text.RegularExpressions.Regex.IsMatch(before, $@"\b{modifier}\b");
    }

    private static bool LooksLikeTest(string name, string path, string language) => language switch
    {
        "python" => name.StartsWith("test_", StringComparison.Ordinal) && Path.GetFileName(path).StartsWith("test", StringComparison.Ordinal),
        "go" => name.StartsWith("Test", StringComparison.Ordinal) && path.EndsWith("_test.go", StringComparison.Ordinal),
        "rust" => false,
        _ => false,
    };

    private static string LanguageOf(Document document, string tool)
    {
        if (document.Language is { Length: > 0 } language)
        {
            return language.ToLowerInvariant() switch
            {
                "typescriptreact" => Languages.TypeScript,
                "javascriptreact" => Languages.JavaScript,
                var other => other,
            };
        }

        return Path.GetExtension(document.RelativePath).ToLowerInvariant() switch
        {
            ".java" => Languages.Java,
            ".kt" or ".kts" => "kotlin",
            ".scala" => "scala",
            ".py" => "python",
            ".go" => "go",
            ".rs" => "rust",
            ".rb" => "ruby",
            ".ts" or ".tsx" or ".mts" or ".cts" => Languages.TypeScript,
            ".js" or ".jsx" or ".mjs" or ".cjs" => Languages.JavaScript,
            ".cs" => "csharp-scip",
            _ => tool.Split(' ')[0].Replace("scip-", "", StringComparison.Ordinal),
        };
    }

    private static string[] ReadLines(string file, string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            return text.Split('\n');
        }

        try
        {
            return File.ReadAllLines(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The annotation lines directly above (and on) a definition's line, for Java and Kotlin.</summary>
    private static List<string> Annotations(string[] lines, int line)
    {
        var found = new List<string>();
        if (line < lines.Length && lines[line].TrimStart().StartsWith('@'))
        {
            found.Add(lines[line]);
        }

        // The source can be missing or shorter than the index says (moved, edited since): read only what exists.
        for (var i = Math.Min(line, lines.Length) - 1; i >= 0 && i >= line - 8; i--)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith('@'))
            {
                found.Add(lines[i]);
            }
            else if (trimmed.Length > 0 && !trimmed.StartsWith("//", StringComparison.Ordinal) && !trimmed.StartsWith('*') && !trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                break;
            }
        }

        return found;
    }

    private static string Collapse(string text)
    {
        var one = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return one.Length > 240 ? one[..239] + "…" : one;
    }

    internal readonly record struct Span(int StartLine, int StartCharacter, int EndLine, int EndCharacter)
    {
        public bool Contains(Span other) =>
            (other.StartLine > StartLine || (other.StartLine == StartLine && other.StartCharacter >= StartCharacter))
            && (other.StartLine < EndLine || (other.StartLine == EndLine && other.StartCharacter <= EndCharacter));
    }

    // Older indexers (scip-typescript among them) still write the packed, deprecated range fields.
#pragma warning disable CS0612
    private static Span RangeOf(Occurrence occurrence) => occurrence.TypedRangeCase switch
    {
        Occurrence.TypedRangeOneofCase.SingleLineRange => new(occurrence.SingleLineRange.Line, occurrence.SingleLineRange.StartCharacter, occurrence.SingleLineRange.Line, occurrence.SingleLineRange.EndCharacter),
        Occurrence.TypedRangeOneofCase.MultiLineRange => new(occurrence.MultiLineRange.StartLine, occurrence.MultiLineRange.StartCharacter, occurrence.MultiLineRange.EndLine, occurrence.MultiLineRange.EndCharacter),
        _ => Packed(occurrence.Range) ?? default,
    };

    private static Span? EnclosingOf(Occurrence occurrence) => occurrence.TypedEnclosingRangeCase switch
    {
        Occurrence.TypedEnclosingRangeOneofCase.SingleLineEnclosingRange => new Span(occurrence.SingleLineEnclosingRange.Line, occurrence.SingleLineEnclosingRange.StartCharacter, occurrence.SingleLineEnclosingRange.Line, occurrence.SingleLineEnclosingRange.EndCharacter),
        Occurrence.TypedEnclosingRangeOneofCase.MultiLineEnclosingRange => new Span(occurrence.MultiLineEnclosingRange.StartLine, occurrence.MultiLineEnclosingRange.StartCharacter, occurrence.MultiLineEnclosingRange.EndLine, occurrence.MultiLineEnclosingRange.EndCharacter),
        _ => Packed(occurrence.EnclosingRange),
    };
#pragma warning restore CS0612

    /// <summary>The deprecated packed form: [line, start, end] on one line, or [startLine, start, endLine, end].</summary>
    private static Span? Packed(IReadOnlyList<int> range) => range.Count switch
    {
        3 => new Span(range[0], range[1], range[0], range[2]),
        4 => new Span(range[0], range[1], range[2], range[3]),
        _ => null,
    };
}

internal enum ScipSuffix { None, Namespace, Type, Term, Method, TypeParameter, Parameter, Meta, Macro }

/// <summary>A parsed SCIP symbol: <c>scheme manager package version descriptors</c>, or a local.</summary>
internal sealed record ScipSymbol(string Scheme, string Package, IReadOnlyList<(string Name, ScipSuffix Suffix)> Descriptors, string? Parent)
{
    public string Name => Descriptors.Count == 0 ? "" : Descriptors[^1].Name;

    public ScipSuffix Suffix => Descriptors.Count == 0 ? ScipSuffix.None : Descriptors[^1].Suffix;

    public bool IsMethod => Suffix == ScipSuffix.Method;

    /// <summary>Namespaces as a path, then types and members dotted: <c>src/app/user.service.ts/UserService.load</c>.</summary>
    public string FullName
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var (name, suffix) in Descriptors)
            {
                if (suffix is ScipSuffix.TypeParameter or ScipSuffix.Parameter)
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.Append(sb[^1] == '/' ? "" : ".");
                }

                sb.Append(name);
                if (suffix == ScipSuffix.Namespace)
                {
                    sb.Append('/');
                }
            }

            return sb.ToString().TrimEnd('/');
        }
    }

    public static ScipSymbol? Parse(string symbol)
    {
        if (string.IsNullOrEmpty(symbol) || symbol.StartsWith("local ", StringComparison.Ordinal))
        {
            return null;
        }

        var i = 0;
        var fields = new string[4];
        for (var f = 0; f < 4; f++)
        {
            var sb = new StringBuilder();
            while (i < symbol.Length)
            {
                if (symbol[i] == ' ')
                {
                    if (i + 1 < symbol.Length && symbol[i + 1] == ' ')
                    {
                        sb.Append(' '); // a doubled space is a literal one
                        i += 2;
                        continue;
                    }

                    break;
                }

                sb.Append(symbol[i++]);
            }

            fields[f] = sb.ToString();
            if (i >= symbol.Length)
            {
                return null;
            }

            i++; // the separating space
        }

        var descriptors = new List<(string, ScipSuffix)>();
        var starts = new List<int>();
        while (i < symbol.Length)
        {
            starts.Add(i);
            if (symbol[i] == '[' || symbol[i] == '(')
            {
                var close = symbol[i] == '[' ? ']' : ')';
                var end = symbol.IndexOf(close, i + 1);
                if (end < 0)
                {
                    return null;
                }

                descriptors.Add((symbol[(i + 1)..end], symbol[i] == '[' ? ScipSuffix.TypeParameter : ScipSuffix.Parameter));
                i = end + 1;
                continue;
            }

            var name = ReadName(symbol, ref i);
            if (name is null || i >= symbol.Length)
            {
                return null;
            }

            switch (symbol[i])
            {
                case '/': descriptors.Add((name, ScipSuffix.Namespace)); i++; break;
                case '#': descriptors.Add((name, ScipSuffix.Type)); i++; break;
                case '.': descriptors.Add((name, ScipSuffix.Term)); i++; break;
                case ':': descriptors.Add((name, ScipSuffix.Meta)); i++; break;
                case '!': descriptors.Add((name, ScipSuffix.Macro)); i++; break;
                case '(':
                    var end = symbol.IndexOf(')', i);
                    if (end < 0 || end + 1 >= symbol.Length || symbol[end + 1] != '.')
                    {
                        return null;
                    }

                    descriptors.Add((name, ScipSuffix.Method));
                    i = end + 2;
                    break;
                default:
                    return null;
            }
        }

        // The parent is the symbol without its last descriptor, unless that leaves only namespaces (a file or package).
        string? parent = null;
        for (var d = descriptors.Count - 2; d >= 0; d--)
        {
            if (descriptors[d].Item2 is ScipSuffix.Type or ScipSuffix.Term or ScipSuffix.Method)
            {
                parent = symbol[..starts[d + 1]];
                break;
            }
        }

        return new ScipSymbol(fields[0], fields[2], descriptors, parent);
    }

    private static string? ReadName(string symbol, ref int i)
    {
        if (symbol[i] == '`')
        {
            var sb = new StringBuilder();
            i++;
            while (i < symbol.Length)
            {
                if (symbol[i] == '`')
                {
                    if (i + 1 < symbol.Length && symbol[i + 1] == '`')
                    {
                        sb.Append('`');
                        i += 2;
                        continue;
                    }

                    i++;
                    return sb.ToString();
                }

                sb.Append(symbol[i++]);
            }

            return null;
        }

        var start = i;
        while (i < symbol.Length && (char.IsLetterOrDigit(symbol[i]) || symbol[i] is '_' or '+' or '-' or '$'))
        {
            i++;
        }

        return i > start ? symbol[start..i] : null;
    }
}
