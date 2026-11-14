"""A persistent stdio MCP client for codegraph-mcp: Client(server, root).call(tool, **args) -> (text, is_error, ms)."""
import json, os, subprocess, time
class Client:
    def __init__(self, server, root, env=None):
        self.p = subprocess.Popen([server, "--root", root], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=subprocess.DEVNULL, text=True, env={**os.environ, **(env or {})})
        self.n = 0
        self.rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "perf", "version": "1"}})
        self.send({"jsonrpc": "2.0", "method": "notifications/initialized"})
    def send(self, m): self.p.stdin.write(json.dumps(m) + "\n"); self.p.stdin.flush()
    def rpc(self, method, params):
        self.n += 1; i = self.n; self.send({"jsonrpc": "2.0", "id": i, "method": method, "params": params})
        for line in self.p.stdout:
            m = json.loads(line)
            if m.get("id") == i: return m
        raise EOFError("server closed")
    def call(self, tool, **args):
        t = time.perf_counter(); m = self.rpc("tools/call", {"name": tool, "arguments": args}); ms = (time.perf_counter() - t) * 1000
        if "error" in m: return json.dumps(m["error"]), True, ms
        r = m["result"]; return "\n".join(c.get("text", "") for c in r["content"]), bool(r.get("isError")), ms
    def close(self): self.p.stdin.close(); return self.p.wait(10)
