# Obsidian end-to-end

This harness runs the CodeGraph plugin inside real Obsidian, headless in Docker, against a generated codebase from
[tools/corpus](../corpus/README.md). It tests what the unit tests and the corpus checks can't:

- Obsidian's own link resolver;
- its frontmatter parser;
- the plugin's commands and live panel in the real app.

```bash
python3 tools/obsidian-e2e/run.py --base foundry-field-test:none                      # 1.5.12 and 1.8.9, C# and TypeScript
python3 tools/obsidian-e2e/run.py --base <image> --versions 1.8.9 --langs csharp --no-build
```

`--base` is a local image with the .NET 10 SDK, Node 22 or later and Python 3, the same one the corpus uses.
`run.py` does the following:

1. Publishes `codegraph-index` and `codegraph-mcp`.
2. Builds one image per Obsidian version. The [Dockerfile](Dockerfile) adds Xvfb and the libraries Electron needs,
   then unpacks that version's Linux build. This is the only step that needs the network.
3. Runs one container per version and language with `--network none`. With no network, Obsidian can't fetch a
   newer `app.asar`, so the version tested is the version named.

The default versions are 1.5.12, the plugin's `minAppVersion`, and 1.8.9. Obsidian isn't open source, so build
these images locally and never publish them.

## What a run does

[run_e2e.sh](run_e2e.sh) sets up the container:

1. Generates and indexes a corpus codebase.
2. Writes the repo's `.mcp.json` the way `codegraph-index init` would.
3. Installs the plugin with `install-plugin.mjs` and gives it settings.
4. Writes Obsidian's own config so it opens the vault straight away.
5. Starts Obsidian under Xvfb with the DevTools protocol on a local port.

[e2e.mjs](e2e.mjs) then drives it through [cdp.mjs](cdp.mjs), a small DevTools client built on Node's own `fetch` and
`WebSocket`, so the harness has no npm dependencies. These are the steps:

| step | checks |
| --- | --- |
| Obsidian opens the vault | the workspace is ready |
| the plugin loads | it is enabled, and it set its server and indexer up from `.mcp.json` and the data file |
| Rebuild code notes | the command writes the notes and Obsidian's metadata cache holds every one |
| every link resolves | `metadataCache.getFirstLinkpathDest` finds each link's note, each `#heading` is one of its headings, and nothing under `_code/` is in `unresolvedLinks` |
| frontmatter parses | each code note's frontmatter has the fields Dataview queries, with the right types |
| the live panel answers | for the most-used type, opened in a note |
| picking a member | the drop-down changes member, the Callers tab lists rows, and every row link resolves |
| clicking a row | opens that row's note |
| status bar | reads `CodeGraph … fresh` |
| .mcp.json approval | with the approval dropped from the data file, the plugin uses the installed binaries and shows the entry in a dialog; "Use it" records the approval and the repo's server is used again |
| Find symbol | the search box suggests, and choosing opens the note |
| Insert code link | the link lands in a written note under `docs/`, and Obsidian resolves it, so the type gets a backlink |
| re-index outside Obsidian | after a class is added and `codegraph-index` re-runs, the watcher rebuilds the notes and the new type's note appears (C# and TypeScript) |
| stale banner | a source edit without a re-index shows the banner |
| no plugin errors | no exception or console error mentioning codegraph |

Each run writes `<name>.json` with every step's outcome and details, and `<name>.out` with the container's output.
It also keeps Obsidian's log and takes screenshots of the panel, the search box, the stale banner and the final
state. `summary.md` puts the steps side by side for every version and language.

## Screenshots for the docs

[screenshots.py](screenshots.py) uses the same image to take the screenshots in
[docs/obsidian.md](../../docs/obsidian.md). It runs against this repo's own graph, not a corpus codebase, so the notes
show real names:

```bash
./install.sh                                    # installs codegraph-index and codegraph-mcp, indexes this repo
python3 tools/obsidian-e2e/screenshots.py       # writes docs/images/obsidian-*.png
python3 tools/obsidian-e2e/screenshots.py --version 1.8.9 --type CodeGraph.Core.GraphStore --member Replace
```

The script does the following:

1. Copies the repo's sources, generated `obj/` sources and graph to a scratch folder.
2. Mounts the copy at the repo's own path, and the NuGet package folder read-only, so the graph's file paths stay
   valid and the panel reports the index as fresh.
3. Starts Obsidian 1.8.9 at 1600×1000 with the dark theme and `--network none`.
4. Runs [shots.mjs](shots.mjs), which opens each note, panel tab and report and saves one PNG each.

The settings screenshot shows a placeholder repo folder, so no home folder lands in the docs. Nothing is saved
with it.

## Notes

- The harness turns off restricted mode and enables the plugin through `app.plugins`, Obsidian's internal plugin
  manager. Other checks read `app.metadataCache`. Both are internal, undocumented APIs: if a future version moves
  them, the harness fails at the step that uses them, not silently.
- Obsidian's D-Bus warnings in the log are expected: the container has no session bus.
- TypeScript declaration files (`.d.ts`) aren't indexed by the extractor, so the steps that edit a source file
  pick a regular `.ts` file.
