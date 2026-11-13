# Language versions

*Author: Alan Seunarayan*

[Home](README.md) · [Installation](installation.md) · [Usage](usage.md) · [Obsidian](obsidian.md) · [Use cases](use-cases.md) · [How it works](how-it-works.md) · **Language versions** · [Performance](performance.md) · [Architecture](architecture.md)

---

## Contents

- [What each route reads with](#what-each-route-reads-with)
- [How an older repository is read](#how-an-older-repository-is-read)
- [How a newer repository is read](#how-a-newer-repository-is-read)
- [What index_status shows](#what-index_status-shows)
- [Check a new language version](#check-a-new-language-version)
- [Update the compiler](#update-the-compiler)
- [Test on older toolchains](#test-on-older-toolchains)
- [What stays the same](#what-stays-the-same)

## What each route reads with

The indexer reads code with a compiler.
Each compiler knows some language versions and not the versions after them.
The table gives the compiler of each route, where its version is set, and what it reads.

| Route | Compiler | Set in | Reads |
|---|---|---|---|
| C# | Roslyn, the version in `RoslynVersion` | `Directory.Build.props` | C# 1 to C# 14. The project's own `LangVersion` applies. |
| TypeScript, JavaScript, Angular | `typescript` and `@angular/compiler`, bundled | `extractors/typescript/package.json` | Every TypeScript syntax up to the bundled version. The repository's `tsconfig` options apply. |
| Java, Kotlin, Scala | scip-java, on the JDK on PATH | the machine | What that JDK compiles. |
| Go | scip-go, on the Go toolchain on PATH | the machine | What that toolchain compiles. |
| Rust | rust-analyzer on PATH | the machine | What that rust-analyzer parses. |
| Python | scip-python on PATH | the machine | What that pyright parses. |

The C# route and the TypeScript route pin their compiler.
The SCIP routes do not.
For them, the indexer on PATH decides, and each entry in `ScipIndexers.All` names the version that this release was tested with.

TypeScript 7 has no compiler API.
The TypeScript route therefore stays on TypeScript 6 until the extractor is ported to the new language service.

## How an older repository is read

A newer compiler reads older code.
A project on C# 7.3, a `tsconfig` from the TypeScript 2 era, or a `go.mod` that says `go 1.18` indexes in full.

Some older settings are errors for the newer compiler but do not stop it.
TypeScript 6 reports `target: ES5` and `moduleResolution: node` as deprecated.
The program still builds, and the extractor records the deprecation as a note.

A C# project must be restored.
The indexer does not run `dotnet restore`.
If the target framework's reference pack is not on the machine, every declaration has an error, and the graph has no calls into the framework.
The indexer then names the project as unrestored in its log and in `index_status`.

## How a newer repository is read

An older compiler does not read newer code.
When a project asks for a C# that Roslyn does not know, or uses a feature from a later version, the parser reports an error at that feature and continues.
The declarations under the error are not in the graph.

The indexer counts these errors and tells the "feature X is not available in C# N" family apart from other errors.
It also counts each declaration shape that it has no mapping for, such as a new `TypeKind`.
Both counts go into the graph and into `index_status`, with the first example.

The TypeScript route counts syntax errors the same way.
The SCIP routes count the error diagnostics in the index, and the symbol kinds that the importer does not know.
A kind from a newer `scip.proto` arrives as a number.

## What index_status shows

`index_status` prints one line per language in the graph:

```
csharp    : Roslyn 5.9.0 (up to C# 14); code uses C# 12, C# 14
  ⚠ 2 error(s) say the code needs a newer language version than it was read with; declarations under them are missing. First: New.cs(4): CS9260 Feature 'extensions' is not available in C# 13.0
typescript: typescript 6.0.3, @angular/compiler 22.2.6; code uses ES5
  note: tsconfig: Option 'target=ES5' is deprecated and will stop functioning in TypeScript 7.0.
java      : scip-java 0.12.3; code uses Java 17
```

A warning line means the graph is missing code.
Read it before you trust an answer that looks wrong.
[EXTRACTORS.md](../EXTRACTORS.md#language-versions) gives the keys behind these lines.

## Check a new language version

Do these steps when a language gets a new version.

1. Index a repository that uses the new features.
   Read the `compiler:` line in the log, or call `index_status`.
   A `language_version_errors` count means the compiler is too old.
   An `unmapped` count means the compiler reads the feature but the indexer has no mapping for it.
2. For C#, run the version matrix in `tests/CodeGraph.Tests/CSharpVersionMatrixTests.cs`.
   The test `The_matrix_covers_every_version_this_Roslyn_knows` fails when Roslyn knows a version that the matrix does not list.
   Add the version to the ladder.
   Add one feature per new declaration shape, with the nodes and edges that the graph must hold for it.
   Run the matrix.
   An unmapped shape or a missing node shows what to add to the walker.
3. For TypeScript, add the feature to a fixture under `extractors/typescript/test/fixtures/` and run `npm test`.
   The tests assert that `unmapped` is empty for the fixtures.
4. For a SCIP language, index the repository with the new indexer and read the `unmapped` count.
   A numeric `SymbolKind` means `scip.proto` must be updated.
5. Run the corpus in both dialects, so the same answer key is checked in the new syntax and in the old syntax.

## Update the compiler

For C#, change `RoslynVersion` in `Directory.Build.props` and run the test suite.
The matrix test tells you when the new Roslyn knows a new C#.
Then run the corpus and the older-toolchain matrix.

For TypeScript, change the `typescript` version in `extractors/typescript/package.json`, run `npm ci` and `npm test`.
A TypeScript major can remove `tsconfig` options.
The legacy fixture in `test/fixtures/legacy-app` shows whether an old workspace still builds.

For a SCIP indexer, install the new version, run the corpus, and change `Tested` in `ScipIndexers.All`.
An indexer older than `Tested` on a user's machine becomes a note in their graph, not a failure.

After each update, compare the graph of one real repository before and after.
`codegraph-index export` writes the graph as records that you can diff.

## Test on older toolchains

The corpus runs on older toolchains in Docker.
Each image has an older SDK, with the .NET 10 runtime copied in for the indexer.

```bash
python3 tools/corpus/run.py --toolchains dotnet8,node20,jdk17,go121,rust170,python38 --dialects modern,classic
```

The generator writes each codebase in two dialects.
`modern` is what a new repository gets today.
`classic` is the oldest language level that the toolchains still accept: C# 7.3 on a `.sln`, ES5 CommonJS, Java 8, Go 1.18, Rust 2018 and Python 3.8.
The answer key is the same for both, so a difference between them is a bug.
The `read with` column in the summary shows the compiler and the language level that each run exercised.
[tools/corpus/toolchains/README.md](../tools/corpus/toolchains/README.md) lists the images.

## What stays the same

These rules keep a graph compatible across versions:

- Node ids never change shape.
  C# keeps Roslyn's documentation-comment ids.
  SCIP ids drop the version field.
  TypeScript ids come from paths and names.
- `SchemaVersion` in `GraphStore.cs` changes only when the output shape changes.
  A mismatch rebuilds the graph.
  There are no migrations.
- A new enum value, such as `TypeKind.Extension`, is added at the end.
  Older graphs do not contain it.
  The install script publishes the indexer and the server together, so a server never reads a kind that it does not know.
- The meta keys in [EXTRACTORS.md](../EXTRACTORS.md#language-versions) are a contract.
  A route that records nothing still works.

---

← Previous: [How it works](how-it-works.md) · Next: [Performance](performance.md) →
