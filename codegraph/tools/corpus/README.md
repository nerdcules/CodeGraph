# Generated corpus

Synthetic codebases with a known answer key, to test each extractor against. `gen.py` builds one seeded model
(modules, classes, interfaces, inheritance, overrides, overloads, generics, nested types, statics, lambdas, tests,
an entry point, planted dead code, and Angular components, services and templates) and writes it out as one
language (C#, Java, TypeScript, JavaScript, Angular, Go, Rust or Python), in one construction. `expected.json` lists every method, type and edge the graph should hold.
`check.py` indexes the codebase, exports the graph (`codegraph-index export --dead-code`) and scores it.

```bash
python3 tools/corpus/run.py --base <local image with the .NET 10 SDK, Node 22.22+ and Python 3> --jobs 4 --seeds 1,2
python3 tools/corpus/run.py --langs java --profiles large      # one slice
python3 tools/corpus/gen.py --lang csharp --profile collisions --out /tmp/c && \
  python3 tools/corpus/check.py --corpus /tmp/c --indexer "dotnet $PWD/src/CodeGraph.Indexer/bin/Release/net10.0/codegraph-index.dll" --out /tmp/c.json
```

`run.py` publishes `codegraph-index`, builds the image from `Dockerfile` and runs one container per language,
profile and seed, `--jobs` at a time. With `--base`, the host's JDK, Maven, scip-java and their caches are mounted
read-only, along with the host's Go toolchain and scip-go and its Rust toolchain with rust-analyzer, and the
containers run with `--network none`. Go and Rust have no inheritance, so they skip `deep-hierarchy`. Python
needs scip-python (`npm install -g @sourcegraph/scip-python`), mounted the same way; without it `run.py` skips
Python and says so. Python has no single-file or multi-project construction. Results go to `artifacts/corpus/`: one JSON per run,
`summary.md` and `summary.json`.

| profile | construction |
|---|---|
| `tiny` | one module, three classes |
| `classic` | three modules: inheritance, interfaces, overloads, generics, nested types, lambdas, tests |
| `deep-hierarchy` | a 24-class inheritance chain with overrides and `super` calls at each level |
| `wide-fanout` | a hub every method calls, and one method calling everything |
| `generics-nested` | mostly generic and nested types, lambdas |
| `collisions` | the same type, interface and method names in every module |
| `single-file` | every module in one file (C#, TypeScript, Rust) |
| `per-module` | one file per module (a holder class with nested types in Java) |
| `multi-project` | a project per module: C# projects in a `.slnx`, Maven modules, TypeScript project references, Angular libraries, a `go.work`, a Cargo workspace |
| `large` | 20 modules, 500+ types, about 6,000 methods |

What is scored, per edge kind: precision (recorded edges that are in the key) and recall (key edges that were
recorded). Also: methods found, planted dead methods reported, and live methods (called, tests, entry points
the language's route knows, lifecycle hooks, overrides of called methods) wrongly reported as dead.

Conventions the key follows, because every route does: no self-calls (recursion), constructors as `<init>`,
calls to an interface member go to the interface, and calls inside a lambda belong to the enclosing method.
