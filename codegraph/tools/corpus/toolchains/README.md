# Older toolchains

One image per toolchain a repo might still be on, with the .NET 10 runtime copied in beside it so codegraph-index
and codegraph-mcp run unchanged. `run.py --toolchains dotnet8,node20,...` builds each, builds the corpus image on
it, and runs the languages it serves in both dialects, pinned to the level the image's SDK accepts.

| toolchain | image | tests | level pin |
|---|---|---|---|
| `dotnet8` | .NET 8 SDK | MSBuildLocator finds only SDK 8, so Roslyn's build host loads MSBuild 17.8 into a .NET 10 process; `net8.0` projects (C# 12 by default, C# 7.3 in the classic dialect) | `net8.0` |
| `node20` | Node 20 | the extractor and its TypeScript 6 on the oldest Node it supports; ES5 CommonJS workspaces | per dialect |
| `jdk17` | Temurin JDK 17 with Maven | scip-java (mounted from the host) on JDK 17 reading `--release 17` code | `17` |
| `go121` | Go 1.21 | scip-go (mounted from the host) driving the Go 1.21 toolchain, `go 1.21` modules | `1.21` |
| `rust170` | Rust 1.70 with its own rust-analyzer | an old rust-analyzer's SCIP output through the importer, edition 2018 and 2021 | per dialect |
| `python38` | Python 3.8 | scip-python (mounted from the host) over a 3.8 interpreter, `requires-python >=3.8` | per dialect |

The host's indexers that the image does not provide are mounted as in a `--base` run (coursier's scip-java, scip-go,
scip-python); the language toolchain itself always comes from the image. The images need the network once, when
they are built; the runs do not.
