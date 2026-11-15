#!/bin/bash
# 3 rounds x 4 assistants x {on, off}; odd rounds run "on" first, even rounds "off" first.
cd "$(dirname "$0")"
for n in 1 2 3; do
  for a in claude codex copilot cursor; do
    if [ $((n % 2)) -eq 1 ]; then order="on off"; else order="off on"; fi
    for c in $order; do python3 run.py "$a" "$c" "$n"; done
  done
done
echo ALL DONE
