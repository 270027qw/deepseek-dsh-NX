// ============================================================================
// dsh-plugin/host.js —— NX Copilot 插件 Host 半部分
// 在 DeepSeek Harness 中通过 cordis_define 定义插件时使用（与仓库文件保持同步）。
//
// 提供模型可见工具：
//   nx_bridge_connect  —— 连接 NX 桥并获取会话信息
//   nx_bridge_call     —— 调用桥接方法（session.info / model.tree / feature.* …）
//   nx_bridge_journal  —— 把 NXOpen C# 代码交给桥编译执行（journal.run）
// 以及 Client 面板使用的 Package 私有 RPC：
//   nx.status / nx.connect
//
// 传输：通过 subprocess 服务执行 node -e 内联 HTTP 客户端（避免依赖宿主全局
//       fetch/网络模块，桥接协议见 docs/protocol.md）。
// ============================================================================

return {
  apply(ctx) {
    // ---------------- 状态 ----------------
    const state = {
      url: null,
      token: null,
      session: null,
      lastOp: null,
      lastError: null,
      lastResult: null,
      count: 0,
    };

    // ---------------- 内联 Node HTTP 客户端 ----------------
    // argv: [node, '-e', CLIENT_JS, url, token, method, paramsJson]
    const CLIENT_JS = [
      "const http=require('http');",
      "const u=new URL(process.argv[1]);",
      "const params=JSON.parse(process.argv[4]||'null');",
      "const body=JSON.stringify({jsonrpc:'2.0',id:1,method:process.argv[3],params:params});",
      "const req=http.request({hostname:u.hostname,port:u.port||80,path:u.pathname||'/rpc',method:'POST',headers:{'Content-Type':'application/json','Authorization':'Bearer '+process.argv[2],'Content-Length':Buffer.byteLength(body)}},res=>{let d='';res.setEncoding('utf8');res.on('data',c=>d+=c);res.on('end',()=>process.stdout.write(JSON.stringify({status:res.statusCode,body:d})));});",
      "req.on('error',e=>process.stdout.write(JSON.stringify({status:0,error:String(e&&e.message||e)})));",
      "req.setTimeout(25000,()=>req.destroy(new Error('timeout')));",
      "req.write(body);req.end();",
    ].join('\n');

    // ---------------- 桥接调用 ----------------
    async function rpc(url, token, method, params, signal) {
      const subprocess = ctx.get('subprocess');
      if (subprocess === undefined) throw new Error('subprocess 服务不可用');
      let nodePath;
      try {
        nodePath = await subprocess.resolveExecutable('node');
      } catch {
        throw new Error('无法解析 node 可执行文件路径');
      }
      let cwd = '.';
      const fs = ctx.get('fs');
      if (fs !== undefined) {
        try {
          cwd = fs.processPath(await fs.resolve('.'));
        } catch {
          /* 保留默认 cwd */
        }
      }
      const handle = subprocess.spawn({
        argv: [nodePath, '-e', CLIENT_JS, url, token || '', method, JSON.stringify(params || {})],
        cwd,
        stdio: {
          stdin: 'ignore',
          stdout: { maxBytes: 2 * 1024 * 1024, spill: { maxBytes: 8 * 1024 * 1024 } },
          stderr: { maxBytes: 200 * 1024 },
        },
        graceMs: 5000,
        signal,
      });
      const outcome = await handle.done;
      const out = handle.collected.stdout ? handle.collected.stdout.readFrom(0).text : '';
      const err = handle.collected.stderr ? handle.collected.stderr.readFrom(0).text : '';
      if (outcome.exitCode !== 0) {
        throw new Error('桥接客户端异常退出（' + outcome.exitCode + '）: ' + (err || out).slice(0, 500));
      }
      let parsed;
      try {
        parsed = JSON.parse(out);
      } catch {
        throw new Error('桥接客户端输出非 JSON: ' + out.slice(0, 200));
      }
      if (parsed.error) throw new Error('无法连接桥接服务: ' + parsed.error);
      if (parsed.status !== 200) throw new Error('桥接 HTTP ' + parsed.status + ': ' + String(parsed.body).slice(0, 500));
      const rpcRes = JSON.parse(parsed.body);
      if (rpcRes.error) {
        const detail = rpcRes.error.detail ? '：' + rpcRes.error.detail : '';
        throw new Error((rpcRes.error.message || '桥接错误') + detail);
      }
      return rpcRes.result;
    }

    async function callRpc(method, params, opts, signal) {
      const url = opts.host ? 'http://' + opts.host + ':' + (opts.port || 8123) + '/rpc' : state.url;
      if (!url) throw new Error('未连接桥接服务：请先调用 nx_bridge_connect，或在参数中提供 host/port');
      const token = opts.token !== undefined ? opts.token : state.token;
      return await rpc(url, token, method, params, signal);
    }

    async function safe(method, fn) {
      try {
        const value = await fn();
        state.lastOp = method;
        state.lastError = null;
        state.lastResult = value;
        state.count += 1;
        return { ok: true, method, ...value };
      } catch (e) {
        state.lastOp = method;
        state.lastError = String((e && e.message) || e);
        return { ok: false, method, error: state.lastError };
      }
    }

    async function connect(url, token, signal) {
      const session = await rpc(url, token || '', 'session.info', {}, signal);
      state.url = url;
      state.token = token || '';
      state.session = session;
      state.lastError = null;
      return { ok: true, url, session };
    }

    // ---------------- 工具注册 ----------------
    // 注意：动态工具必须由 harness.defineTool(...) 创建（带动态标记），
    //       再经 harness.registerTool(ctx, tool) 注册。
    const output = {
      schema: { type: 'json' },
      render: (_args, value) => [{ type: 'text', text: JSON.stringify(value, null, 2) }],
    };

    const toolConnect = harness.defineTool({
      name: 'nx_bridge_connect',
      description:
        '连接 NX Copilot 桥接服务并获取 NX 会话信息（NX 版本、当前部件、单位）。' +
        '桥接服务运行在装有 NX 2606 的机器上（nx-bridge/journals/NXBridgeServer.cs，默认端口 8123），' +
        '本机联调可用 mock-bridge（node mock-bridge/server.mjs --port 8123 --token demo）。' +
        '成功后在后续 nx_bridge_call / nx_bridge_journal 中可省略 host/port/token。',
      parameters: {
        host: { type: 'string', required: true, description: '桥接服务主机 IP（如 192.168.1.50 或 127.0.0.1）' },
        port: { type: 'number', description: '端口，默认 8123' },
        token: { type: 'string', description: '访问令牌（桥接端配置的 TOKEN；若桥接未设令牌可省略）' },
      },
      output,
      async execute(args, exec) {
        const url = 'http://' + args.host + ':' + (args.port || 8123) + '/rpc';
        return await safe('nx_bridge_connect', () => connect(url, args.token || '', exec.signal));
      },
    });
    ctx.effect(() => harness.registerTool(ctx, toolConnect));

    const toolCall = harness.defineTool({
      name: 'nx_bridge_call',
      description:
        '调用 NX 桥接方法（JSON-RPC 2.0）。常用方法：' +
        'session.info（会话信息）、part.open {file}、part.save、model.tree（体/特征列表）、' +
        'feature.block {origin:[x,y,z],lengthX,lengthY,lengthZ,name?}、' +
        'feature.cylinder {origin,direction,diameter,height,name?}、' +
        'feature.sphere {origin,diameter,name?}、' +
        'feature.suppress/feature.unsuppress {featureId}、' +
        'measure.distance {p1:[x,y,z],p2:[x,y,z]}、ui.message {text}、server.ping。' +
        '单位一律毫米。完整协议见 docs/protocol.md。',
      parameters: {
        method: { type: 'string', required: true, description: '桥接方法名（见描述）' },
        params: { type: 'json', description: '方法参数对象，如 {"origin":[0,0,0],"lengthX":100}' },
        host: { type: 'string', description: '覆盖连接地址的主机（已连接时可省略）' },
        port: { type: 'number', description: '覆盖端口（默认 8123）' },
        token: { type: 'string', description: '覆盖令牌（已连接时可省略）' },
      },
      output,
      async execute(args, exec) {
        const opts = {
          host: args.host,
          port: args.port,
          token: args.token,
        };
        return await safe('nx_bridge_call:' + args.method, () => callRpc(args.method, args.params || {}, opts, exec.signal));
      },
    });
    ctx.effect(() => harness.registerTool(ctx, toolCall));

    const toolJournal = harness.defineTool({
      name: 'nx_bridge_journal',
      description:
        '把一段 NXOpen C# 代码交给桥接服务编译执行（journal.run）。' +
        '代码约定：直接写语句；可用 theSession / theUI（已预置）；结束时把结果赋给 RESULT（字符串）。' +
        '例如生成法兰：theSession.Parts.Work.Features.CreateCylinderBuilder(null) …；RESULT="done"。' +
        '编译错误会原样返回，可根据错误修正代码重试。这是实现自由建模能力的通道（孔、布尔、草图等）。',
      parameters: {
        code: { type: 'string', required: true, description: 'NXOpen C# 代码（语句序列）' },
        name: { type: 'string', description: '操作名（显示用）' },
        host: { type: 'string', description: '覆盖连接地址的主机' },
        port: { type: 'number', description: '覆盖端口' },
        token: { type: 'string', description: '覆盖令牌' },
      },
      output,
      async execute(args, exec) {
        const opts = { host: args.host, port: args.port, token: args.token };
        return await safe('nx_bridge_journal:' + (args.name || 'journal'), () =>
          callRpc('journal.run', { code: args.code }, opts, exec.signal));
      },
    });
    ctx.effect(() => harness.registerTool(ctx, toolJournal));

    // ---------------- Client 面板 RPC ----------------
    ctx.effect(() => harness.handle('nx.status', async () => ({
      url: state.url,
      connected: state.session !== null,
      session: state.session,
      lastOp: state.lastOp,
      lastError: state.lastError,
      count: state.count,
    })));

    ctx.effect(() => harness.handle('nx.connect', async (args) => {
      const url = String((args && args.url) || '').replace(/\/+$/, '');
      if (!url) return { ok: false, error: '缺少 url' };
      try {
        return await connect(url + '/rpc', String((args && args.token) || ''), undefined);
      } catch (e) {
        state.lastError = String((e && e.message) || e);
        return { ok: false, url, error: state.lastError };
      }
    }));
  },
};
