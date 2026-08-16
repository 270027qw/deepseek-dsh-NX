# 架构

## 目标

在**不购买西门子 Design Copilot / Industrial Copilot 订阅**、**设计数据不出局域网**的前提下，
为 NX 2606 提供等价的自然语言建模副驾驶，AI 终端使用 DeepSeek Harness。

## 系统组成

```
┌──────────────────────────── DeepSeek Harness (AI 终端) ────────────────────────────┐
│                                                                                     │
│  DSH Web GUI（对话）                                                                │
│    │                                                                                │
│  DeepSeek 大模型（意图理解 / 代码生成 / 错误自纠错）                                    │
│    │                                                                                │
│  Cordis 动态插件 nxcp（本仓库 dsh-plugin/）                                           │
│    ├─ 工具: nx_bridge_connect / nx_bridge_call / nx_bridge_journal                   │
│    ├─ 面板: tool.view.cordis 状态卡片                                                 │
│    └─ 传输: subprocess 服务 → node -e 内联 HTTP 客户端                                 │
│                  │                                                                  │
└────────────────────┼───────────────────────────────────────────────────────────────┘
                     │ HTTP POST /rpc（JSON-RPC 2.0，Bearer Token）
                     ▼
┌──────────────────────────── NX 2606 机器 ────────────────────────────┐
│  NX 会话（journal: NXBridgeServer.cs 运行中）                           │
│    ├─ HttpListener 服务线程（8123）                                     │
│    ├─ 方法分派（串行化 + Undo 标记 + 单位换算 + 异常隔离）                 │
│    ├─ 内置操作: session / part / model / feature / measure / ui        │
│    └─ journal.run: csc.exe 编译 → Assembly.LoadFrom → 进程内执行          │
│                 │                                                      │
│                 ▼                                                      │
│  NXOpen .NET API（.NET Framework 4.8）                                 │
└───────────────────────────────────────────────────────────────────────┘
```

## 模块职责

| 模块 | 职责 | 关键技术点 |
| --- | --- | --- |
| `dsh-plugin/host.js` | 模型可见工具 + Client RPC | `harness.registerTool` / `harness.handle`；`subprocess.spawn` 执行内联 Node HTTP 客户端（避免依赖宿主 fetch/网络模块） |
| `dsh-plugin/client.js` | Run 卡片状态面板 | `tool.view.cordis`（key `self`）；`host.call`；`timer.interval` 轮询 |
| `nx-bridge/journals/NXBridgeServer.cs` | NX 会话内 HTTP 服务 | HttpListener + 极简 JSON 解析器（零外部依赖）；`Session.UpdateManager.DoUpdate` 模型更新 |
| `journal.run` | 任意 NXOpen 代码执行 | `csc.exe` 编译（.NET Framework 自带）→ 反射调用；编译错误回传实现 AI 自纠错 |
| `mock-bridge/server.mjs` | 无 NX 环境联调 | 同协议内存模型 |
| `docs/nx-open-notes.md` | 代码生成准则 | 给大模型的 NXOpen API 备忘录（journal 模板、已知陷阱） |

## 设计决策

1. **智能放在 LLM，桥只做通道与护栏**：内置方法只覆盖高频、低风险的建模原语；
   复杂能力（孔/布尔/草图/阵列）由大模型生成 NXOpen 代码经 `journal.run` 执行 ——
   这正是 Design Copilot「理解意图 → 执行任务」的实现方式，且可验证（编译错误回传）。
2. **协议极简**：JSON-RPC 2.0 over HTTP，单端点；C# 与 Node 两侧都零依赖实现。
3. **安全**：Token 鉴权；`PrefixHost` 限定监听网卡；写操作全部可 Undo；
   `journal.run` 能力等同于把建模权限交给大模型，仅限可信局域网。
4. **可移植**：桥是单文件 journal，部署 = 拷文件 + 改 4 个常量；模拟桥保证 DSH 侧可先行联调。

## 扩展点

- **新内置方法**：在 `Dispatch` 加 case + 实现方法即可（协议文档同步更新）。
- **更安全的代码执行**：生产环境可把 `journal.run` 改为「生成文件 + 人工确认执行」，
  或加方法白名单。
- **双向**：桥可后续增加事件推送（模型变化通知 DSH），协议升级为 JSON-RPC + notify。
- **多机**：一台 DSH 可连多台 NX（工具参数 host/port 覆盖）。
