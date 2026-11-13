#!/usr/bin/env python3
"""
Run the corpus matrix: every language x profile (x seed) in its own Docker container, several at once.

    python3 tools/corpus/run.py --base foundry-field-test:none --jobs 4
    python3 tools/corpus/run.py --langs java,csharp --profiles tiny,large --seeds 1,2

Each container generates a codebase (gen.py), indexes it with this checkout's codegraph-index and scores the
graph (check.py). Results land in --results as one JSON per run, plus summary.md and summary.json.

With --base, the image builds on that local image (it needs the .NET 10 SDK, Node 22.22+ and Python 3) and the
host's JDK, Maven, scip-java and their caches are mounted read-only, so nothing is downloaded and containers
run with --network none. Without it the image installs those from apt (needs network once, at build time).
"""
from __future__ import annotations

import argparse
import concurrent.futures as cf
import json
import os
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, HERE)
from gen import DIALECTS, LANGS, PROFILES, UNSUPPORTED  # noqa: E402

IMAGE = "codegraph-corpus"


def sh(cmd: list[str], **kw) -> subprocess.CompletedProcess:
    return subprocess.run(cmd, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, **kw)


def host_tools() -> dict[str, str]:
    """Host toolchains to mount: the JDK, Maven, scip-java and their caches; Go and scip-go; the Rust toolchain."""
    mounts = {}
    java = shutil.which("java")
    if java:
        props = sh([java, "-XshowSettings:properties", "-version"]).stdout
        home = next((l.split("=", 1)[1].strip() for l in props.splitlines() if "java.home =" in l), None)
        if home:
            mounts[os.path.realpath(home)] = "/opt/jdk"
    mvn = shutil.which("mvn")
    if mvn:
        home = next((l.split(":", 1)[1].strip() for l in sh([mvn, "-v"]).stdout.splitlines() if l.startswith("Maven home")), None)
        if home:
            mounts[os.path.realpath(home)] = "/opt/maven"
    go = shutil.which("go")
    if go:
        mounts[os.path.realpath(sh([go, "env", "GOROOT"]).stdout.strip())] = "/opt/go"
        gobin = sh([go, "env", "GOBIN"]).stdout.strip() or os.path.join(sh([go, "env", "GOPATH"]).stdout.strip().split(os.pathsep)[0], "bin")
        if os.path.isdir(gobin):
            mounts[gobin] = "/opt/gobin"
    rustc = shutil.which("rustc")
    if rustc:
        sysroot = sh([rustc, "--print", "sysroot"]).stdout.strip()
        if os.path.isdir(sysroot):
            mounts[sysroot] = "/opt/rust"
    scip_python = shutil.which("scip-python")
    if scip_python:  # an npm package: mount the package, run_one.sh makes a launcher for the container's node
        package = os.path.realpath(scip_python)
        while package != "/" and not os.path.exists(os.path.join(package, "package.json")):
            package = os.path.dirname(package)
        if package != "/":
            mounts[package] = "/opt/scip-python"
    user = os.path.expanduser("~")
    for host, container in [(os.path.join(os.environ.get("XDG_DATA_HOME", os.path.join(user, ".local", "share")), "coursier", "bin"), "/opt/coursier/bin"),
                            (os.path.join(user, ".cache", "coursier"), "/home/runner/.cache/coursier"),
                            (os.path.join(user, ".m2"), "/seed/m2")]:
        if os.path.isdir(host):
            mounts[host] = container
    return mounts


def build(base: str | None, out: str):
    print("publishing codegraph-index and codegraph-mcp …", flush=True)
    for project, folder in (("CodeGraph.Indexer", out), ("CodeGraph.Mcp", out + "-mcp")):
        p = sh(["dotnet", "publish", os.path.join(REPO, "src", project), "-c", "Release", "-o", folder, "-v", "q", "-nologo"])
        if p.returncode != 0:
            sys.exit(p.stdout[-3000:])
    print(f"building image {IMAGE} …", flush=True)
    args = ["docker", "build", "-q", "-t", IMAGE, HERE]
    if base:
        args[3:3] = ["--build-arg", f"BASE={base}", "--build-arg", "INSTALL_TOOLS=0"]
    p = sh(args)
    if p.returncode != 0:
        sys.exit(p.stdout[-3000:])


def run_one(job, publish: str, results: str, mounts: dict, cpus: str, memory: str, network: str, level: str | None = None, tag: str | None = None, no_live: bool = False, image: str = IMAGE) -> dict:
    lang, profile, seed, dialect = job
    name = f"{lang}-{profile}-s{seed}" + (f"-{dialect}" if dialect != "modern" else "") + (f"-{tag}" if tag else "")
    args = ["docker", "run", "--rm", "--name", f"cg-corpus-{name}-{os.getpid()}", f"--network={network}",
            f"--cpus={cpus}", f"--memory={memory}", "--user", f"{os.getuid()}:{os.getgid()}",
            *(["-e", f"CORPUS_TAG={tag}"] if tag else []), *(["-e", "CORPUS_NO_LIVE=1"] if no_live else []),
            "-v", f"{publish}:/opt/codegraph/index:ro",
            "-v", f"{publish}-mcp:/opt/codegraph/mcp:ro",
            "-v", f"{os.path.join(REPO, 'obsidian-plugin')}:/opt/codegraph/obsidian-plugin:ro",
            "-v", f"{os.path.join(REPO, 'extractors', 'typescript')}:/opt/codegraph/extractors/typescript:ro",
            "-v", f"{results}:/results"]
    for host, container in mounts.items():
        args += ["-v", f"{host}:{container}:ro"]
    started = time.monotonic()
    p = sh(args + [image, lang, profile, str(seed), dialect, *([level] if level else [])])
    path = os.path.join(results, f"{name}.json")
    result = json.load(open(path)) if os.path.exists(path) else dict(language=lang, profile=profile, ok=False, error=p.stdout[-1500:])
    result.update(seed=seed, dialect=dialect, toolchain=tag, wall_seconds=round(time.monotonic() - started, 1))
    json.dump(result, open(path, "w"), indent=1)
    line = p.stdout.strip().splitlines()[-1] if p.stdout.strip() else ""
    print(f"  [{result['wall_seconds']:6.1f}s] {name:38} {'ok ' if result.get('ok') else 'ERR'} {line[:150]}", flush=True)
    return result


def pct(v: float) -> str:
    return f"{v * 100:.1f}" if v < 1 else "100"


def vault_cell(v: dict | None) -> str:
    if not v:
        return "–"
    if not v.get("ok"):
        return "**fail**"
    return f"{v['notes']} notes, {v['links']['links']} links, {v['export_seconds']}s"


def live_cell(v: dict | None) -> str:
    if not v:
        return "–"
    if not v.get("ok"):
        return "**fail**"
    c = v["callers"]
    return f"{v['calls']} calls, {v['median_ms']} ms median, callers R={pct(c['recall'])}"


def compiler_cell(r: dict) -> str:
    """What the graph was read with: compiler and language level, plus any count that means missing code."""
    facts = r.get("compiler") or {}
    by = lambda suffix: next((v for k, v in facts.items() if k.endswith("." + suffix)), None)
    parts = [p for p in (by("compiler"), by("language_versions")) if p]
    for suffix in ("compile_errors", "language_version_errors"):
        if by(suffix) not in (None, "0"):
            parts.append(f"**{by(suffix)} {suffix.replace('_', ' ')}**")
    if by("unmapped"):
        parts.append(f"**unmapped {by('unmapped')}**")
    if by("notes"):
        parts.append(f"note: {by('notes')}")
    return "; ".join(parts).replace("|", "/") or "–"


def profile_cell(r: dict) -> str:
    cell = r["profile"] + ("" if r.get("seed", 1) == 1 else " s" + str(r["seed"]))
    if r.get("dialect", "modern") != "modern" or r.get("level"):
        cell += f" ({r.get('dialect', 'modern')}" + (f", {r['level']}" if r.get("level") else "") + ")"
    if r.get("toolchain"):
        cell += f" on {r['toolchain']}"
    return cell


def summarise(results: list[dict], out: str):
    kinds = ["Calls", "Overrides", "Implements", "Supertype", "TestedBy", "Injects", "Renders", "TemplateCalls"]
    lines = ["| language | profile | methods | calls | files | lines | index s | methods found | " + " | ".join(f"{k} P/R" for k in kinds) + " | dead found | live reported | vault | plugin live | read with |",
             "|" + "---|" * (13 + len(kinds))]
    for r in sorted(results, key=lambda r: (r["language"], list(PROFILES).index(r["profile"]), r.get("seed", 1), r.get("dialect", ""), r.get("toolchain") or "")):
        if not r.get("ok"):
            lines.append(f"| {r['language']} | {profile_cell(r)} | | | | | | **failed**: {r.get('error', '')[:120].replace('|', '/').replace(chr(10), ' ')} |" + " |" * (len(kinds) + 5))
            continue
        s, e = r["stats"], r["edges"]
        cells = [f"{pct(e[k]['precision'])}/{pct(e[k]['recall'])}" if k in e else "–" for k in kinds]
        d = r["dead_code"]
        lines.append(f"| {r['language']} | {profile_cell(r)} | {s['methods']} | {s['calls']} | {s['files']} | {s['lines']} | "
                     f"{r['index_seconds']} | {r['methods']['found']}/{r['methods']['expected']} | " + " | ".join(cells) + f" | {d['found']}/{d['planted']} | {d['live_reported']} | "
                     f"{vault_cell(r.get('vault'))} | {live_cell(r.get('live'))} | {compiler_cell(r)} |")
    findings = []
    for r in results:
        if not r.get("ok"):
            continue
        for k, v in r["edges"].items():
            if v["missing"] or v["extra"]:
                findings.append(f"- {r['language']}/{r['profile']} {k}: missing {v['expected'] - v['matched']}, extra {v['recorded'] - v['matched']}; "
                                f"e.g. missing {v['missing'][:2]} extra {v['extra'][:2]}")
        if r["dead_code"]["live_reported"]:
            findings.append(f"- {r['language']}/{r['profile']} dead code: {r['dead_code']['live_reported']} live methods reported, e.g. {r['dead_code']['live_reported_sample'][:3]}")
        if r["dead_code"]["missed"]:
            findings.append(f"- {r['language']}/{r['profile']} dead code: planted methods not reported, e.g. {r['dead_code']['missed'][:3]}")
        for part in ("vault", "live"):
            if r.get(part) and not r[part].get("ok"):
                findings.append(f"- {r['language']}/{r['profile']} {part}: {', '.join(r[part].get('problems') or [r[part].get('error', '')[:200]])}")
        if r["methods"]["missing"]:
            findings.append(f"- {r['language']}/{r['profile']} methods: {r['methods']['expected'] - r['methods']['found']} not in the graph, e.g. {r['methods']['missing'][:3]}")
    text = "# Corpus results\n\nP/R is precision/recall in percent.\n\n" + "\n".join(lines) + "\n\n## Differences\n\n" + ("\n".join(findings) or "None.") + "\n"
    open(os.path.join(out, "summary.md"), "w").write(text)
    json.dump(results, open(os.path.join(out, "summary.json"), "w"), indent=1)
    return text


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--langs", default=",".join(LANGS))
    ap.add_argument("--profiles", default=",".join(PROFILES))
    ap.add_argument("--seeds", default="1")
    ap.add_argument("--dialects", default="modern", help="comma separated: modern, classic (see gen.py LEVELS)")
    ap.add_argument("--level", help="pin every run's language level to this (net8.0, 17, ES2015, 1.21, 2018, >=3.8); default per dialect")
    ap.add_argument("--tag", help="a suffix for result names, such as the toolchain the runs are on")
    ap.add_argument("--no-live", action="store_true", help="skip the MCP live checks")
    ap.add_argument("--jobs", type=int, default=max(1, (os.cpu_count() or 2) // 2))
    ap.add_argument("--cpus", default="2", help="CPUs per container")
    ap.add_argument("--memory", default="4g", help="memory per container")
    ap.add_argument("--base", help="local base image with the .NET 10 SDK, Node and Python; mounts host Java tools")
    ap.add_argument("--results", default=os.path.join(REPO, "artifacts", "corpus"))
    ap.add_argument("--no-build", action="store_true")
    a = ap.parse_args()
    results = os.path.abspath(a.results)
    publish = os.path.join(results, ".publish")
    os.makedirs(results, exist_ok=True)
    if not a.no_build:
        build(a.base, publish)
    mounts = host_tools() if a.base else {}
    network = "none" if a.base else "bridge"
    langs = a.langs.split(",")
    if "python" in langs and a.base and not shutil.which("scip-python"):
        print("skipping python: scip-python is not installed (npm install -g @sourcegraph/scip-python)", flush=True)
        langs.remove("python")
    dialects = [d for d in a.dialects.split(",") if d in DIALECTS] or ["modern"]
    jobs = [(l, p, int(s), d) for l in langs for p in a.profiles.split(",") for s in a.seeds.split(",") for d in dialects if (l, p) not in UNSUPPORTED]
    # The largest first, so the long runs overlap the short ones.
    jobs.sort(key=lambda j: -PROFILES[j[1]]["modules"] * PROFILES[j[1]]["classes"])
    print(f"{len(jobs)} runs, {a.jobs} at a time ({a.cpus} CPUs, {a.memory} each), network {network}", flush=True)
    started = time.monotonic()
    with cf.ThreadPoolExecutor(a.jobs) as pool:
        done = list(pool.map(lambda j: run_one(j, publish, results, mounts, a.cpus, a.memory, network, a.level, a.tag, a.no_live), jobs))
    text = summarise(done, results)
    print(f"\n{len(done)} runs in {time.monotonic() - started:.0f}s; {sum(1 for r in done if not r.get('ok'))} failed → {results}/summary.md\n")
    print(text)
    return 0 if all(r.get("ok") for r in done) else 1


if __name__ == "__main__":
    sys.exit(main())
