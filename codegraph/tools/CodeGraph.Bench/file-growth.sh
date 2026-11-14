#!/bin/bash
# How the graph file grows under incremental passes (BENCHMARKS.md, round 14). Run it on a copy of this repo:
# it edits src/CodeGraph.Core/Worktree.cs and puts it back at the end.
#
#   file-growth.sh cli    <codegraph-index> <repo copy> [no-op passes] [edit passes]   (default 8 and 30)
#   file-growth.sh daemon <codegraph-index> <repo copy> [saves]                        (default 150)
#
# cli: a full index, no-op passes, then passes that each change one method; the file size after each.
# daemon: a full index, then --watch and saves 3 s apart; the file size every 10 saves.
set -u
MODE=$1; IDX=$2; REPO=$3
SLN=$REPO/codegraph/CodeGraph.slnx
DB=$REPO/.codegraph/graph.db
F=$REPO/codegraph/src/CodeGraph.Core/Worktree.cs
LOG=$(mktemp)
cp "$F" "$F.orig"
trap 'cp "$F.orig" "$F"; rm -f "$F.orig" "$LOG"' EXIT
size() { stat -c %s "$DB" | awk '{printf "%.1f MB", $1/1048576}'; }
probe() { cp "$F.orig" "$F"; sed -i "0,/^}/s//    internal static int GrowthProbe$1() => $1;\n}/" "$F"; }

"$IDX" "$SLN" --full >/dev/null 2>&1; echo "full index  $(size)"
if [ "$MODE" = cli ]; then
  for i in $(seq 1 "${4:-8}"); do "$IDX" "$SLN" >/dev/null 2>&1; echo "no-op $i  $(size)"; done
  for i in $(seq 1 "${5:-30}"); do
    probe "$i"; "$IDX" "$SLN" >"$LOG" 2>&1
    echo "edit $i  $(size)  ($(grep -o 'walking [0-9]* of [0-9]* files' "$LOG"))"
  done
else
  "$IDX" "$SLN" --watch >"$LOG" 2>&1 &
  PID=$!
  for _ in $(seq 1 60); do grep -q "watching" "$LOG" && break; sleep 1; done
  sleep 3; echo "daemon up  $(size)"
  for i in $(seq 1 "${4:-150}"); do
    probe "$i"; sleep 3
    if [ $((i % 10)) -eq 0 ]; then echo "save $i  $(size)"; fi
  done
  kill "$PID"; wait "$PID" 2>/dev/null
  echo "passes: $(grep -c 'indexed .* nodes' "$LOG")"
  grep -o 'walked) in [0-9.]*s' "$LOG" | grep -o '[0-9.]*' | sort -n | awk '{a[NR]=$1} END {print "pass time median " a[int((NR+1)/2)] " s, range " a[1] "-" a[NR] " s"}'
fi
