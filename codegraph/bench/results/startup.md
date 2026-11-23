Measured 2026-11-22 on this Linux machine (linux-x64), Jellyfin graph (21,174 nodes, 109,420 edges) at 1fbd873.

| what | runs | median | min–max |
|---|---:|---:|---|
| `dotnet run` of a new script (cold build; CodeGraph.Client already built), SDK 10.0.113 | 10 | 3,777 ms | 2,658–4,067 ms |
| `dotnet run` of the same script again (build cache) | 10 | 639 ms | 593–685 ms |
| `codegraph status`, Native AOT build | 20 | 51.7 ms | 47.6–67.1 ms |
| `codegraph status`, JIT build (framework-dependent, runtime 10.0.13) | 20 | 209.8 ms | 186.9–268.8 ms |
| `codegraph callers GuidExtensions.IsEmpty --depth 2 --limit 0`, AOT | 20 | 317.7 ms | 257.9–332.0 ms |
| the same, JIT | 20 | 473.6 ms | 400.2–601.0 ms |

Both `dotnet run` rows run the script `callers-by-project.cs -- GuidExtensions.IsEmpty 2`, the same query as the `callers` rows.
The callers JSON with `--limit 0` is 418,051 bytes. Sizes: AOT `codegraph` 3,456,648 bytes plus `libduckdb.so`
70,529,912 bytes. win-x64: not built or measured (Native AOT cannot cross-compile between operating systems).
A compile error of 2 errors is 737 bytes of output, 205 tokens (claude-sonnet-5-5, overhead command).

## The release archive's CLI (ReadyToRun)

Measured 2026-11-23 on the same machine and Jellyfin graph file, with the `codegraph` from the v1.6.0
`codegraph-linux-x64.tar.gz` (self-contained, ReadyToRun), installed by `install.sh --version v1.6.0`. One warm-up
run, then 20 runs each, timed from Python's `subprocess.run`. The load average was 5.02 when the runs ended (8
cores), and the rows above were measured on another day, so compare them with care.

| what | runs | median | min–max |
|---|---:|---:|---|
| `codegraph status`, ReadyToRun (v1.6.0 archive) | 20 | 82.5 ms | 79.5–84.6 ms |
| `codegraph callers GuidExtensions.IsEmpty --depth 2 --limit 0`, ReadyToRun | 20 | 245.1 ms | 236.3–273.1 ms |
