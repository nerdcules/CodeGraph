# CodeGraph lite-cli mode

Answer questions about the code in {{TARGET_REPO}} with the `codegraph` command-line tool (JSON on stdout; run `codegraph --help` only for an option not listed here):
- `find <text> [--kind Type|Method|Property|Field|Event|File|Project] [--match Exact|Contains|StartsWith|EndsWith]`: symbols `{id, kind, fullName, signature, file, startLine, project, isTest, accessibility}`
- `outline <symbol>` (members), `snippet <symbol>` (source lines), `tests <symbol>` (direct test callers)
- `callers <symbol> [--depth N]`, `callees <symbol> [--depth N]`: `{target, hits}`; `impact <symbol> [--depth N]`: `{target, dependents, tests}`
- `query "<one SELECT>"`: `{columns, items: rows as arrays}`; `schema`: tables, node kinds, edge kinds
- A `<symbol>` is an id or an exact name (`Type.Method`); an ambiguous name fails and lists the ids.
- Every list (`find`'s output, `hits`, `dependents`, `tests`, `children`) is an object `{"items": [...], "total": N, "truncated": bool}` with the first 25 items unless `--limit N` (0 = all); traversal items are `{symbol, depth}`. So: `.items[].fullName`, `.hits.items[].symbol.project`, `.tests.items[].symbol.fullName`.
- Filter, group and count with `jq` (also `grep`, `head`, `tail`, `wc`, `cut`, `tr`) in the same pipeline; print only the summary that answers the question, not raw JSON.
- No other shell command is allowed, and nothing may be written: the repository is read-only. A refused command broke a rule: fix it and carry on.
