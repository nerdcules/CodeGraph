#!/usr/bin/env python3
"""
The Obsidian plugin's live path on a generated codebase: live_check.cjs drives the real codegraph-mcp through the
plugin's MCP client, and this scores what it saw. Every row must parse and link to a note; callers are compared
with the answer key; a source edit must turn the answers stale and a re-index fresh again, after which the export
rewrites almost nothing. Standard library only; called from check.py.
"""
from __future__ import annotations

import difflib
import json
import os
import re

SAMPLES = 6
TARGETS = 15
HERE = os.path.dirname(os.path.abspath(__file__))
COMMENT = {"python": "#"}


def pick_targets(key: dict, nodes: dict, normalise) -> list[dict]:
    """Callees from the answer key whose name maps to exactly one method in the graph, spread over the key."""
    by_name: dict[str, list[str]] = {}
    for i, n in nodes.items():
        if n["kind"] == "Method":
            by_name.setdefault(normalise(n), []).append(i)
    callees = sorted(k for k in key["callers"] if len(by_name.get(k, [])) == 1)
    step = max(1, len(callees) // TARGETS)
    # The search names the owner too: in the large and collision profiles a bare method name repeats in every
    # module, and the one wanted can fall past the first page of results.
    return [dict(id=by_name[k][0], name=query_for(nodes[by_name[k][0]]), key=k) for k in callees[::step][:TARGETS]]


def query_for(node: dict) -> str:
    """Owner.member from the full name, the way a person narrows a search; the bare name when there is no owner."""
    full = re.sub(r"\([^()]*\)$", "", node["fullName"])
    parts = [p for p in re.split(r"[.:/]", full) if p]
    if len(parts) >= 2:
        # Owner.member, the member as the full name spells it (a Java constructor is <init> there, "constructor" by name).
        query = f"{parts[-2]}.{parts[-1]}"
        if query in node["fullName"]:  # find_symbol matches substrings of the name or full name
            return query
    return node["name"]


def node_cmd(run, plugin_lib, mcp, db, corpus, manifest, mode, targets_file, out, log):
    code, seconds, output = run(["node", os.path.join(HERE, "live_check.cjs"), "--lib", plugin_lib, "--mcp", json.dumps(mcp),
                                 "--db", db, "--cwd", corpus, "--manifest", manifest, "--mode", mode, "--in", targets_file,
                                 "--out", out], corpus, log)
    seen = json.load(open(out)) if os.path.exists(out) else dict(error=output[-500:])
    return code, seconds, seen


def check_live(run, indexer: list[str], mcp: list[str], plugin_lib: str, corpus: str, db: str, key: dict,
               nodes: dict, normalise, log) -> dict:
    from vault_check import export  # noqa: E402  (same folder)

    result = dict(ok=False)
    out_dir = os.path.join(corpus, "vault", "_code")
    manifest = os.path.join(out_dir, ".codegraph-export.json")
    if not os.path.exists(manifest):
        result["error"] = "no export to link rows to"
        return result
    targets = pick_targets(key, nodes, normalise)
    targets_file = os.path.join(corpus, "live-targets.json")
    json.dump(targets, open(targets_file, "w"))
    seen_file = os.path.join(corpus, "live-seen.json")

    code, seconds, seen = node_cmd(run, plugin_lib, mcp, db, corpus, manifest, "queries", targets_file, seen_file, log)
    if code != 0 or "error" in seen:
        result["error"] = "live queries failed: " + str(seen.get("error", ""))[-500:]
        return result

    found = unparsed = unlinked = stale_rows = 0
    expected_pairs, actual_pairs, samples = set(), set(), []
    for t in targets:
        entry = seen["targets"].get(t["id"], {})
        found += bool(entry.get("found"))
        for tab in ("find", "callers", "tests", "impact"):
            d = entry.get(tab, {})
            unparsed += d.get("unparsed", 0)
            unlinked += d.get("unlinked", 0)
            stale_rows += bool(d.get("stale"))
            if d.get("unparsed_sample") or d.get("unlinked_sample"):
                samples.append(f"{t['key']} {tab}: {d.get('unparsed_sample') or d.get('unlinked_sample')}")
        callers = {normalise(nodes[i]) for i in entry.get("callers", {}).get("ids", []) if i in nodes}
        expected_pairs |= {(t["key"], c) for c in key["callers"][t["key"]]}
        actual_pairs |= {(t["key"], c) for c in callers}
    matched = expected_pairs & actual_pairs
    result.update(
        targets=len(targets), found_by_name=found, unparsed_rows=unparsed, unlinked_rows=unlinked, problem_sample=samples[:SAMPLES],
        fresh_answers_marked_stale=stale_rows + bool(seen.get("status_stale")),
        callers=dict(expected=len(expected_pairs), recorded=len(actual_pairs), matched=len(matched),
                     precision=round(len(matched) / len(actual_pairs), 4) if actual_pairs else 1.0,
                     recall=round(len(matched) / len(expected_pairs), 4) if expected_pairs else 1.0,
                     missing=sorted(map(list, expected_pairs - actual_pairs))[:SAMPLES],
                     extra=sorted(map(list, actual_pairs - expected_pairs))[:SAMPLES]),
        calls=seen.get("calls"), first_call_ms=seen.get("first_call_ms"), median_ms=seen.get("median_ms"), max_ms=seen.get("max_ms"))

    # A source edit makes the answers stale; a re-index makes them fresh, and the export then has almost nothing to do.
    edited = nodes[targets[0]["id"]].get("file") if targets else None
    before = {os.path.relpath(os.path.join(d, f), out_dir): open(os.path.join(d, f), encoding="utf-8").read()
              for d, _, fs in os.walk(out_dir) for f in fs if f.endswith(".md")}
    if edited and os.path.isfile(edited):
        with open(edited, "a") as f:
            f.write(f"\n{COMMENT.get(key['language'], '//')} edited by the live check\n")
        _, _, after_edit = node_cmd(run, plugin_lib, mcp, db, corpus, manifest, "status", targets_file, seen_file, log)
        result["stale_after_edit"] = bool(after_edit.get("status_stale")) and bool((after_edit.get("callers") or {}).get("stale"))
        code, _, _ = run(indexer + [os.path.join(corpus, key["target"]), "--db", db], corpus, log)
        _, _, after_index = node_cmd(run, plugin_lib, mcp, db, corpus, manifest, "status", targets_file, seen_file, log)
        result["fresh_after_reindex"] = code == 0 and not after_index.get("status_stale") and not (after_index.get("callers") or {}).get("stale")
        _, _, summary, _ = export(run, indexer, db, out_dir, corpus, log)
        result["export_after_reindex"] = summary
        changed = []
        for rel, old in sorted(before.items()):
            path = os.path.join(out_dir, rel)
            new = open(path, encoding="utf-8").read() if os.path.exists(path) else ""
            if new != old and rel != "Index.md":
                diff = [l for l in difflib.unified_diff(old.splitlines(), new.splitlines(), lineterm="", n=0) if l[:1] in "+-" and l[:3] not in ("+++", "---")]
                changed.append(dict(note=rel, diff=diff[:6]))
        result["notes_changed_by_edit"] = changed[:SAMPLES]
    else:
        result["stale_after_edit"] = result["fresh_after_reindex"] = None

    _, _, failure = node_cmd(run, plugin_lib, mcp, db, corpus, manifest, "failure", targets_file, seen_file, log)
    result["failure_modes"] = {k: v for k, v in failure.items() if k != "mode"}
    reason_shown = failure.get("server exits at start", {}).get("message", "")

    problems = []
    if not targets:
        problems.append("no targets")
    if found < len(targets):
        problems.append("find_symbol missed targets")
    if unparsed or unlinked:
        problems.append("rows unparsed or unlinked")
    if result["fresh_answers_marked_stale"]:
        problems.append("fresh graph reported stale")
    if result["stale_after_edit"] is False:
        problems.append("edit not reported stale")
    if result["fresh_after_reindex"] is False:
        problems.append("still stale after re-index")
    s = result.get("export_after_reindex")
    if s is not None and (s["written"] > 1 + len([c for c in result.get("notes_changed_by_edit", []) if c["note"].startswith("Reports")]) or s["deleted"]):
        problems.append("export after a comment-only edit rewrote notes")
    if not all(v.get("failed") for v in result["failure_modes"].values()) or "install or update .NET" not in reason_shown:
        problems.append("a server that cannot start is not reported with its reason")
    result["problems"] = problems
    result["ok"] = not problems
    return result
