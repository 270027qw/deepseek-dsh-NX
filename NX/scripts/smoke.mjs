#!/usr/bin/env node
// ============================================================================
// scripts/smoke.mjs
// NX Copilot 协议冒烟测试：对任意 bridge（mock 或真实 NX 桥）跑一遍核心方法。
//
// 用法:
//   node scripts/smoke.mjs [--host 127.0.0.1] [--port 8123] [--token demo]
// ============================================================================

import http from 'node:http';

const args = process.argv.slice(2);
function arg(name, def) {
  const i = args.indexOf(name);
  return i >= 0 && i + 1 < args.length ? args[i + 1] : def;
}
const host = arg('--host', '127.0.0.1');
const port = Number(arg('--port', '8123'));
const token = arg('--token', '');

function rpc(method, params = {}) {
  return new Promise((resolve, reject) => {
    const body = JSON.stringify({ jsonrpc: '2.0', id: 1, method, params });
    const req = http.request(
      {
        host,
        port,
        path: '/rpc',
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Content-Length': Buffer.byteLength(body),
          ...(token ? { Authorization: `Bearer ${token}` } : {}),
        },
      },
      (res) => {
        let data = '';
        res.setEncoding('utf8');
        res.on('data', (c) => (data += c));
        res.on('end', () => {
          try {
            resolve(JSON.parse(data));
          } catch {
            reject(new Error(`非 JSON 响应: ${data.slice(0, 200)}`));
          }
        });
      },
    );
    req.on('error', reject);
    req.setTimeout(30000, () => req.destroy(new Error('timeout')));
    req.write(body);
    req.end();
  });
}

function ok(name, value) {
  console.log(`[PASS] ${name}`);
  return value;
}

const steps = [
  ['session.info', {}, (r) => r.result.nxVersion],
  ['model.tree', {}, (r) => r.result.features.length],
  ['feature.block', { origin: [0, 0, 0], lengthX: 100, lengthY: 60, lengthZ: 40, name: 'Base Plate' }, (r) => r.result.feature.journalId],
  ['feature.cylinder', { origin: [50, 30, 40], direction: [0, 0, 1], diameter: 20, height: 30, name: 'Boss' }, (r) => r.result.feature.journalId],
  ['feature.sphere', { origin: [0, 0, 50], diameter: 15, name: 'Ball' }, (r) => r.result.feature.journalId],
  ['measure.distance', { p1: [0, 0, 0], p2: [100, 0, 0] }, (r) => r.result.distance],
  ['model.tree', {}, (r) => r.result.features.length],
  ['journal.run', { code: 'RESULT = "hello from journal";' }, (r) => r.result.ok],
];

console.log(`smoke test -> http://${host}:${port}/ (token: ${token ? 'set' : 'NONE'})`);
for (const [method, params, pick] of steps) {
  const res = await rpc(method, params);
  if (res.error) {
    console.error(`[FAIL] ${method}: ${JSON.stringify(res.error)}`);
    process.exitCode = 1;
    continue;
  }
  const picked = pick ? pick(res) : res.result;
  console.log(`[PASS] ${method} -> ${JSON.stringify(picked)}`);
}
console.log(process.exitCode ? 'smoke test FAILED' : 'smoke test OK');
