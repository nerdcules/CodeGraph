#!/usr/bin/env python3
"""
A chart of the language and SDK versions CodeGraph is tested against, as SVG.

    python3 tools/corpus/versions_chart.py --out docs/images/language-versions.svg
    python3 tools/corpus/versions_chart.py --results artifacts/corpus/summary.json --out artifacts/corpus/versions.svg

The coverage chart (no --results) is read from the code, so it cannot drift from it: the Roslyn version in
Directory.Build.props, the TypeScript and Angular versions in extractors/typescript/package.json, each SCIP
indexer's tested version in ScipIndexers.cs, the dialect levels in gen.py and the toolchain images in run.py.
A test in CodeGraph.Tests checks that the committed chart names the same versions as the build.

The results chart draws one cell per language, toolchain and dialect from a run's summary.json: green when every
run scored in full, amber with the worst recall when it did not, red when a run failed, each cell naming the
compiler the graph says it was read with.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, HERE)
from gen import LEVELS  # noqa: E402
from run import TOOLCHAINS  # noqa: E402

# The newest C# the pinned Roslyn knows. RoslynIndexer.NewestLanguageVersion is the truth; the test compares them.
NEWEST_CSHARP = "C# 14"

# What the standard corpus image held in the last full run (BENCHMARKS.md, round 10); the results chart shows
# the actual compiler each run recorded.
CURRENT = {"csharp": ".NET 10 SDK", "typescript": "Node 22", "javascript": "Node 22", "angular": "Node 22",
           "java": "JDK 27", "go": "Go 1.27", "rust": "Rust 1.99", "python": "Python 3.15"}

LANGUAGES = [("csharp", "C#"), ("typescript", "TypeScript"), ("javascript", "JavaScript"), ("angular", "Angular"),
             ("java", "Java"), ("go", "Go"), ("rust", "Rust"), ("python", "Python")]
TOOL_OF = {"java": "scip-java", "go": "scip-go", "rust": "rust-analyzer", "python": "scip-python"}

STYLE = """
  text { font-family: -apple-system, 'Segoe UI', Helvetica, Arial, sans-serif; font-size: 13px; fill: #1f2328; }
  .h { font-weight: 600; font-size: 14px; }
  .m { fill: #59636e; font-size: 11.5px; }
  .chip { font-size: 12px; }
  .bg { fill: #ffffff; }
  .grid { stroke: #d1d9e0; stroke-width: 1; }
  .c1 { fill: #ddf4ff; stroke: #0969da; } .c2 { fill: #fff8c5; stroke: #9a6700; } .c3 { fill: #dafbe1; stroke: #1a7f37; }
  .c4 { fill: #fbefff; stroke: #8250df; } .c5 { fill: #f6f8fa; stroke: #d1d9e0; }
  .ok { fill: #dafbe1; stroke: #1a7f37; } .warn { fill: #fff8c5; stroke: #9a6700; } .bad { fill: #ffebe9; stroke: #cf222e; } .none { fill: #f6f8fa; stroke: #d1d9e0; }
"""


def esc(s: str) -> str:
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace('"', "&quot;")


def pins() -> dict:
    """The versions the build pins, read from the files that pin them."""
    props = open(os.path.join(REPO, "Directory.Build.props"), encoding="utf-8").read()
    roslyn = re.search(r"<RoslynVersion>([^<]+)</RoslynVersion>", props).group(1)
    package = json.load(open(os.path.join(REPO, "extractors", "typescript", "package.json"), encoding="utf-8"))["dependencies"]
    scip = open(os.path.join(REPO, "src", "CodeGraph.Indexer", "ScipIndexers.cs"), encoding="utf-8").read()
    tested = {m.group(1): m.group(2) for m in re.finditer(r'"(scip-java|scip-python|scip-go|rust-analyzer)",.*?Tested: "([\d.]+)"', scip, re.S)}
    return {"csharp": f"Roslyn {roslyn}, up to {NEWEST_CSHARP}",
            "typescript": f"typescript {package['typescript']}", "javascript": f"typescript {package['typescript']}",
            "angular": f"typescript {package['typescript']}, @angular/compiler {package['@angular/compiler']}",
            **{lang: f"{tool} {tested[tool]}" for lang, tool in TOOL_OF.items()}}


def level_label(lang: str, level: str | None) -> str:
    if level is None:
        return "no pin"
    return {"csharp": lambda v: f"{v} (LangVersion 7.3)" if LEVELS[lang]["classic"] == v and lang == "csharp" else v,
            "java": lambda v: f"Java {v}", "go": lambda v: f"Go {v}", "rust": lambda v: f"edition {v}", "python": lambda v: f"Python {v}"}.get(lang, lambda v: v)(level)


def chip(x: float, y: float, w: float, text: str, cls: str, h: float = 24) -> str:
    return (f'<rect class="{cls}" x="{x:.0f}" y="{y:.0f}" width="{w:.0f}" height="{h}" rx="6"/>'
            f'<text class="chip" x="{x + 8:.0f}" y="{y + h / 2 + 4.5:.0f}">{esc(text)}</text>')


def coverage_svg() -> str:
    p = pins()
    images = {lang: f"{spec.get('title', name)} ({name})" for name, spec in TOOLCHAINS.items() for lang in spec["langs"]}
    columns = [("Compiler, pinned or tested", "c3", 280), ("Oldest level (classic)", "c2", 150), ("Newest level (modern)", "c1", 175),
               ("Oldest SDK image", "c4", 265), ("Current SDK", "c5", 120)]
    left, row_h, top = 110, 44, 70
    starts = [left + sum(w for _, _, w in columns[:j]) for j in range(len(columns))]
    width = starts[-1] + columns[-1][2] + 16
    rows = []
    for i, (lang, name) in enumerate(LANGUAGES):
        y = top + i * row_h
        cells = [p[lang], level_label(lang, LEVELS[lang]["classic"]), level_label(lang, LEVELS[lang]["modern"]), images.get(lang, "–"), CURRENT.get(lang, "–")]
        if lang == "csharp":
            cells[1] = "C# 7.3 on a .sln"
            cells[2] = f"{LEVELS[lang]['modern']}, SDK default C#"
        rows.append(f'<line class="grid" x1="{left - 100}" y1="{y + row_h - 8}" x2="{width - 16}" y2="{y + row_h - 8}"/>')
        rows.append(f'<text class="h" x="16" y="{y + 20}">{esc(name)}</text>')
        for (_, cls, w), x, text in zip(columns, starts, cells):
            rows.append(chip(x, y + 4, w - 12, text, cls))
    height = top + len(LANGUAGES) * row_h + 70
    header = "".join(f'<text class="m" x="{x}" y="{top - 12}">{esc(title)}</text>' for (title, _, _), x in zip(columns, starts))
    caption = ("The compiler is what reads the code (the SCIP routes: the indexer version this release was tested with). The levels are the two "
               "dialects the corpus writes with one answer key. The SDK image is the oldest toolchain the corpus runs on in Docker; the current SDK is the standard corpus image.")
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} {height}" width="{width}" height="{height}" role="img" aria-labelledby="title desc">\n'
            f'<title id="title">Language and SDK versions tested</title>\n<desc id="desc">{esc(caption)}</desc>\n<style>{STYLE}</style>\n'
            f'<rect class="bg" width="{width}" height="{height}"/>\n'
            f'<text class="h" x="16" y="28">Language and SDK versions CodeGraph is tested against</text>\n'
            f'<text class="m" x="16" y="46">Read from Directory.Build.props, extractors/typescript/package.json, ScipIndexers.cs, tools/corpus/gen.py and run.py; tools/corpus/versions_chart.py redraws it.</text>\n'
            + header + "\n" + "\n".join(rows) + "\n"
            + "".join(f'<text class="m" x="16" y="{height - 36 + 16 * k}">{esc(line)}</text>\n' for k, line in enumerate(wrap(caption, 170))) + "</svg>\n")


def wrap(text: str, width: int) -> list[str]:
    lines, current = [], ""
    for word in text.split():
        if len(current) + len(word) + 1 > width:
            lines.append(current)
            current = word
        else:
            current = f"{current} {word}".strip()
    return lines + [current]


def results_svg(results: list[dict]) -> str:
    """One column per toolchain (the standard image first), one row per language, the dialects as lines in each cell."""
    runs: dict[tuple, list] = {}
    for r in results:
        runs.setdefault((r["language"], r.get("toolchain") or "standard", r.get("dialect", "modern")), []).append(r)
    toolchains = sorted({tc for (_, tc, _) in runs}, key=lambda tc: (tc != "standard", tc))
    langs = [(lang, name) for lang, name in LANGUAGES if any(k[0] == lang for k in runs)]
    dialects = [d for d in ("modern", "classic") if any(k[2] == d for k in runs)]
    left, col_w, row_h, top = 110, 390, 22 + 16 * len(dialects), 76
    width = max(left + col_w * max(1, len(toolchains)) + 16, 900)
    height = top + len(langs) * row_h + 40
    out = [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} {height}" width="{width}" height="{height}" role="img" aria-labelledby="title desc">',
           '<title id="title">Corpus results by language, toolchain and dialect</title>',
           '<desc id="desc">One cell per language and toolchain, a line per dialect: runs that scored in full, the worst recall when not, and the compiler the graph was read with. Green when every run scored in full, amber otherwise, red when a run failed.</desc>',
           f"<style>{STYLE}</style>", f'<rect class="bg" width="{width}" height="{height}"/>',
           '<text class="h" x="16" y="28">Corpus results by language, toolchain and dialect</text>',
           '<text class="m" x="16" y="46">green: every run in full · amber: worst recall shown · red: a run failed · each line: dialect, runs, the compiler the graph says it was read with</text>']
    for j, tc in enumerate(toolchains):
        title = TOOLCHAINS.get(tc, {}).get("title", "standard image" if tc == "standard" else tc)
        out.append(f'<text class="m" x="{left + j * col_w}" y="{top - 14}">{esc(title if tc == "standard" else f"{tc} · {title}")}</text>')
    for i, (lang, name) in enumerate(langs):
        y = top + i * row_h
        out.append(f'<text class="h" x="16" y="{y + 20}">{esc(name)}</text>')
        for j, tc in enumerate(toolchains):
            cells = [(d, runs[(lang, tc, d)]) for d in dialects if (lang, tc, d) in runs]
            if not cells:
                continue
            x = left + j * col_w
            status = "ok"
            lines = []
            for d, rs in cells:
                ok = [r for r in rs if r.get("ok")]
                worst = min((v["recall"] for r in ok for v in r.get("edges", {}).values()), default=1.0)
                cls = "bad" if len(ok) < len(rs) else "warn" if worst < 0.99 else "ok"
                status = {"bad": "bad", "warn": "warn" if status != "bad" else "bad", "ok": status}[cls]
                facts = (ok or rs)[0].get("compiler") or {}
                by = lambda s: next((v for k, v in facts.items() if k.endswith("." + s)), "")
                compiler = re.sub(r"\s*\(.*?\)", "", by("compiler")).split(",")[0]
                read = "; ".join(t for t in (compiler, by("language_versions")) if t)
                score = f"{len(ok)}/{len(rs)}" + ("" if cls == "ok" else f", recall {worst * 100:.0f}%" if cls == "warn" else ", failed")
                # 62 characters fit the chip; the longest line, an old rust-analyzer with its recall, is 62.
                line = f"{d} {score} · {read}"
                lines.append(line if len(line) <= 62 else line[:61] + "…")
            out.append(f'<rect class="{status}" x="{x}" y="{y + 4}" width="{col_w - 12}" height="{row_h - 10}" rx="6"/>')
            for k, line in enumerate(lines):
                out.append(f'<text class="m" x="{x + 8}" y="{y + 20 + 16 * k}">{esc(line)}</text>')
    out.append("</svg>")
    return "\n".join(out) + "\n"


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--results", help="a run's summary.json; without it, the coverage chart is drawn from the code")
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    svg = results_svg(json.load(open(a.results, encoding="utf-8"))) if a.results else coverage_svg()
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    with open(a.out, "w", encoding="utf-8") as f:
        f.write(svg)
    print(f"wrote {a.out}")


if __name__ == "__main__":
    main()
