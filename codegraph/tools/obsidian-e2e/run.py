#!/usr/bin/env python3
"""
Run the CodeGraph plugin end to end inside real Obsidian, one container per Obsidian version and corpus language.

    python3 tools/obsidian-e2e/run.py --base foundry-field-test:none
    python3 tools/obsidian-e2e/run.py --base foundry-field-test:none --versions 1.8.9 --langs csharp,typescript

Publishes codegraph-index and codegraph-mcp from this checkout, builds one image per Obsidian version (downloading
that version once, at build time), and runs each container with --network none. Results, logs and screenshots
land in --results; summary.md lists every step per run.
"""
from __future__ import annotations

import argparse
import concurrent.futures as cf
import json
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
IMAGE = "codegraph-obsidian"
# The oldest version the plugin's manifest accepts (minAppVersion), and a recent one.
DEFAULT_VERSIONS = "1.5.12,1.8.9"


def sh(cmd: list[str], **kw) -> subprocess.CompletedProcess:
    return subprocess.run(cmd, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, **kw)


def build(base: str, versions: list[str], publish: str):
    print("publishing codegraph-index and codegraph-mcp …", flush=True)
    for project, folder in (("CodeGraph.Indexer", publish), ("CodeGraph.Mcp", publish + "-mcp")):
        p = sh(["dotnet", "publish", os.path.join(REPO, "src", project), "-c", "Release", "-o", folder, "-v", "q", "-nologo"])
        if p.returncode != 0:
            sys.exit(p.stdout[-3000:])
    p = sh(["node", os.path.join(REPO, "obsidian-plugin", "build.mjs")])
    if p.returncode != 0:
        sys.exit(p.stdout)
    for version in versions:
        print(f"building image {IMAGE}:{version} …", flush=True)
        p = sh(["docker", "build", "-q", "-t", f"{IMAGE}:{version}", "--build-arg", f"BASE={base}",
                "--build-arg", f"OBSIDIAN_VERSION={version}", HERE])
        if p.returncode != 0:
            sys.exit(p.stdout[-3000:])


def run_one(version: str, lang: str, profile: str, publish: str, results: str) -> dict:
    name = f"obsidian-{version}-{lang}-{profile}"
    args = ["docker", "run", "--rm", "--name", f"cg-obsidian-{version}-{lang}-{os.getpid()}", "--network=none",
            "--shm-size=1g", "--cpus=2", "--memory=4g", "--user", f"{os.getuid()}:{os.getgid()}",
            "-v", f"{publish}:/opt/codegraph/index:ro",
            "-v", f"{publish}-mcp:/opt/codegraph/mcp:ro",
            "-v", f"{os.path.join(REPO, 'obsidian-plugin')}:/opt/codegraph/obsidian-plugin:ro",
            "-v", f"{os.path.join(REPO, 'extractors', 'typescript')}:/opt/codegraph/extractors/typescript:ro",
            "-v", f"{os.path.join(REPO, 'tools', 'corpus')}:/opt/corpus:ro",
            "-v", f"{HERE}:/opt/e2e:ro",
            "-v", f"{results}:/results",
            f"{IMAGE}:{version}", lang, profile]
    started = time.monotonic()
    p = sh(args)
    open(os.path.join(results, f"{name}.out"), "w").write(p.stdout)
    path = os.path.join(results, f"{name}.json")
    result = json.load(open(path)) if os.path.exists(path) else dict(obsidian=version, language=lang, ok=False, steps=[], error=p.stdout[-2000:])
    result.update(profile=profile, wall_seconds=round(time.monotonic() - started, 1))
    json.dump(result, open(path, "w"), indent=1)
    print(f"  [{result['wall_seconds']:6.1f}s] {name:44} {'PASS' if result.get('ok') else 'FAIL'} "
          f"{result.get('passed', 0)}/{result.get('total', 0)} steps", flush=True)
    return result


def summarise(results: list[dict], out: str) -> str:
    names = []
    for r in results:
        for s in r.get("steps", []):
            if s["name"] not in names:
                names.append(s["name"])
    cols = [f"{r['obsidian']} {r['language']}" for r in results]
    lines = ["| step | " + " | ".join(cols) + " |", "|---|" + "---|" * len(cols)]
    for n in names:
        cells = []
        for r in results:
            s = next((s for s in r.get("steps", []) if s["name"] == n), None)
            cells.append("–" if s is None else ("ok" if s["ok"] else "**fail**"))
        lines.append(f"| {n} | " + " | ".join(cells) + " |")
    failures = [f"- Obsidian {r['obsidian']} {r['language']}, {s['name']}: {s.get('error', '')[:300]}"
                for r in results for s in r.get("steps", []) if not s["ok"]]
    failures += [f"- Obsidian {r['obsidian']} {r['language']}: {r['error'][:300]}" for r in results if r.get("error")]
    text = "# Obsidian end-to-end\n\n" + "\n".join(lines) + "\n\n## Failures\n\n" + ("\n".join(failures) or "None.") + "\n"
    open(os.path.join(out, "summary.md"), "w").write(text)
    json.dump(results, open(os.path.join(out, "summary.json"), "w"), indent=1)
    return text


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--base", required=True, help="local image with the .NET 10 SDK, Node 22+ and Python 3")
    ap.add_argument("--versions", default=DEFAULT_VERSIONS)
    ap.add_argument("--langs", default="csharp,typescript")
    ap.add_argument("--profile", default="classic")
    ap.add_argument("--jobs", type=int, default=2)
    ap.add_argument("--results", default=os.path.join(REPO, "artifacts", "obsidian-e2e"))
    ap.add_argument("--no-build", action="store_true")
    a = ap.parse_args()
    results = os.path.abspath(a.results)
    publish = os.path.join(results, ".publish")
    os.makedirs(results, exist_ok=True)
    versions = a.versions.split(",")
    if not a.no_build:
        build(a.base, versions, publish)
    jobs = [(v, l) for v in versions for l in a.langs.split(",")]
    print(f"{len(jobs)} runs, {a.jobs} at a time, network none", flush=True)
    with cf.ThreadPoolExecutor(a.jobs) as pool:
        done = list(pool.map(lambda j: run_one(j[0], j[1], a.profile, publish, results), jobs))
    print("\n" + summarise(done, results))
    return 0 if all(r.get("ok") for r in done) else 1


if __name__ == "__main__":
    sys.exit(main())
