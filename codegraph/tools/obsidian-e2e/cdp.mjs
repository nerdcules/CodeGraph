// A minimal Chrome DevTools Protocol client over Node's built-in fetch and WebSocket (Node 22+): enough to wait
// for Obsidian's window, evaluate code in it, collect console errors and take screenshots. No dependencies.

export async function connect(port, { timeoutMs = 60000 } = {}) {
  const deadline = Date.now() + timeoutMs;
  let target;
  while (!target) {
    try {
      const list = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
      target = list.find((t) => t.type === 'page' && t.webSocketDebuggerUrl);
    } catch {
      // not listening yet
    }

    if (!target) {
      if (Date.now() > deadline) {
        throw new Error(`no Obsidian window on port ${port} after ${timeoutMs} ms`);
      }

      await sleep(500);
    }
  }

  const ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => {
    ws.onopen = resolve;
    ws.onerror = () => reject(new Error('DevTools socket failed'));
  });

  let nextId = 1;
  const pending = new Map();
  const events = [];
  ws.onmessage = (message) => {
    const data = JSON.parse(message.data);
    if (data.id && pending.has(data.id)) {
      const { resolve, reject } = pending.get(data.id);
      pending.delete(data.id);
      data.error ? reject(new Error(data.error.message)) : resolve(data.result);
    } else if (data.method) {
      events.push(data);
    }
  };

  const send = (method, params = {}) => new Promise((resolve, reject) => {
    const id = nextId++;
    pending.set(id, { resolve, reject });
    ws.send(JSON.stringify({ id, method, params }));
  });

  await send('Runtime.enable');
  await send('Page.enable');

  return {
    events,
    send,
    /** Evaluates an async function body in the page and returns its JSON result. */
    async evaluate(body, timeoutMs = 60000) {
      const expression = `(async () => { ${body} })()`;
      const result = await Promise.race([
        send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true }),
        sleep(timeoutMs).then(() => { throw new Error(`evaluate timed out: ${body.slice(0, 80)}`); }),
      ]);
      if (result.exceptionDetails) {
        const detail = result.exceptionDetails.exception?.description || result.exceptionDetails.text;
        throw new Error(detail);
      }

      return result.result.value;
    },
    /** Polls a page expression until it returns a truthy value. */
    async waitFor(body, { timeoutMs = 60000, label = body.slice(0, 80) } = {}) {
      const deadline = Date.now() + timeoutMs;
      for (;;) {
        let value;
        try {
          value = await this.evaluate(body, 10000);
        } catch {
          value = null;
        }

        if (value) {
          return value;
        }

        if (Date.now() > deadline) {
          throw new Error(`timed out waiting for ${label}`);
        }

        await sleep(250);
      }
    },
    async screenshot() {
      const { data } = await send('Page.captureScreenshot', { format: 'png' });
      return Buffer.from(data, 'base64');
    },
    close() {
      ws.close();
    },
  };
}

export const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
