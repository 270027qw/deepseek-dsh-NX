#!/usr/bin/env node
// ============================================================================
// mock-bridge/server.mjs
// NX Copilot 模拟桥 —— 与 nx-bridge/journals/NXBridgeServer.cs 相同协议
// （JSON-RPC 2.0 over HTTP），用内存模型模拟 NX 会话，供无 NX 环境的本机
// 端到端联调（DSH 插件 ↔ bridge）。
//
// 用法:
//   node mock-bridge/server.mjs [--port 8123] [--token demo]
// ============================================================================

import http from 'node:http';
import { URL } from 'node:url';

const args = process.argv.slice(2);
const port = Number(args[args.indexOf('--port') + 1] ?? 8123);
const token = args[args.indexOf('--token') + 1] ?? '';

// ---------------- 内存模型 ----------------

const model = {
  part: {
    name: 'mock_part.prt',
    fullPath: 'C:\\mock\\mock_part.prt',
    units: 'Millimeters',
    modified: false,
  },
  bodies: [
    { name: 'SOLID BODY 1', type: 'solid', faces: 6, edges: 12 },
  ],
  features: [
    { name: 'Block (100x60x40)', type: 'BLOCK', journalId: 'BLOCK(1)', suppressed: false },
  ],
  nextFeature: 2,
};

let requestCount = 0;

// ---------------- JSON-RPC 分发 ----------------

const handlers = {
  'server.ping': () => ({
    pong: true,
    time: new Date().toISOString(),
    nxVersion: '2606.1000 (mock)',
  }),
  'server.stop': () => {
    process.exit(0);
  },
  'session.info': () => ({
    nxVersion: '2606.1000 (mock)',
    processId: process.pid,
    server: `http://mock-bridge:${port}/`,
    requests: requestCount,
    workPart: { ...model.part },
  }),
  'part.open': (p) => {
    model.part.fullPath = p.file ?? model.part.fullPath;
    model.part.name = p.file ? p.file.split(/[\\/]/).pop() : model.part.name;
    model.part.modified = false;
    return { alreadyOpen: false, part: { ...model.part } };
  },
  'part.save': () => {
    model.part.modified = false;
    return { saved: true };
  },
  'part.closeAll': () => ({ closed: true }),
  'model.tree': () => ({
    part: { ...model.part },
    bodies: model.bodies.map((b) => ({ ...b })),
    features: model.features.map((f) => ({ ...f })),
  }),
  'feature.block': (p) => addFeature('BLOCK', p, {
    name: p.name ?? `Block (${p.lengthX}x${p.lengthY}x${p.lengthZ})`,
    origin: p.origin ?? [0, 0, 0],
    lengthX: p.lengthX, lengthY: p.lengthY, lengthZ: p.lengthZ,
  }),
  'feature.cylinder': (p) => addFeature('CYLINDER', p, {
    name: p.name ?? `Cylinder (d${p.diameter} h${p.height})`,
    origin: p.origin ?? [0, 0, 0],
    direction: p.direction ?? [0, 0, 1],
    diameter: p.diameter, height: p.height,
  }),
  'feature.sphere': (p) => addFeature('SPHERE', p, {
    name: p.name ?? `Sphere (d${p.diameter})`,
    center: p.origin ?? [0, 0, 0],
    diameter: p.diameter,
  }),
  'feature.suppress': (p) => setSuppressed(p.featureId, true),
  'feature.unsuppress': (p) => setSuppressed(p.featureId, false),
  'measure.distance': (p) => {
    const [x1, y1, z1] = p.p1 ?? [0, 0, 0];
    const [x2, y2, z2] = p.p2 ?? [0, 0, 0];
    return {
      distance: Math.hypot(x2 - x1, y2 - y1, z2 - z1),
      units: 'mm',
      note: '坐标间欧氏距离（模拟桥）',
    };
  },
  'ui.message': (p) => ({ shown: true, message: p.text ?? '' }),
  'journal.run': (p) => {
    const code = p.code ?? '';
    const lines = code.split('\n').filter((l) => l.trim());
    // 模拟：尝试从代码中提取 RESULT 赋值；否则返回代码摘要
    const resultMatch = code.match(/RESULT\s*=\s*"([^"]*)"/);
    return {
      ok: true,
      result: resultMatch ? resultMatch[1] : null,
      mock: true,
      compiledLines: lines.length,
      preview: code.slice(0, 200),
    };
  },
};

function addFeature(type, p, info) {
  const journalId = `${type}(${model.nextFeature++})`;
  model.features.push({ name: info.name, type, journalId, suppressed: false });
  model.part.modified = true;
  return { feature: { journalId, name: info.name } };
}

function setSuppressed(featureId, suppressed) {
  const f = model.features.find((x) => x.journalId === featureId);
  if (!f) return { error: { code: -32000, message: `未找到特征 ${featureId}` } };
  f.suppressed = suppressed;
  model.part.modified = true;
  return { journalId: featureId, name: f.name, suppressed };
}

// ---------------- HTTP 服务 ----------------

const server = http.createServer((req, res) => {
  const url = new URL(req.url, `http://127.0.0.1:${port}`);

  if (req.method === 'GET' && url.pathname === '/ping') {
    res.writeHead(200, { 'Content-Type': 'text/plain; charset=utf-8' });
    res.end('pong');
    return;
  }

  if (req.method !== 'POST' || url.pathname !== '/rpc') {
    sendJson(res, 200, { jsonrpc: '2.0', id: null, error: { code: -32601, message: 'method not found' } });
    return;
  }

  const auth = req.headers.authorization ?? '';
  const authorized = token === '' || auth === `Bearer ${token}`;
  if (!authorized) {
    sendJson(res, 200, { jsonrpc: '2.0', id: null, error: { code: -32001, message: 'unauthorized' } });
    return;
  }

  let body = '';
  req.on('data', (c) => (body += c));
  req.on('end', () => {
    let reqJson;
    try {
      reqJson = JSON.parse(body);
    } catch {
      sendJson(res, 200, { jsonrpc: '2.0', id: null, error: { code: -32700, message: 'parse error' } });
      return;
    }
    const method = reqJson.method;
    const params = reqJson.params ?? {};
    const id = reqJson.id ?? null;
    requestCount += 1;

    const handler = handlers[method];
    if (!handler) {
      sendJson(res, 200, { jsonrpc: '2.0', id, error: { code: -32601, message: `未知方法: ${method}` } });
      return;
    }

    try {
      const result = handler(params);
      if (result && result.error) {
        sendJson(res, 200, { jsonrpc: '2.0', id, error: result.error });
      } else {
        sendJson(res, 200, { jsonrpc: '2.0', id, result });
      }
    } catch (err) {
      sendJson(res, 200, { jsonrpc: '2.0', id, error: { code: -32000, message: String(err?.message ?? err) } });
    }
  });
});

function sendJson(res, status, payload) {
  const text = JSON.stringify(payload);
  res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8' });
  res.end(text);
}

server.listen(port, '127.0.0.1', () => {
  console.log(`[mock-bridge] listening on http://127.0.0.1:${port}/ (token: ${token === '' ? 'NONE' : '***'})`);
  console.log(`[mock-bridge] rpc endpoint: POST /rpc   health: GET /ping`);
});
