#!/usr/bin/env python3
"""
Checks `codegraph-index export obsidian` on a generated codebase: every link resolves to a note and heading that
exist, every type and member in the graph has a place, frontmatter parses, source links point at real lines, a
second run writes nothing, and the folder's lifecycle (emptied, rebuilt, with a person's notes in it) never loses
a note it did not write. Standard library only; called from check.py.
"""
from __future__ import annotations

import json
import os
import re
import shutil
import time
import urllib.parse
import urllib.request

SAMPLES = 6
WIKILINK = re.compile(r"\[\[([^\[\]]+?)\]\]")
SOURCE_LINK = re.compile(r"\]\((file://[^)\s]+)\)")
HEADING = re.compile(r"^(#{1,6}) (.+?)\s*$")
SUMMARY = re.compile(r"obsidian: (\d+) written, (\d+) unchanged, (\d+) deleted")
MARKER = "codegraph: generated"
MANIFEST = ".codegraph-export.json"
MEMBER_KINDS = ("Method", "Property", "Field", "Event")


def parse_note(path: str) -> dict:
    """Frontmatter (as key → raw value), headings, wikilinks and source links of one note."""
    text = open(path, encoding="utf-8").read()
    lines = text.split("\n")
    frontmatter, body_start = {}, 0
    if lines and lines[0] == "---":
        for i in range(1, len(lines)):
            if lines[i] == "---":
                body_start = i + 1
                break
            key, sep, value = lines[i].partition(": ")
            frontmatter[key] = value if sep else None
    headings, links, sources, fenced = set(), [], [], False
    for line in lines[body_start:]:
        if line.startswith("```"):
            fenced = not fenced
            continue
        if fenced:
            continue
        m = HEADING.match(line)
        if m:
            headings.add(m.group(2))
        for inner in WIKILINK.findall(line):
            # Inside a table cell the label separator is escaped as \|.
            target = re.split(r"\\?\|", inner, maxsplit=1)[0]
            links.append(target)
        sources.extend(SOURCE_LINK.findall(line))
    return dict(text=text, frontmatter=frontmatter, headings=headings, links=links, sources=sources)


def frontmatter_problems(note: dict) -> list[str]:
    fm = note["frontmatter"]
    problems = []
    if not note["text"].startswith("---\n" + MARKER + "\n"):
        problems.append("no codegraph marker on the first frontmatter line")
    for key, value in fm.items():
        if key == MARKER.split(":")[0]:
            continue
        if value is None:
            problems.append(f"{key}: not a key: value line")
            continue
        try:
            json.loads(value)  # every value is written as JSON, which is valid YAML
        except ValueError:
            problems.append(f"{key}: {value[:60]!r} is not a JSON scalar or list")
    return problems


def scan(vault: str, folder: str) -> dict:
    """All notes under the export folder, keyed by vault-relative path without .md."""
    notes = {}
    root = os.path.join(vault, folder)
    for dirpath, _, files in os.walk(root):
        for f in files:
            if f.endswith(".md"):
                full = os.path.join(dirpath, f)
                rel = os.path.relpath(full, vault).replace(os.sep, "/")[:-3]
                notes[rel] = parse_note(full)
    return notes


def resolve_links(notes: dict) -> dict:
    broken, missing_heading, total = [], [], 0
    for path, note in notes.items():
        for target in note["links"]:
            total += 1
            file, _, heading = target.partition("#")
            if file not in notes:
                broken.append(f"{path} → {target}")
            elif heading and heading not in notes[file]["headings"]:
                missing_heading.append(f"{path} → {target}")
    return dict(links=total, broken=len(broken), broken_sample=broken[:SAMPLES],
                missing_heading=len(missing_heading), missing_heading_sample=missing_heading[:SAMPLES])


def check_sources(notes: dict) -> dict:
    bad, total, line_counts = [], 0, {}
    for path, note in notes.items():
        for url in note["sources"]:
            total += 1
            parsed = urllib.parse.urlparse(url)
            file = urllib.request.url2pathname(urllib.parse.unquote(parsed.path))
            line = int(parsed.fragment[1:]) if parsed.fragment.startswith("L") and parsed.fragment[1:].isdigit() else 0
            if file not in line_counts:
                line_counts[file] = sum(1 for _ in open(file, encoding="utf-8", errors="replace")) if os.path.isfile(file) else -1
            if line_counts[file] < 0 or not 1 <= line <= max(line_counts[file], 1):
                bad.append(f"{path}: {url}")
    return dict(source_links=total, bad=len(bad), bad_sample=bad[:SAMPLES])


def coverage(nodes: dict, edges: list, manifest: dict, notes: dict, folder: str) -> dict:
    """Every type, and every member a type or file contains, has a note or a heading in one; every manifest entry
    points at a note and heading that exist."""
    parent = {}
    for e in edges:
        if e["kind"] == "Contains" and e["to"] not in parent:
            parent[e["to"]] = e["from"]
    refs = manifest.get("nodes", {})
    types = [i for i, n in nodes.items() if n["kind"] == "Type"]
    def owner(i):  # the nearest type or file above a member, through any members in between
        for _ in range(32):
            up = parent.get(i)
            if up not in nodes:
                return None
            if nodes[up]["kind"] not in MEMBER_KINDS:
                return nodes[up]["kind"]
            i = up
        return None
    members = [i for i, n in nodes.items() if n["kind"] in MEMBER_KINDS and owner(i) in ("Type", "File")]
    no_type_note = [i for i in types if i not in refs or refs[i].get("heading")]
    no_member_heading = [i for i in members if i not in refs or not refs[i].get("heading")]
    dangling = []
    for i, ref in refs.items():
        note = f"{folder}/{ref['path'][:-3]}"
        if note not in notes or (ref.get("heading") and ref["heading"] not in notes[note]["headings"]):
            dangling.append(f"{i} → {ref}")
    return dict(types=len(types), types_without_note=len(no_type_note), types_without_note_sample=no_type_note[:SAMPLES],
                members=len(members), members_without_heading=len(no_member_heading),
                members_without_heading_sample=no_member_heading[:SAMPLES],
                manifest_entries=len(refs), dangling=len(dangling), dangling_sample=dangling[:SAMPLES])


def export(run, indexer: list[str], db: str, out: str, cwd: str, log, extra: list[str] = ()) -> tuple[int, float, dict | None, str]:
    code, seconds, output = run(indexer + ["export", "obsidian", "--db", db, "--out", out, *extra], cwd, log)
    m = SUMMARY.search(output)
    summary = dict(written=int(m.group(1)), unchanged=int(m.group(2)), deleted=int(m.group(3))) if m else None
    return code, seconds, summary, output


def check_vault(run, indexer: list[str], corpus: str, db: str, nodes: dict, edges: list, log) -> dict:
    vault = os.path.join(corpus, "vault")
    folder = "_code"
    out = os.path.join(vault, folder)
    shutil.rmtree(vault, ignore_errors=True)
    os.makedirs(vault)
    result = dict(ok=False)

    code, seconds, first, output = export(run, indexer, db, out, corpus, log)
    result["export_seconds"] = round(seconds, 2)
    if code != 0 or first is None:
        result["error"] = "export obsidian failed: " + output[-500:]
        return result
    result["first_run"] = first

    notes = scan(vault, folder)
    manifest = json.load(open(os.path.join(out, MANIFEST)))
    result["notes"] = len(notes)
    result["links"] = resolve_links(notes)
    result["sources"] = check_sources(notes)
    fm = {p: frontmatter_problems(n) for p, n in notes.items()}
    fm = {p: v for p, v in fm.items() if v}
    result["frontmatter"] = dict(bad_notes=len(fm), sample=[f"{p}: {v[0]}" for p, v in list(fm.items())[:SAMPLES]])
    result["coverage"] = coverage(nodes, edges, manifest, notes, folder)
    listed = set(manifest.get("files", []))
    on_disk = {p[len(folder) + 1:] + ".md" for p in notes}
    result["manifest_files_match"] = listed == on_disk

    # Idempotence: nothing changed, so nothing is written.
    _, _, second, _ = export(run, indexer, db, out, corpus, log)
    result["second_run"] = second

    # Lifecycle: a person's note and a hand-edited generated note survive an export that empties the folder (a
    # language with no nodes); a full export then restores every note and still leaves the person's note alone.
    mine = os.path.join(out, "Types", "My notes.md")
    os.makedirs(os.path.dirname(mine), exist_ok=True)
    open(mine, "w").write("# My notes\n\nWritten by a person.\n")
    edited_rel = next((p for p in sorted(manifest.get("files", [])) if p.startswith("Types/")), None)
    edited = os.path.join(out, *edited_rel.split("/")) if edited_rel else None
    if edited:
        open(edited, "w").write("# Rewritten by hand, marker removed\n")
    _, _, emptied, _ = export(run, indexer, db, out, corpus, log, ["--language", "no-such-language"])
    remaining = {os.path.relpath(os.path.join(d, f), out).replace(os.sep, "/") for d, _, fs in os.walk(out) for f in fs if f.endswith(".md")}
    # The emptied export still writes its index and (empty) reports.
    expected_left = {"Types/My notes.md"} | ({edited_rel} if edited_rel else set()) | set(json.load(open(os.path.join(out, MANIFEST))).get("files", []))
    _, _, rebuilt, _ = export(run, indexer, db, out, corpus, log)
    restored = scan(vault, folder)
    result["lifecycle"] = dict(
        emptied=emptied, left_after_emptying=sorted(remaining)[:SAMPLES],
        kept_unmarked=remaining == expected_left,
        rebuilt=rebuilt, restored_all=set(notes) <= set(restored),
        person_note_kept=os.path.exists(mine) and open(mine).read().startswith("# My notes"))

    problems = []
    if result["links"]["broken"] or result["links"]["missing_heading"]:
        problems.append("broken links")
    if result["sources"]["bad"]:
        problems.append("bad source links")
    if result["frontmatter"]["bad_notes"]:
        problems.append("bad frontmatter")
    c = result["coverage"]
    if c["types_without_note"] or c["members_without_heading"] or c["dangling"]:
        problems.append("coverage")
    if not result["manifest_files_match"]:
        problems.append("manifest files differ from disk")
    if not second or second["written"] or second["deleted"]:
        problems.append("second run not a no-op")
    lc = result["lifecycle"]
    if not (lc["kept_unmarked"] and lc["restored_all"] and lc["person_note_kept"]):
        problems.append("lifecycle")
    result["problems"] = problems
    result["ok"] = not problems
    return result


if __name__ == "__main__":
    # Standalone: python3 vault_check.py <vault> → link and frontmatter checks of an existing export.
    import sys
    v = os.path.abspath(sys.argv[1])
    found = scan(v, sys.argv[2] if len(sys.argv) > 2 else "_code")
    print(json.dumps(dict(notes=len(found), links=resolve_links(found), sources=check_sources(found)), indent=1))
