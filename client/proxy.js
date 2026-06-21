#!/usr/bin/env node
/*
 * stdio <-> HTTP MCP proxy for UnityExplorerTLD.MCP.
 *
 * Claude Code launches this as a subprocess (MCP stdio transport) and pipes
 * JSON-RPC over stdin/stdout. The proxy forwards each message to the in-game
 * HTTP MCP server and figures out the host itself at runtime:
 *   - tries 127.0.0.1 first (works for a Windows-side server, or WSL mirrored
 *     networking), then the WSL2 NAT default-route gateway (the Windows host),
 *   - caches whichever answers.
 * So nothing is hardcoded, setup needs no IP, and the game does not have to be
 * running when you set things up (the proxy just resolves the host on first use).
 *
 * Register:  claude mcp add unity-explorer -- node /path/to/proxy.js
 * Overrides: UE_MCP_HOST (skip detection), UE_MCP_PORT (default 3000).
 */
'use strict';

const http = require('http');
const readline = require('readline');
const { execSync } = require('child_process');

const PORT = parseInt(process.env.UE_MCP_PORT || '3000', 10);
const CONNECT_TIMEOUT_MS = 4000;    // fail fast per host if nothing is listening
const RESPONSE_TIMEOUT_MS = 35000;  // an eval can wait on the game's main thread

function candidateHosts() {
  if (process.env.UE_MCP_HOST) return [process.env.UE_MCP_HOST];
  const hosts = ['127.0.0.1'];
  try {
    const m = execSync('ip route show default', { encoding: 'utf8' }).match(/default via (\S+)/);
    if (m && m[1] && m[1] !== '127.0.0.1') hosts.push(m[1]); // WSL2 NAT gateway = Windows host
  } catch (_) { /* not WSL / no default route */ }
  return hosts;
}

let cachedHost = null;

function forward(host, line) {
  return new Promise((resolve) => {
    const body = Buffer.from(line, 'utf8');
    const req = http.request(
      {
        host, port: PORT, path: '/', method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Accept': 'application/json, text/event-stream',
          'Content-Length': body.length,
        },
      },
      (res) => {
        let data = '';
        res.setEncoding('utf8');
        res.on('data', (c) => (data += c));
        res.on('end', () => resolve({ ok: true, body: data }));
      }
    );
    req.on('socket', (socket) => {
      socket.setTimeout(CONNECT_TIMEOUT_MS);
      socket.once('connect', () => socket.setTimeout(RESPONSE_TIMEOUT_MS));
      socket.on('timeout', () => req.destroy(new Error('timeout')));
    });
    req.on('error', (e) => resolve({ ok: false, err: e }));
    req.write(body);
    req.end();
  });
}

async function forwardBest(line) {
  const hosts = cachedHost ? [cachedHost] : candidateHosts();
  for (const h of hosts) {
    const res = await forward(h, line);
    if (res.ok) { cachedHost = h; return res; }
  }
  if (cachedHost) { cachedHost = null; return forwardBest(line); } // cached host died - re-probe
  return { ok: false, hosts };
}

function errorLine(id, hosts) {
  return JSON.stringify({
    jsonrpc: '2.0',
    id: id === undefined ? null : id,
    error: {
      code: -32000,
      message: `Cannot reach the UnityExplorer MCP server on port ${PORT} (tried ${(hosts || []).join(', ')}). ` +
        `Is The Long Dark running with the UnityExplorerTLD.MCP mod, and was client/setup.sh run (firewall rule)?`,
    },
  });
}

const rl = readline.createInterface({ input: process.stdin });
rl.on('line', async (raw) => {
  const line = raw.trim();
  if (!line) return;

  let msg;
  try { msg = JSON.parse(line); } catch (_) { return; } // ignore non-JSON noise
  const hasId = msg && msg.id !== undefined && msg.id !== null;

  const res = await forwardBest(line);
  if (res.ok) {
    const out = (res.body || '').trim();   // requests -> JSON; notifications -> 202/no body
    if (out) process.stdout.write(out + '\n');
  } else if (hasId) {
    process.stdout.write(errorLine(msg.id, res.hosts) + '\n');
  }
});
