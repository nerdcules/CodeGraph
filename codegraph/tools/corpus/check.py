#!/usr/bin/env python3
"""
Index a generated codebase with codegraph-index, export the graph and score it against expected.json.

    python3 check.py --corpus /tmp/corpus --indexer "dotnet /opt/codegraph/index/codegraph-index.dll" --out result.json

Scores, per edge kind, the precision (how many recorded edges are in the answer key) and recall (how many answer
key edges were recorded), plus methods found, and dead code: planted dead methods reported, and live methods
wrongly reported. Standard library only.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shlex
import subprocess
import sys
import time

SAMPLES = 6
GENERIC = re.compile(r"<(?!init>|template>)[^<>]*>")


def arity(signature: str | None) -> int:
    """Parameters in a signature's parameter list, counted at nesting depth zero. Go's receiver group
    (func (*T).Name(...)) is skipped, and so is a leading self (Rust, Python)."""
    if not signature or "(" not in signature:
        return 0
    i = signature.index("(")
    if signature.startswith("func ("):  # Go method: the receiver comes first
        i = signature.index("(", signature.index(")") + 1)
    i += 1
    depth, params, current = 0, [], ""
    while i < len(signature):
        ch = signature[i]
        if ch in "<([{":
            depth += 1
        elif ch in ">)]}":
            if depth == 0:
                break
            depth -= 1
        if ch == "," and depth == 0:
            params.append(current.strip())
            current = ""
        else:
            current += ch
        i += 1
    if current.strip():
        params.append(current.strip())
    if params and re.fullmatch(r"(&\s*('\w+\s+)?(mut\s+)?)?(mut\s+)?self(\s*:.*)?|cls", params[0]):
        params = params[1:]
    return len(params)


def normalise(node: dict) -> str:
    """The answer key's name for a node: generics and parameter lists dropped, constructors as <init>."""
    fq = node["fullName"]
    if node["kind"] == "Method":
        fq = re.sub(r"\([^()]*\)$", "", fq)  # C# full names end in their parameter types
    while True:
        stripped = GENERIC.sub("", fq)
        if stripped == fq:
            break
        fq = stripped
    if node["kind"] != "Method":
        return fq
    if node.get("isTest") and " > " in fq:
        return fq.replace(" > ", ".") + "/0"
    if node.get("memberKind") == "Constructor":
        fq = fq[: fq.rfind(".") + 1] + "<init>"
    return f"{fq}/{0 if node.get('isTest') else arity(node.get('signature'))}"


def score(expected: set, actual: set) -> dict:
    tp = expected & actual
    return dict(expected=len(expected), recorded=len(actual), matched=len(tp),
                precision=round(len(tp) / len(actual), 4) if actual else (1.0 if not expected else 0.0),
                recall=round(len(tp) / len(expected), 4) if expected else 1.0,
                missing=sorted(map(list, expected - actual))[:SAMPLES], extra=sorted(map(list, actual - expected))[:SAMPLES])


def run(cmd: list[str], cwd: str, log) -> tuple[int, float, str]:
    started = time.monotonic()
    p = subprocess.run(cmd, cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    log.write(f"$ {shlex.join(cmd)}\n{p.stdout}\n")
    return p.returncode, time.monotonic() - started, p.stdout


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--corpus", required=True)
    ap.add_argument("--indexer", default="codegraph-index", help="command that runs codegraph-index")
    ap.add_argument("--out", required=True)
    ap.add_argument("--no-vault", action="store_true", help="skip the Obsidian export checks")
    ap.add_argument("--mcp", help="command that runs codegraph-mcp; with --plugin-lib, checks the plugin's live path")
    ap.add_argument("--plugin-lib", help="the Obsidian plugin's lib/ folder")
    a = ap.parse_args()
    corpus = os.path.abspath(a.corpus)
    key = json.load(open(os.path.join(corpus, "expected.json")))
    indexer = shlex.split(a.indexer)
    db = os.path.join(corpus, ".codegraph", "graph.db")
    result = dict(language=key["language"], profile=key["profile"], stats=key["stats"], ok=False)
    with open(a.out + ".log", "w") as log:
        if key["language"] == "csharp":
            # As a developer's checkout is: restored, so the target framework's reference pack is resolved. Offline this
            # succeeds when the SDK ships the pack (the corpus has no package dependencies); otherwise the indexer says so.
            run(["dotnet", "restore", os.path.join(corpus, key["target"]), "--nologo", "-v", "q"], corpus, log)
        code, seconds, output = run(indexer + [os.path.join(corpus, key["target"]), "--db", db], corpus, log)
        result["index_seconds"] = round(seconds, 2)
        if code != 0 or not os.path.exists(db):
            result["error"] = "index failed: " + "\n".join(output.strip().splitlines()[-8:])
            json.dump(result, open(a.out, "w"), indent=1)
            print(f"{key['language']}/{key['profile']}: index failed", file=sys.stderr)
            return 1
        export = os.path.join(corpus, "graph.ndjson")
        code, _, output = run(indexer + ["export", "--db", db, "--dead-code", "--out", export], corpus, log)
        if code != 0:
            result["error"] = "export failed: " + output[-500:]
            json.dump(result, open(a.out, "w"), indent=1)
            return 1

    nodes, edges, dead, facts = {}, [], set(), {}
    for line in open(export):
        r = json.loads(line)
        if r["type"] == "node":
            nodes[r["id"]] = r
        elif r["type"] == "edge":
            edges.append(r)
        elif r["type"] == "dead":
            dead.add(r["node"])
        elif r["type"] == "meta":
            facts[f"{r['language']}.{r['key']}"] = r["value"]
    # What the graph says it was read with (compiler, language_versions, compile_errors, unmapped, notes), so a
    # run on an older toolchain shows which compiler and language level it exercised.
    result["compiler"] = {k: v for k, v in facts.items() if k.split(".", 1)[1] in ("compiler", "language_versions", "compile_errors", "language_version_errors", "unmapped", "notes", "first_compile_error", "first_language_version_error")}
    result["dialect"], result["level"] = key.get("dialect", "modern"), key.get("level")
    name = {i: normalise(n) for i, n in nodes.items()}
    methods = set(key["methods"])
    types = set(key["types"])
    known = methods | types | {f"{t}.<template>/0" for t in types}
    is_method = lambda i: i in nodes and nodes[i]["kind"] == "Method"
    is_type = lambda i: i in nodes and nodes[i]["kind"] == "Type"

    actual = {k: set() for k in key["edges"]}
    unmapped_edges = 0
    for e in edges:
        f, t, kind = e["from"], e["to"], e["kind"]
        if f not in name or t not in name:
            continue
        pair = (name[f], name[t])
        if kind == "Calls" and is_method(f) and is_method(t):
            target = "TemplateCalls" if pair[0].endswith(".<template>/0") else "Calls"
        elif kind in ("Overrides", "TestedBy") and is_method(f) and is_method(t):
            target = kind
        elif kind == "Implements" and is_method(f) and is_method(t):
            target = "Implements"
        elif kind in ("Inherits", "Implements") and is_type(f) and is_type(t):
            target = "Supertype"
        elif kind in ("Injects", "Renders"):
            target = kind
        else:
            continue
        if pair[0] in known and pair[1] in known:
            actual[target].add(pair)
        else:
            unmapped_edges += 1

    expected = {k: {tuple(p) for p in v} for k, v in key["edges"].items()}
    result["edges"] = {k: score(expected[k], actual[k]) for k in expected if expected[k] or actual[k]}
    found = {name[i] for i in nodes if is_method(i)}
    result["methods"] = dict(expected=len(methods), found=len(methods & found), missing=sorted(methods - found)[:SAMPLES],
                             unmapped=len([1 for i in nodes if is_method(i) and name[i] not in methods]),
                             unmapped_sample=sorted({name[i] for i in nodes if is_method(i) and name[i] not in methods})[:SAMPLES])
    result["unmapped_edges"] = unmapped_edges

    # Dead code: planted methods must be reported; a method with a caller, a test, an entry point the language's
    # extractor knows, a lifecycle hook, or one overriding or implementing a called method must not be.
    reported = {name[i] for i in dead if i in name}
    planted = {k for k, v in key["methods"].items() if v["dead"]}
    called = set(key["callers"])
    up: dict[str, set] = {}
    for kind in ("Overrides", "Implements"):
        for a_, b_ in expected[kind]:
            up.setdefault(a_, set()).add(b_)
    def reaches_called(k, seen=()):
        return k in called or any(reaches_called(b, seen + (k,)) for b in up.get(k, ()) if b not in seen)
    live = {k for k, v in key["methods"].items()
            if v["test"] or v.get("lifecycle") or (v["entry"] and key["entry_is_framework"]) or reaches_called(k)}
    result["dead_code"] = dict(planted=len(planted), found=len(planted & reported),
                               recall=round(len(planted & reported) / len(planted), 4) if planted else 1.0,
                               missed=sorted(planted - reported)[:SAMPLES],
                               live_reported=len(reported & live), live_reported_sample=sorted(reported & live)[:SAMPLES])
    result["graph"] = dict(nodes=len(nodes), edges=len(edges), db_mb=round(os.path.getsize(db) / 1e6, 2))
    if not a.no_vault:
        from vault_check import check_vault
        with open(a.out + ".log", "a") as log:
            result["vault"] = check_vault(run, indexer, corpus, db, nodes, edges, log)
            if a.mcp and a.plugin_lib and result["vault"].get("first_run"):
                from live import check_live
                result["live"] = check_live(run, indexer, shlex.split(a.mcp), a.plugin_lib, corpus, db, key, nodes, normalise, log)
    result["ok"] = True
    json.dump(result, open(a.out, "w"), indent=1)
    worst = min((v["recall"] for v in result["edges"].values()), default=1.0)
    compiler = "; ".join(f"{k.split('.', 1)[1]}={v}" for k, v in sorted(result["compiler"].items()) if k.endswith((".compiler", ".language_versions")) or (v not in ("0", "") and k.endswith((".compile_errors", ".unmapped", ".notes"))))
    print(f"{key['language']}/{key['profile']} [{result['dialect']}{', ' + result['level'] if result['level'] else ''}] read with {compiler or 'no compiler facts'}")
    print(f"{key['language']}/{key['profile']}: {result['methods']['found']}/{len(methods)} methods, "
          f"calls P={result['edges']['Calls']['precision']:.3f} R={result['edges']['Calls']['recall']:.3f}, worst recall {worst:.3f}, "
          f"dead {result['dead_code']['found']}/{len(planted)} (+{result['dead_code']['live_reported']} live), {result['index_seconds']}s"
          + (f", vault {'ok' if result['vault']['ok'] else 'FAIL ' + ','.join(result['vault'].get('problems', [result['vault'].get('error', '')]))}" if "vault" in result else "")
          + (f", live {'ok' if result['live']['ok'] else 'FAIL ' + ','.join(result['live'].get('problems', [result['live'].get('error', '')]))}" if "live" in result else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())
