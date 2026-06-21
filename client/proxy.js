#!/usr/bin/env node
/*
 * stdio <-> HTTP MCP proxy for UnityExplorerTLD.MCP.
 *
 * Claude Code launches this as a subprocess (MCP stdio transport) and pipes
 * JSON-RPC over stdin/stdout. The proxy forwards each message to the in-game
 * HTTP MCP server on the Windows host, resolving the WSL->host gateway at
 * runtime - so no IP is ever hardcoded and reboots don't break anything.
 *
 * Why this design:
 *   - No background process: Claude Code starts/stops it with the session.
 *   - No extra dependency: Node ships with Claude Code; only built-ins used.
 *   - No IP in config: the Windows host is resolved live (and re-resolved on
 *     connection failure).
 *
 * Register it with:  claude mcp add unity-explorer -- node /path/to/proxy.js
 * Override target with env vars if needed: UE_MCP_HOST, UE_MCP_PORT.
 */
'use strict';

const http = require('http');
const readline = require('readline');
const { execSync } = require('child_process');

const PORT = parseInt(process.env.UE_MCP_PORT || '3000', 10);

function resolveHost() {
  if (process.env.UE_MCP_HOST) return process.env.UE_MCP_HOST;
  try {
    // WSL2 NAT: the default-route gateway is the Windows host.
    const out = execSync('ip route show default', { encoding: 'utf8' });
    const m = out.match(/default via (\S+)/);
    if (m) return m[1];
  } catch (_) { /* fall through */ }
  return '127.0.0.1';
}

let host = resolveHost();

function forward(line) {
  return new Promise((resolve) => {
    const body = Buffer.from(line, 'utf8');
    const req = http.request(
      {
        host,
        port: PORT,
        path: '/',
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Accept': 'application/json, text/event-stream',
          'Content-Length': body.length,
        },
        timeout: 35000,
      },
      (res) => {
        let data = '';
        res.setEncoding('utf8');
        res.on('data', (c) => (data += c));
        res.on('end', () => resolve({ ok: true, body: data }));
      }
    );
    req.on('error', (e) => resolve({ ok: false, err: e }));
    req.on('timeout', () => { req.destroy(); resolve({ ok: false, err: new Error('timeout') }); });
    req.write(body);
    req.end();
  });
}

function errorLine(id, message) {
  return JSON.stringify({ jsonrpc: '2.0', id: id === undefined ? null : id, error: { code: -32000, message } });
}

const rl = readline.createInterface({ input: process.stdin });

rl.on('line', async (raw) => {
  const line = raw.trim();
  if (!line) return;

  let msg;
  try { msg = JSON.parse(line); } catch (_) { return; } // ignore non-JSON noise
  const hasId = msg && msg.id !== undefined && msg.id !== null;

  let res = await forward(line);
  if (!res.ok) {
    host = resolveHost(); // gateway may have changed - re-resolve and retry once
    res = await forward(line);
  }

  if (res.ok) {
    // Requests get a JSON body; notifications get 202 with no body (write nothing).
    const out = (res.body || '').trim();
    if (out) process.stdout.write(out + '\n');
  } else if (hasId) {
    process.stdout.write(
      errorLine(
        msg.id,
        `Cannot reach UnityExplorer MCP server at ${host}:${PORT}. ` +
        `Is The Long Dark running with the mod, and is the Windows firewall rule for TCP ${PORT} in place? (run client/setup.sh)`
      ) + '\n'
    );
  }
});
