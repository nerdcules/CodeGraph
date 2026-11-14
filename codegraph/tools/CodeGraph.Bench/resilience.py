"""Resilience battery for codegraph-index and codegraph-mcp (BENCHMARKS.md, round 13).

    python3 resilience.py <work dir> <repo template> <codegraph-mcp> <codegraph-index>

<repo template> is a copy of this repository with its graph built (.codegraph/graph.db), so the scenarios can name
its files and members. Each scenario works on its own copy under <work dir> and prints PASS or FAIL with what it
saw. Set DOTNET_ROOT when the executables are framework-dependent."""
import json, os, random, shutil, signal, statistics, subprocess, sys, threading, time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mcpclient import Client

WORK, TEMPLATE, MCP, INDEX = sys.argv[1:5]
results = []


def report(name, ok, detail):
    results.append((name, ok))
    print(f"{'PASS' if ok else 'FAIL'}  {name}: {detail}", flush=True)


def fresh(name, with_graph=True):
    d = os.path.join(WORK, name)
    shutil.rmtree(d, ignore_errors=True)
    shutil.copytree(TEMPLATE, d, symlinks=True, ignore=shutil.ignore_patterns() if with_graph else shutil.ignore_patterns(".codegraph"))
    return d


def sln(d):
    return os.path.join(d, "codegraph", "CodeGraph.slnx")


def index(d, *extra, timeout=300):
    return subprocess.run([INDEX, sln(d), *extra], capture_output=True, text=True, timeout=timeout)


def nodes(text):
    return text.split(" nodes")[0].strip() if " nodes" in text else None


# R1: a server started before any graph exists answers with guidance, then serves the graph once it is built.
d = fresh("r1", with_graph=False)
c = Client(MCP, d)
t1, e1, _ = c.call("graph_stats")
index(d)
time.sleep(1.5)
t2, e2, _ = c.call("graph_stats")
report("R1 no graph, then built while the server runs", e1 and "no code graph" in t1 and not e2 and nodes(t2),
       f"before: {t1[:70]!r} (error={e1}); after build: {t2[:40]!r}")
c.close()

# R2: a corrupt graph file: the server must answer with an error and stay up; the indexer must recover.
d = fresh("r2")
db = os.path.join(d, ".codegraph", "graph.db")
size = os.path.getsize(db)
with open(db, "r+b") as f:
    for _ in range(64):
        f.seek(random.randrange(4096, size)); f.write(os.urandom(4096))
c = Client(MCP, d)
answers = [c.call(tool, **args) for tool, args in [("graph_stats", {}), ("find_symbol", {"query": "Replace"}), ("graph_stats", {})]]
alive = c.p.poll() is None
c.close()
report("R2a corrupt graph: server answers, stays up", alive and all(isinstance(a[0], str) for a in answers),
       f"alive={alive}; answers: {[a[0][:60] for a in answers]}")
r = index(d)
r2 = index(d, "--full") if r.returncode != 0 else r
c = Client(MCP, d); t, e, _ = c.call("graph_stats"); c.close()
report("R2b corrupt graph: indexer recovers", r.returncode == 0 and "damaged" in (r.stdout + r.stderr) and not e and nodes(t),
       f"incremental exit {r.returncode} ({(r.stderr or r.stdout).strip().splitlines()[-1][:110] if (r.stderr or r.stdout).strip() else ''}); "
       f"{'--full exit ' + str(r2.returncode) + '; ' if r2 is not r else ''}then graph_stats: {t[:40]!r}")

# R3: kill -9 the indexer at different moments of a full and of an incremental index; the graph must stay whole.
d = fresh("r3")
c0 = Client(MCP, d); baseline = nodes(c0.call("graph_stats")[0]); c0.close()
outcomes = []
for mode, delay in [("full", 2), ("full", 9), ("full", 13), ("full", 14.5), ("full", 15.5), ("incremental", 1.5), ("incremental", 2.3), ("incremental", 2.6)]:
    if mode == "incremental":
        src = os.path.join(d, "codegraph", "src", "CodeGraph.Core", "Worktree.cs")
        open(src, "a").write(f"\n// kill test {delay}\n")
    p = subprocess.Popen([INDEX, sln(d)] + (["--full"] if mode == "full" else []), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(delay)
    finished = p.poll() is not None
    if not finished:
        p.send_signal(signal.SIGKILL)
    p.wait()
    c = Client(MCP, d); t, e, _ = c.call("graph_stats"); c.close()
    outcomes.append((mode, delay, "finished first" if finished else "killed", nodes(t), e))
after = index(d)
c = Client(MCP, d); final = nodes(c.call("graph_stats")[0]); c.close()
ok = all(n == baseline and not e for _, _, _, n, e in outcomes) and after.returncode == 0 and final == baseline
report("R3 kill -9 mid-index: graph stays readable and whole", ok,
       "; ".join(f"{m} at {dl}s {k}: {n} nodes{' ERROR' if e else ''}" for m, dl, k, n, e in outcomes) + f"; next index exit {after.returncode}, {final} nodes")

# R4: 8 servers reading in a loop while the indexer runs 3 full and 3 incremental passes.
d = fresh("r4")
stop = threading.Event(); lock = threading.Lock(); lat, errs, counts = [], [], set()
def reader(k):
    c = Client(MCP, d)
    while not stop.is_set():
        for tool, args in [("graph_stats", {}), ("find_symbol", {"query": "Replace", "limit": 5}), ("impact_of", {"id": "M:CodeGraph.Core.Worktree.RootOf(System.String)"})]:
            t, e, ms = c.call(tool, **args)
            with lock:
                lat.append(ms)
                if e: errs.append(f"{tool}: {t[:80]}")
                if tool == "graph_stats" and nodes(t): counts.add(nodes(t))
    c.close()
threads = [threading.Thread(target=reader, args=(k,)) for k in range(8)]
[t.start() for t in threads]
time.sleep(2)
src = os.path.join(d, "codegraph", "src", "CodeGraph.Core", "Worktree.cs"); orig = open(src).read()
writes = []
for i in range(3):
    writes.append(index(d, "--full").returncode)
    open(src, "w").write(orig.rstrip()[:-1] + f"    public static void Probe{i}() {{ }}\n}}\n")
    writes.append(index(d).returncode)
stop.set(); [t.join() for t in threads]
c = Client(MCP, d); seen = c.call("find_symbol", query="Probe2", nameMatch="Exact")[0]; c.close()
lat.sort()
report("R4 8 readers during 3 full + 3 incremental writes", not errs and all(w == 0 for w in writes) and "Probe2" in seen,
       f"{len(lat)} calls, {len(errs)} errors {errs[:2]}; writer exits {writes}; latency p50 {lat[len(lat)//2]:.1f} ms, "
       f"p99 {lat[int(len(lat)*.99)]:.1f} ms, max {lat[-1]:.0f} ms; node counts seen {sorted(counts)}; new method visible after: {'Probe2' in seen}")

# R5: malformed requests must each get an answer, and the server must keep serving.
d = os.path.join(WORK, "r4")
c = Client(MCP, d)
probes = []
c.p.stdin.write("this is not json\n"); c.p.stdin.flush()
probes.append(("not JSON", c.call("graph_stats")))
for label, tool, args in [
    ("unknown tool", "no_such_tool", {}),
    ("missing required argument", "impact_of", {}),
    ("wrong type", "impact_of", {"id": "M:X", "depth": "deep"}),
    ("negative depth", "callers_of", {"id": "M:CodeGraph.Core.Worktree.RootOf(System.String)", "depth": -5}),
    ("huge depth", "impact_of", {"id": "M:CodeGraph.Core.Worktree.RootOf(System.String)", "depth": 1000000000}),
    ("1 MB query", "find_symbol", {"query": "x" * 1_000_000}),
    ("unicode and control characters", "find_symbol", {"query": "Ünïcødé\u0000‮"}),
    ("unknown id", "tests_for", {"id": "M:Does.Not.Exist"}),
    ("SQL-looking query", "search_code", {"query": "'; DROP TABLE nodes; --"}),
    ("id with brackets", "tests_for", {"id": "[M:CodeGraph.Core.Worktree.RootOf(System.String)]"}),
    ("id as symbolId", "tests_for", {"symbolId": "M:CodeGraph.Core.Worktree.RootOf(System.String)"}),
]:
    probes.append((label, c.call(tool, **args)))
alive_text = c.call("graph_stats")[0]
c.close()
summary = "; ".join(f"{l} -> {'error' if e else 'answer'} {ms:.0f} ms" for l, (t, e, ms) in probes)
report("R5 malformed requests, then a normal call", nodes(alive_text) is not None and len(probes) == 12, summary)
bad = [(l, t[:100]) for l, (t, e, ms) in probes if "An error occurred invoking" in t]
report("R5b every error says what was wrong", not bad, f"opaque errors: {bad}" if bad else "no opaque 'An error occurred' answers")
print("      graph still intact after the SQL-looking query:", nodes(alive_text), "nodes", flush=True)

# R6: the live daemon dies under a server that started it (CODEGRAPH_DAEMON=auto).
d = os.path.join(WORK, "r4")
c = Client(MCP, d, env={"CODEGRAPH_DAEMON": "auto"})
c.call("graph_stats")
deadline = time.time() + 120; status = ""
while time.time() < deadline:
    status = c.call("live_status")[0]
    if "running" in status and "loading" not in status: break
    time.sleep(2)
pid = int(status.split("pid ")[1].split(")")[0]) if "pid " in status else None
if pid: os.kill(pid, signal.SIGKILL)
time.sleep(1)
after_kill = c.call("live_status")[0].splitlines()[0]
graph_ok = nodes(c.call("graph_stats")[0])
live_t, live_e, _ = c.call("diagnostics", file=os.path.join(d, "codegraph/src/CodeGraph.Core/Worktree.cs"))
c.call("live_status", start=True)
deadline = time.time() + 120; restarted = ""
while time.time() < deadline:
    restarted = c.call("live_status")[0].splitlines()[0]
    if "running" in restarted: break
    time.sleep(2)
c.close()
report("R6 live daemon killed under the server", pid is not None and graph_ok and "running" in restarted,
       f"daemon pid {pid}; after kill: {after_kill!r}; graph tools: {graph_ok} nodes; live tool: {live_t.splitlines()[0][:90]!r}; "
       f"live_status start=true: {restarted!r}")
if "pid " in restarted:
    os.kill(int(restarted.split("pid ")[1].split(")")[0]), signal.SIGTERM)
time.sleep(1)

# R7: closing stdin ends the server.
c = Client(MCP, os.path.join(WORK, "r4")); c.call("graph_stats")
t = time.time(); c.p.stdin.close(); code = c.p.wait(15); took = time.time() - t
report("R7 stdin closed: the server exits", code == 0 and took < 5, f"exit {code} after {took:.1f} s")

# R8: --root edge cases.
outs = []
for label, root in [("missing folder", "/nonexistent/x"), ("a file", os.path.join(WORK, "r4", "codegraph", "CodeGraph.slnx")),
                    ("unexpanded placeholder", "${workspaceFolder}"), ("empty", "")]:
    c = Client(MCP, root if root else "", env={})
    t, e, _ = c.call("graph_stats"); outs.append((label, t.splitlines()[0][:70], e)); c.close()
report("R8 --root edge cases: no crash, a clear answer", all(isinstance(t, str) for _, t, _ in outs), "; ".join(f"{l}: {t!r}" for l, t, e in outs))

# R9: an edit without a daemon is flagged as stale.
d = os.path.join(WORK, "r4")
src = os.path.join(d, "codegraph", "src", "CodeGraph.Core", "Worktree.cs"); open(src, "a").write("\n// stale test\n")
c = Client(MCP, d); t, e, _ = c.call("find_symbol", query="RootOf"); c.close()
report("R9 edited file without a daemon: answer flagged stale", t.startswith("⚠ STALE INDEX"), t.splitlines()[0][:120])

# R10: init against broken or awkward config files.
d = fresh("r10")
cfg = lambda *p: os.path.join(d, *p)
open(cfg(".mcp.json"), "w").write("[]")
a = subprocess.run([INDEX, "init", sln(d), "--no-index"], capture_output=True, text=True)
untouched = open(cfg(".mcp.json")).read() == "[]"
os.makedirs(cfg(".vscode"), exist_ok=True)
open(cfg(".vscode", "mcp.json"), "w").write('{\n  // a comment\n  "servers": { "other": { "type": "stdio", "command": "x" }, },\n}\n')
os.remove(cfg(".mcp.json"))
b = subprocess.run([INDEX, "init", sln(d), "--no-index", "--client", "vscode"], capture_output=True, text=True)
merged = json.load(open(cfg(".vscode", "mcp.json")))["servers"]
os.makedirs(cfg(".cursor"), exist_ok=True); open(cfg(".cursor", "mcp.json"), "w").write("{}"); os.chmod(cfg(".cursor", "mcp.json"), 0o444); os.chmod(cfg(".cursor"), 0o555)
c3 = subprocess.run([INDEX, "init", sln(d), "--no-index", "--client", "cursor"], capture_output=True, text=True)
os.chmod(cfg(".cursor"), 0o755); os.chmod(cfg(".cursor", "mcp.json"), 0o644)
last = lambda r: (r.stdout + r.stderr).strip().splitlines()[-1][:110] if (r.stdout + r.stderr).strip() else ""
report("R10 init with broken config files", a.returncode != 0 and untouched and b.returncode == 0 and set(merged) == {"other", "codegraph"} and c3.returncode != 0,
       f"non-object .mcp.json: exit {a.returncode}, file untouched {untouched} ({last(a)!r}); "
       f"JSONC with comments: exit {b.returncode}, servers {sorted(merged)}; read-only .cursor/mcp.json: exit {c3.returncode} ({last(c3)!r})")

print(f"\n{sum(1 for _, ok in results if ok)} of {len(results)} passed")
