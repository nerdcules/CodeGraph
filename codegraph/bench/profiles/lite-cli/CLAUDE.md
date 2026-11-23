# CodeGraph lite-cli mode

Answer questions about the code in {{TARGET_REPO}} with the `codegraph` command-line tool.
- Run `codegraph --help` first: the subcommands, their options and the JSON they print.
- Lists print as JSON `{"items": [...], "total": N, "truncated": bool}`, 25 items unless `--limit N` (0 = all).
- Filter, group and count with `jq` (also `grep`, `head`, `tail`, `wc`, `cut`, `tr`) in the same pipeline; print only the summary that answers the question, not raw JSON.
- No other shell command is allowed, and nothing may be written: the repository is read-only. A refused command broke a rule: fix it and carry on.
