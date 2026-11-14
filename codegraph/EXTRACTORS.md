# Languages and extractors

[CodeGraph](README.md) · [Docs](docs/README.md) · [MCP server](MCP-SERVER.md) · **Extractors** · [Benchmarks](BENCHMARKS.md) · [Prompts](PROMPTS.md) · [Changelog](CHANGELOG.md) · [Claude Code plugin](plugin/README.md) · [Obsidian plugin](obsidian-plugin/README.md)

CodeGraph's store and MCP tools know nothing about C#. What a language needs is an *extractor*: a program that
reads the code with that language's own compiler and writes graph records. Three routes are built in.

| route | languages | how to index | live tools (`--watch`) |
|---|---|---|---|
| Roslyn, in process | C# | `codegraph-index App.sln` | Roslyn, every tool |
| Node extractor, `extractors/typescript` | TypeScript, JavaScript, Angular | `codegraph-index angular.json` or `tsconfig.json` | typescript-language-server, bundled |
| SCIP indexers | Java, Kotlin, Scala, Python, Go, Rust | `codegraph-index pom.xml`, `build.gradle`, `pyproject.toml`, `go.mod` or `go.work`, `Cargo.toml` | jdtls, pyright, gopls or rust-analyzer when on PATH |

SCIP indexers are found on PATH or in the folders their installers use (coursier, `go install`, cargo), since
coursier does not add its folder to PATH. SCIP ids are `scip:` plus the symbol without its version, so a
release bump keeps them. A folder works as a target too. `codegraph-index init` picks the route from what the repo holds. A C# solution
with an Angular `ClientApp/` gets both, in one graph.

## What each route records

| | C# | TypeScript / Angular | SCIP |
|---|---|---|---|
| types, members, signatures, modifiers | yes | yes | names and kinds; modifiers read from the source line |
| calls, property reads, overrides, implementations | yes | yes | from references and `is_implementation` |
| calls into the framework and packages (External nodes) | yes | yes, `lib` and npm packages | yes, methods only |
| tests and `TestedBy` | xUnit, NUnit, MSTest | Jasmine, Jest, Vitest `it` / `test` blocks | `@Test` (JUnit, TestNG), `test_*` (Python), `TestXxx` (Go) |
| framework entry points | framework-triggered methods | lifecycle hooks, decorated methods, templates | Spring annotations, `main` |
| dependency injection | | constructor parameters and `inject()` (`Injects`) | |
| templates | | Angular templates: `Renders` and template calls | |
| incremental | per file | whole workspace, a few seconds | whole project |
| `data_flow` | yes | no | no |

Indexers differ in what they put in the index, and the importer evens that out:

- **scip-java** lists a method's override in both directions and a type's supertypes transitively. Only the
  subtype-to-supertype direction and direct supertypes are kept.
- **rust-analyzer** emits no relationships and names impl members `impl#[Self][Trait]member()`. The member goes
  to its self type, and the trait in the descriptor becomes `Implements` edges. `#[test]` marks a test.
  A rust-analyzer from 2023 (1.70) is read too: it has no `--output` (the indexer runs it the old way), writes
  a file module's path reversed (restored from the file path), names impl members `Type#Trait#member()` (read
  as the form above, so ids match), records no symbol kinds (a trait is told from its `trait` line) and no
  enclosing ranges (member bodies are found by brace matching). It does not resolve calls through `dyn Trait`;
  those stay missing, and the graph's notes say the indexer is older than the tested one.
- **Any indexer without enclosing ranges** gets member bodies by brace matching in the source (the indented block
  for Python), so references still land in the member that holds them; the graph's notes say so.
- **scip-go** calls interface members `MethodSpecification`; they are methods. Go's generated test main lies
  outside the module and is skipped. A `go.work` root is indexed through each module it uses.
- **scip-python** writes declarations as a fenced block in the documentation, which becomes the signature. It
  references the class where code calls it (`Cache(1)`), so that is a call to `__init__`. A nested class gets no
  symbol information, so its bases come from its `class Name(...)` line. Without `pip` on PATH it gets an empty
  environment rather than stopping.

SCIP is the breadth route. It tells calls and reads apart only by the target's kind, and it carries no
attributes. A language that matters enough gets a native extractor, as TypeScript did.

## The record contract

An extractor prints newline-delimited JSON to stdout. `codegraph-index import [file | -]` reads it, and so does
`codegraph-index` itself when it runs a built-in extractor. Paths may be relative to the header's `root`.

```json
{"type":"header","language":"typescript","tool":"codegraph-ts 0.1.0","root":"/repo/web","target":"/repo/web/angular.json"}
{"type":"file","path":"src/app/user.service.ts"}
{"type":"node","id":"ts:src/app/user.service.ts#UserService.load","kind":"Method","name":"load","fullName":"src/app/user.service:UserService.load","signature":"load(id: number): Promise<User>","file":"src/app/user.service.ts","start":14,"end":17,"accessibility":"Public","async":true,"override":true}
{"type":"edge","from":"ts:src/app/user.service.ts#UserService.load","to":"ts:src/app/http.service.ts#HttpService.get","kind":"Calls","file":"src/app/user.service.ts"}
{"type":"search","node":"ts:src/app/user.service.ts#UserService.load","parts":[{"text":"load","weight":3},{"text":"loading user","weight":1}]}
{"type":"embed","node":"ts:src/app/user.service.ts#UserService.load","text":"Method UserService.load ..."}
{"type":"meta","key":"files","value":"47"}
```

| record | fields | notes |
|---|---|---|
| `header` | `language`, `tool`, `root`, `target` | first; `target` is what `--watch` and freshness hints name |
| `file` | `path` | stamped by the importer, so the freshness check covers it |
| `node` | `id`, `kind`, `name`, `fullName`, `signature`, `file`, `start`, `end`, `isTest`, `typeKind`, `accessibility`, `static`, `abstract`, `virtual`, `sealed`, `async`, `override`, `memberKind`, `language` | kinds and enums as in `Model.cs`; `language` defaults to the header's |
| `edge` | `from`, `to`, `kind`, `file` | `file` is the file that produced it, defaulting to the source node's; an edge to a node-less id is allowed |
| `search` | `node`, `parts` of `{text, weight}` | tokenized by the importer, as C# documents are |
| `embed` | `node`, `text` | embedded when `CODEGRAPH_EMBEDDINGS_URL` is set |
| `meta` | `key`, `value` | stored as `lang.<language>.<key>` |

Rules that keep graphs correct:

- **Ids are stable across runs.** Build them from paths and names, never positions. Prefix them with the
  language (`ts:`, `java:`); C# keeps Roslyn's doc-comment ids.
- **Edge directions follow C#.** `TestedBy` points from the member to the test. `InvokedByFramework` comes from
  a Project node named `framework`. A call to an implicit constructor goes to `<Type>.constructor`.
- **An External node needs a call to it.** The importer drops External nodes nothing points at.

An import replaces only its own languages. It writes a fresh file, copies every other language's rows across
from the old graph, and swaps the file in. A full C# rebuild does the same in the other direction.

## Language versions

Every route reads code with a compiler that knows some language versions and not the ones after them. The
graph records which, so a repo ahead of the indexer shows up in `index_status` instead of as missing edges.
The facts go in `meta` records (or, for C#, straight into the graph) under these keys, each stored as
`lang.<language>.<key>`:

| key | meaning | C# | TypeScript | SCIP |
|---|---|---|---|---|
| `compiler` | what read the code | `Roslyn 5.9.0` | `typescript 6.0.3, @angular/compiler 22.2.6` | the index's `tool_info`, `scip-java 0.12.3` |
| `newest_language_version` | the newest version that compiler knows | `C# 14` | | |
| `language_versions` | what the projects asked for | each project's effective LangVersion, `C# 12, C# 14` | each tsconfig's `target`, `ES5, ES2022` | from the build file: `Java 17`, `Go 1.18`, `Rust edition 2018`, `Python >=3.8` |
| `compile_errors` | errors that cost nodes | syntax and declaration errors in walked files | syntax errors in the workspace's own files | the index's error diagnostics |
| `first_compile_error` | one example, `File(line): code message` | yes | yes | yes |
| `language_version_errors` | of those, "feature X needs C# N" | yes | | |
| `first_language_version_error` | one example | yes | | |
| `unmapped` | shapes the route has no case for, `TypeKind.Extension x3` | Roslyn type, symbol and syntax kinds | class and interface member kinds | SCIP symbol kinds, numeric when newer than the vendored scip.proto |
| `notes` | what did not stop the index but a reader should know | an unrestored project | a deprecated tsconfig option | an indexer older than the tested one |

The run logs the same as one `compiler:` line plus warnings, and `index_status` prints a line per language.
An extractor that records nothing here still works; the status line then says so.

The counts are of what was walked: an incremental C# run reports the files it re-read. Body-level errors are
not counted on any route, because finding them is a full compile and they cost at most an edge; the
declaration-level ones are what lose nodes, and those are counted.

[docs/language-versions.md](docs/language-versions.md) says what each route reads today, how older repos are
read, and how to check a new language version.

## Live tools for other languages

`codegraph-index <target> --watch` re-indexes on save. It also answers the live tools on the graph's pipe by
driving a language server over LSP, so the MCP server needs no change.

| tool | through |
|---|---|
| `live_references` | `textDocument/references` from the declaration's name, each location mapped to the graph member holding it |
| `symbol_at` | `hover` and `definition`, joined to the graph id |
| `diagnostics` | published diagnostics, for one file or every indexed file |
| `check_change` | the proposed text sent with `didChange`; errors compared for the edited files and the files the graph says depend on them, then the text restored |
| `rename_preview` | `textDocument/rename`, shown as line edits and not applied |

Servers push diagnostics with no "done" signal, so `check_change` waits for every watched file to report and go
quiet. Otherwise it waits three times the server's measured report latency, because a file that stays clean may
get no report at all. With typescript-language-server on the RealWorld Angular app (47 files), an edit that adds an
error to a file with one dependent answers in 3.5 to 4.8 s, and a clean edit in 2.5 to 2.8 s.

`CODEGRAPH_LSP` overrides the server command for any language, for example `jdtls -data /tmp/ws`.

## Adding a language

1. **Choose the route.** If a SCIP indexer exists, add an entry to `ScipIndexers.All` with its build file
   markers, command, install hint and source extensions. If the language needs more than SCIP gives, write an
   extractor that prints the records above, modelled on `extractors/typescript`.
2. **Add the live server.** Add the language's server to `LspServer.For`.
3. **Add tests.** Add a fixture under `tests/` or the extractor's own `test/` with the edges each feature is
   responsible for, and an end-to-end test through `codegraph-index`, like `TypeScriptIndexTests`.

---

[CodeGraph](README.md) · [Docs](docs/README.md) · [MCP server](MCP-SERVER.md) · **Extractors** · [Benchmarks](BENCHMARKS.md) · [Prompts](PROMPTS.md) · [Changelog](CHANGELOG.md) · [Claude Code plugin](plugin/README.md) · [Obsidian plugin](obsidian-plugin/README.md)
