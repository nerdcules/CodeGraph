'use strict';

const { spawn } = require('child_process');

// A minimal MCP client over a child process's stdio: newline-delimited JSON-RPC, enough for initialize and
// tools/call. The server starts on the first call and again after it exits.

const PROTOCOL_VERSION = '2025-06-18';

class McpClient {
  /**
   * @param {{ command: string, args: string[], env?: object, cwd?: string }} launch
   * @param {{ spawn?: Function, timeoutMs?: number, log?: (line: string) => void, clientVersion?: string }} [options]
   */
  constructor(launch, options = {}) {
    this.launch = launch;
    this.spawn = options.spawn || spawn;
    this.timeoutMs = options.timeoutMs || 60000;
    this.log = options.log || (() => {});
    this.clientVersion = options.clientVersion || '0.1.0';
    this.child = null;
    this.ready = null;
    this.nextId = 1;
    this.pending = new Map();
    this.buffer = '';
  }

  /** Calls a tool and returns its text, joined across content blocks. Throws when the tool reports an error. */
  async callTool(name, args = {}) {
    await this.start();
    const result = await this.request('tools/call', { name, arguments: args });
    const text = (result.content || []).filter((c) => c.type === 'text').map((c) => c.text).join('\n');
    if (result.isError) {
      throw new Error(text || `${name} failed`);
    }

    return text;
  }

  start() {
    if (this.ready) {
      return this.ready;
    }

    this.ready = new Promise((resolve, reject) => {
      let child;
      try {
        child = this.spawn(this.launch.command, this.launch.args || [], {
          cwd: this.launch.cwd,
          env: this.launch.env,
          stdio: ['pipe', 'pipe', 'pipe'],
          windowsHide: true,
        });
      } catch (error) {
        this.ready = null;
        reject(error);
        return;
      }

      this.child = child;
      child.stdout.setEncoding('utf8');
      child.stdout.on('data', (chunk) => this.onData(chunk));
      child.stderr.setEncoding('utf8');
      this.stderrTail = '';
      child.stderr.on('data', (chunk) => {
        this.stderrTail = (this.stderrTail + chunk).slice(-2000);
        this.log(String(chunk).trimEnd());
      });
      // A server that dies at once (a missing .NET runtime, say) closes its stdin under the first write; the
      // exit that follows carries the reason from stderr, so the broken pipe itself is only logged.
      child.stdin.on('error', (error) => this.log(`stdin: ${error.message}`));
      child.on('error', (error) => this.onExit(child, error));
      child.on('exit', (code) => {
        const why = this.stderrTail.trim().split(/\r?\n/).filter(Boolean).slice(0, 2).join(' ');
        this.onExit(child, new Error(`codegraph-mcp exited (${code})${why ? ': ' + why : ''}`));
      });

      this.request('initialize', {
        protocolVersion: PROTOCOL_VERSION,
        capabilities: {},
        clientInfo: { name: 'codegraph-obsidian', version: this.clientVersion },
      }).then(() => {
        this.notify('notifications/initialized', {});
        resolve();
      }, (error) => {
        this.ready = null;
        reject(error);
      });
    });

    return this.ready;
  }

  stop() {
    const child = this.child;
    this.child = null;
    this.ready = null;
    if (child) {
      try {
        child.stdin.end();
        child.kill();
      } catch {
        // already gone
      }
    }

    this.failAll(new Error('codegraph-mcp stopped'));
  }

  request(method, params) {
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`${method} timed out after ${this.timeoutMs} ms`));
      }, this.timeoutMs);
      this.pending.set(id, { resolve, reject, timer });
      this.write({ jsonrpc: '2.0', id, method, params });
    });
  }

  notify(method, params) {
    this.write({ jsonrpc: '2.0', method, params });
  }

  write(message) {
    if (!this.child || !this.child.stdin.writable) {
      this.failAll(new Error('codegraph-mcp is not running'));
      return;
    }

    this.child.stdin.write(JSON.stringify(message) + '\n');
  }

  onData(chunk) {
    this.buffer += chunk;
    let newline;
    while ((newline = this.buffer.indexOf('\n')) >= 0) {
      const line = this.buffer.slice(0, newline).trim();
      this.buffer = this.buffer.slice(newline + 1);
      if (line.length === 0) {
        continue;
      }

      let message;
      try {
        message = JSON.parse(line);
      } catch {
        this.log(`unparsed: ${line}`);
        continue;
      }

      if (message.id === undefined || !this.pending.has(message.id)) {
        continue; // a notification or a server request; this client asks for neither
      }

      const { resolve, reject, timer } = this.pending.get(message.id);
      this.pending.delete(message.id);
      clearTimeout(timer);
      if (message.error) {
        reject(new Error(message.error.message || 'MCP error'));
      } else {
        resolve(message.result || {});
      }
    }
  }

  onExit(child, error) {
    if (this.child !== child) {
      return; // an earlier server, already stopped or replaced
    }

    this.child = null;
    this.ready = null;
    this.buffer = '';
    this.failAll(error);
  }

  failAll(error) {
    for (const { reject, timer } of this.pending.values()) {
      clearTimeout(timer);
      reject(error);
    }

    this.pending.clear();
  }
}

module.exports = { McpClient };
