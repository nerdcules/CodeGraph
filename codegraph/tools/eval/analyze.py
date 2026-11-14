"""Summarise the eval logs: one row per run, then medians per assistant and condition.
analyze.py [--answers] prints each run's final answer as well, for checking the extracted test counts by hand."""
import glob, json, os, re, statistics, sys

E = os.path.dirname(os.path.abspath(__file__))
TRUTH_TESTS = 44


def lines(path):
    for line in open(path, errors="replace"):
        try:
            yield json.loads(line)
        except ValueError:
            continue


def claude(path):
    usage, tools, answer, cost = {}, [], "", None
    for m in lines(path):
        if m.get("type") == "assistant":
            msg = m["message"]
            usage[msg.get("id")] = msg.get("usage", {})
            tools += [c["name"] for c in msg.get("content", []) if c.get("type") == "tool_use"]
        if m.get("type") == "result":
            answer, cost = m.get("result", ""), m.get("total_cost_usd")
    tokens = sum(u.get("input_tokens", 0) + u.get("cache_read_input_tokens", 0) + u.get("cache_creation_input_tokens", 0)
                 for u in usage.values())
    return dict(model_calls=len(usage), tools=tools, input_tokens=tokens, cost_usd=cost, answer=answer)


def codex(path):
    tools, answer, tokens = [], "", None
    for m in lines(path):
        item = m.get("item") or {}
        if m.get("type") == "item.completed":
            if item.get("type") == "agent_message":
                answer = item.get("text", "")
            elif item.get("type") == "command_execution":
                tools.append("shell")
            elif item.get("type") == "mcp_tool_call":
                tools.append(f"{item.get('server')}.{item.get('tool')}")
        if m.get("type") == "turn.completed":
            u = m.get("usage", {})
            tokens = (tokens or 0) + u.get("input_tokens", 0)
    return dict(model_calls=None, tools=tools, input_tokens=tokens, cost_usd=None, answer=answer)


def copilot(path):
    tools, answer, calls, aiu = [], "", 0, None
    for m in lines(path):
        t, d = m.get("type"), m.get("data") or {}
        if t == "tool.execution_start":
            tools.append(d.get("toolName"))
        elif t == "model.call_finished":
            calls += 1
        elif t == "assistant.message" and d.get("content"):
            answer = d["content"]
        elif t == "session.usage_checkpoint":
            aiu = d.get("totalNanoAiu", 0) / 1e9
    return dict(model_calls=calls, tools=tools, input_tokens=None, cost_usd=None, ai_units=aiu, answer=answer)


def cursor(path):
    tools, answer, tokens = [], "", None
    for m in lines(path):
        if m.get("type") == "tool_call" and m.get("subtype") == "started":
            call = m.get("tool_call") or {}
            name = next(iter(call), "tool")
            if name == "mcpToolCall":
                name = "mcp." + (call[name].get("args", {}).get("toolName") or call[name].get("args", {}).get("name") or "?")
            tools.append(name)
        if m.get("type") == "result":
            answer = m.get("result", "")
            u = m.get("usage") or {}
            tokens = u.get("inputTokens", 0) + u.get("cacheReadTokens", 0) + u.get("cacheWriteTokens", 0)
    return dict(model_calls=None, tools=tools, input_tokens=tokens, cost_usd=None, answer=answer)


def test_counts(answer):
    """Numbers the answer puts next to the word test(s): '44 tests', 'tests: 37', '37 guarding tests'."""
    found = re.findall(r"\b(\d{1,3})\+?\s+(?:\w+\s+){0,3}?tests?\b", answer, re.I)
    return sorted({int(x) for x in found if 1 < int(x) < 500}, reverse=True)


def callers(answer):
    return ("WriteAsync" in answer, bool(re.search(r"\bProgram\b|\bMain\b|IndexAsync", answer)))


rows = []
for meta_path in sorted(glob.glob(os.path.join(E, "logs", "*.meta.json"))):
    meta = json.load(open(meta_path))
    log = meta_path.replace(".meta.json", ".jsonl")
    r = {"claude": claude, "codex": codex, "copilot": copilot, "cursor": cursor}[meta["assistant"]](log)
    r.update(meta)
    r["codegraph_calls"] = sum(1 for t in r["tools"] if "codegraph" in (t or "") or (t or "").startswith("mcp."))
    r["shell_calls"] = sum(1 for t in r["tools"] if t in ("Bash", "shell", "bash", "shellToolCall") or "shell" in (t or "").lower())
    r["counts"] = test_counts(r["answer"])
    r["callers"] = callers(r["answer"])
    rows.append(r)

print(f"{'run':18} {'exit':>4} {'wall':>6} {'calls':>5} {'tools':>5} {'cg':>3} {'sh':>3} {'tokens':>9} {'cost':>6}  counts      callers")
for r in rows:
    print(f"{r['assistant']+'-'+r['cond']+'-'+r['n']:18} {str(r['exit']):>4} {r['wall_s']:>6} {str(r['model_calls'] or '-'):>5} "
          f"{len(r['tools']):>5} {r['codegraph_calls']:>3} {r['shell_calls']:>3} {str(r['input_tokens'] or r.get('ai_units') or '-'):>9} "
          f"{('%.2f' % r['cost_usd']) if r['cost_usd'] else '-':>6}  {str(r['counts']):11} {r['callers']}")

print("\nmedians (range) per assistant and condition")
for a in ("claude", "codex", "copilot", "cursor"):
    for c in ("on", "off"):
        g = [r for r in rows if r["assistant"] == a and r["cond"] == c and r["exit"] == 0]
        if not g:
            continue
        def med(key):
            v = [r[key] for r in g if r.get(key) is not None]
            return f"{statistics.median(v):g} ({min(v):g}-{max(v):g})" if v else "-"
        print(f"  {a:8} {c:3} n={len(g)}  wall {med('wall_s')}  tools {statistics.median(len(r['tools']) for r in g):g}  "
              f"tokens {med('input_tokens')}  aiu {med('ai_units')}  cost {med('cost_usd')}")

if "--answers" in sys.argv:
    for r in rows:
        print(f"\n===== {r['assistant']}-{r['cond']}-{r['n']} =====\n{r['answer'][:2500]}")
