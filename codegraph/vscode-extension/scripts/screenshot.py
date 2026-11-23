#!/usr/bin/env python3
"""Renders media/screenshot-overview.png: the Overview webview (media/overview.js and overview.css, unchanged) in
headless Chromium, fed the state the extension would post for one graph, in VS Code's Dark Modern and Light Modern
colours side by side.

  codegraph-index status --json --db <repo>/.codegraph/graph.db > status.json
  python3 scripts/screenshot.py status.json <repo root> [--managed]

The colours are VS Code 1.146's: the theme-defaults themes (dark_modern.json, light_modern.json and what they
include) and, for keys those leave out (charts.*, toolbar.hoverBackground), the colour registry defaults in
workbench.desktop.main.js. Fonts are VS Code's Linux defaults. Needs chromium and ImageMagick (magick)."""

import json
import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
THEMES = {
    "dark": {"button-border": "#ffffff1a", "button-foreground": "#FFFFFF", "button-hoverBackground": "#026EC1",
             "button-background": "#0078D4", "button-secondaryBackground": "#00000000",
             "button-secondaryForeground": "#CCCCCC", "button-secondaryHoverBackground": "#2B2B2B",
             "charts-blue": "#59a4f9", "charts-green": "#89D185", "charts-orange": "#D18616", "charts-purple": "#B180D7",
             "charts-red": "#F14C4C", "charts-yellow": "#CCA700", "descriptionForeground": "#9D9D9D",
             "editor-background": "#1F1F1F", "focusBorder": "#0078D4", "foreground": "#CCCCCC",
             "textLink-foreground": "#4daafc", "toolbar-hoverBackground": "#5a5d5e50", "sideBar-background": "#181818"},
    "light": {"button-border": "#0000001a", "button-foreground": "#FFFFFF", "button-hoverBackground": "#0258A8",
              "button-background": "#005FB8", "button-secondaryBackground": "#E5E5E5",
              "button-secondaryForeground": "#3B3B3B", "button-secondaryHoverBackground": "#CCCCCC",
              "charts-blue": "#0063d3", "charts-green": "#388A34", "charts-orange": "#D18616", "charts-purple": "#652D90",
              "charts-red": "#E51400", "charts-yellow": "#BF8803", "descriptionForeground": "#3B3B3B",
              "editor-background": "#FFFFFF", "focusBorder": "#005FB8", "foreground": "#3B3B3B",
              "textLink-foreground": "#005FB8", "toolbar-hoverBackground": "#b8b8b850", "sideBar-background": "#F8F8F8"},
}


def main(status_path, root, managed):
    status = json.load(open(status_path))
    db = status["db"]
    solution = (status.get("graph") or {}).get("solution")
    state = {"installed": True, "version": status["version"], "latest": "v" + status["version"], "selected": db,
             "graphs": [{"db": db, "name": os.path.basename(root), "root": root, "managed": managed,
                         "hasTarget": bool(solution),
                         "targets": [{"file": solution, "kind": "csharp"}] if solution else [], "status": status}]}
    work = tempfile.mkdtemp()
    try:
        for f in ("overview.css", "overview.js"):
            shutil.copy(os.path.join(HERE, "media", f), work)
        shots = []
        for name, colours in THEMES.items():
            props = "\n".join(f"  --vscode-{k}: {v};" for k, v in colours.items())
            page = os.path.join(work, name + ".html")
            with open(page, "w") as f:
                f.write(f"""<!DOCTYPE html><html lang="en"><head><meta charset="UTF-8"><style>:root {{
{props}
  --vscode-font-family: system-ui, "Ubuntu", "Droid Sans", sans-serif;
  --vscode-font-size: 13px;
  --vscode-editor-font-family: "Droid Sans Mono", "monospace", monospace;
}}
html {{ background: var(--vscode-sideBar-background); }}</style>
<link rel="stylesheet" href="overview.css"></head>
<body class="vscode-{name}"><main id="app" aria-live="polite"></main>
<script>
const STATE = {json.dumps(state)};
window.acquireVsCodeApi = () => ({{ setState() {{}}, getState() {{ return undefined; }},
  postMessage(m) {{ if (m.type === 'ready') setTimeout(() => window.postMessage({{ type: 'state', state: STATE }}, '*'), 0); }} }});
</script>
<script src="overview.js"></script></body></html>""")
            png = os.path.join(work, name + ".png")
            subprocess.run(["chromium", "--headless=new", "--disable-gpu", "--no-sandbox", "--hide-scrollbars",
                            "--force-device-scale-factor=2", "--window-size=330,1000", "--virtual-time-budget=2000",
                            "--screenshot=" + png, "file://" + page], check=True, capture_output=True)
            # Trim the empty sidebar around the cards, then give back the body's margin (14 px by 12 px at 2x).
            subprocess.run(["magick", png, "-background", colours["sideBar-background"], "-trim", "+repage",
                            "-bordercolor", colours["sideBar-background"], "-border", "28x24", png], check=True)
            shots.append(png)
        out = os.path.join(HERE, "media", "screenshot-overview.png")
        subprocess.run(["magick", *shots, "-background", THEMES["dark"]["sideBar-background"], "+append", out], check=True)
        print(out)
    finally:
        shutil.rmtree(work)


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if a != "--managed"]
    if len(args) != 2:
        sys.exit(__doc__)
    main(args[0], args[1], "--managed" in sys.argv)
