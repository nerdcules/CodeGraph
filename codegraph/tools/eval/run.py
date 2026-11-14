"""One eval run: run.py <assistant> <on|off> <n> [prompt]. Writes logs/<assistant>-<cond>-<n>.jsonl (the CLI's own
machine-readable output, one line each) and .meta.json (command, wall time, exit code).

Expects, next to this script: on/ (the repo with .codegraph/graph.db and .cursor/{mcp,cli}.json) and off/ (the same repo
without .codegraph/, with .cursor/cli.json only). CODEGRAPH_MCP is the server to test; Codex uses its own global entry,
which must point at the same server. See README.md."""
import json, os, subprocess, sys, time

E = os.environ.get("EVAL_DIR") or os.path.dirname(os.path.abspath(__file__))
MCP = os.environ["CODEGRAPH_MCP"]
SERVER_ENV = {k: os.environ[k] for k in ("DOTNET_ROOT",) if k in os.environ}
QUESTION = "What breaks if I change GraphStore.Replace, and which tests cover it?"
assistant, cond, n = sys.argv[1], sys.argv[2], sys.argv[3]
prompt = sys.argv[4] if len(sys.argv) > 4 else QUESTION
repo = os.path.join(E, cond)

READ_TOOLS = ["grep", "rg", "find", "ls", "cat", "head", "tail", "sed", "awk", "wc"]
OTHER_CODEX_SERVERS = []  # plugin servers (codex_app, cua_repl) cannot be disabled by -c; all non-codegraph servers stay as configured in both conditions

if assistant == "claude":
    servers = {"codegraph": {"command": MCP, "env": SERVER_ENV}} if cond == "on" else {}
    cmd = ["claude", "-p", prompt, "--model", "claude-opus-5-5", "--effort", "high", "--permission-mode", "auto",
           "--output-format", "stream-json", "--verbose", "--strict-mcp-config",
           "--mcp-config", json.dumps({"mcpServers": servers}), "--no-session-persistence"]
elif assistant == "codex":
    off = OTHER_CODEX_SERVERS + (["codegraph"] if cond == "off" else [])
    cmd = ["codex", "exec", "--json", "-m", "gpt-6.1-sol", "-s", "read-only", "--skip-git-repo-check"]
    for server in off:
        cmd += ["-c", f"mcp_servers.{server}.enabled=false"]
    cmd += [prompt]
elif assistant == "copilot":
    cmd = ["copilot", "-p", prompt, "--model", "claude-sonnet-5.5", "--output-format", "json", "--no-color"]
    for tool in READ_TOOLS:
        cmd += ["--allow-tool", f"shell({tool})"]
    if cond == "on":
        server = {"type": "local", "command": MCP, "args": [], "env": SERVER_ENV, "tools": ["*"]}
        cmd += ["--additional-mcp-config", json.dumps({"mcpServers": {"codegraph": server}}), "--allow-tool", "codegraph"]
elif assistant == "cursor":
    cmd = ["cursor-agent", "-p", prompt, "--trust", "--model", "composer-2.5", "--output-format", "stream-json"]
else:
    sys.exit(f"unknown assistant {assistant}")

# A clean environment: nothing from the Claude Code session that launches the runs.
env = {k: v for k, v in os.environ.items() if not k.startswith(("CLAUDE", "CODEX_", "COPILOT_", "CURSOR_"))}
os.makedirs(os.path.join(E, "logs"), exist_ok=True)
out = os.path.join(E, "logs", f"{assistant}-{cond}-{n}")
t0 = time.time()
with open(out + ".jsonl", "w") as stdout, open(out + ".err", "w") as stderr:
    try:
        code = subprocess.run(cmd, cwd=repo, stdin=subprocess.DEVNULL, stdout=stdout, stderr=stderr, env=env, timeout=900).returncode
    except subprocess.TimeoutExpired:
        code = "timeout"
wall = round(time.time() - t0, 1)
json.dump({"assistant": assistant, "cond": cond, "n": n, "cmd": cmd, "cwd": repo, "wall_s": wall, "exit": code},
          open(out + ".meta.json", "w"), indent=1)
print(f"{assistant}-{cond}-{n}: exit {code}, {wall} s")
