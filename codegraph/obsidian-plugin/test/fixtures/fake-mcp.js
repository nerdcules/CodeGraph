'use strict';

// A stand-in for codegraph-mcp: newline-delimited JSON-RPC on stdio. Answers initialize, and tools/call with a
// staleness warning plus one row for callers_of, an error for `broken`, and exits for `crash`.

let buffer = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', (chunk) => {
  buffer += chunk;
  let newline;
  while ((newline = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, newline);
    buffer = buffer.slice(newline + 1);
    if (line.trim()) {
      handle(JSON.parse(line));
    }
  }
});

function reply(id, result) {
  process.stdout.write(JSON.stringify({ jsonrpc: '2.0', id, result }) + '\n');
}

function handle(message) {
  if (message.method === 'initialize') {
    process.stdout.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/message', params: { level: 'info', data: 'hello' } }) + '\n');
    reply(message.id, { protocolVersion: message.params.protocolVersion, capabilities: { tools: {} }, serverInfo: { name: 'fake', version: '1' } });
    return;
  }

  if (message.method !== 'tools/call') {
    return;
  }

  const { name, arguments: args } = message.params;
  if (name === 'crash') {
    process.exit(3);
  }

  if (name === 'broken') {
    reply(message.id, { content: [{ type: 'text', text: 'no node with id X' }], isError: true });
    return;
  }

  reply(message.id, {
    content: [
      { type: 'text', text: '⚠ STALE INDEX: 1 file edited since the graph was built. Refresh with: codegraph-index "/repo/App.sln"' },
      { type: 'text', text: `callers of ${args.id}:\nmethod  App.Client.Run()  depth ${args.depth}  /repo/src/Client.cs:52-55  [M:App.Client.Run]\n` },
    ],
  });
}
