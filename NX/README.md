# NX Copilot — 基于 NX 2606 的 Design Copilot 替代方案（DeepSeek Harness 为 AI 终端）

用自然语言驱动西门子 NX 2606 完成建模任务的 AI 副驾驶（Copilot）替代方案。以 **DeepSeek Harness (DSH)** 作为 AI 终端（对话、DeepSeek 大模型、工具调用），通过 **NXOpen 桥接服务**与 NX 2606 会话通信：理解设计意图 → 查询模型 → 创建特征 → 生成并执行 NXOpen journal 代码。

> 适用场景：企业已有 NX 授权但未购买西门子 Design Copilot / Industrial Copilot 订阅，或出于数据安全要求不能把设计数据发送到西门子云，希望用 DeepSeek 等自选大模型在本地完成同类能力。

---

## 架构总览

```
┌─────────────────────────────┐         HTTP (JSON-RPC 2.0)          ┌──────────────────────────────┐
│  DeepSeek Harness (AI 终端)   │   POST /rpc  + Bearer Token          │  NX 2606 (另一台机器)            │
│                              │ ───────────────────────────────────▶ │                              │
│  · DSH Web GUI（对话界面）      │                                     │  · NX 会话（journal 运行中）      │
│  · DeepSeek 大模型             │                                     │  · NXBridgeServer.cs          │
│  · 动态 Cordis 插件 "nxcp"     │                                     │    HttpListener (8123)       │
│    - nx_bridge_connect        │ ◀─────────────────────────────────── │    NXOpen 操作分派             │
│    - nx_bridge_call           │         JSON 响应（含编译错误）          │    journal.run 编译执行        │
│    - nx_bridge_journal        │                                     │                              │
│  · 状态面板（Run 卡片）          │                                     │                              │
└─────────────────────────────┘                                     └──────────────────────────────┘
```

- **AI 终端**：DeepSeek Harness（本仓库的 `dsh-plugin/`）。大模型把用户的中文自然语言转化为受控操作（内置特征 API）或 NXOpen journal 代码（自由能力）。
- **桥接服务**：`nx-bridge/journals/NXBridgeServer.cs`。在 NX 2606 会话内以 journal 方式运行的 HTTP 服务，把 JSON-RPC 请求翻译成 NXOpen 调用。所有操作带 Undo 标记、单位换算、异常隔离（任何错误返回 JSON 而不是崩溃 NX）。
- **协议**：JSON-RPC 2.0 over HTTP，详见 [docs/protocol.md](docs/protocol.md)。
- **模拟桥**：`mock-bridge/server.mjs`。无 NX 环境时用 Node 跑同一协议，用于本机端到端联调（本仓库开发/演示都在用它）。

## 快速开始

> **给别人用**：完整的上手流程（克隆 → NX 部署桥 → DSH 加载插件 → 连接建模）见 [docs/usage-guide.md](docs/usage-guide.md)。

### 1) 本机联调（无需 NX）

```powershell
# 终端 A：启动模拟桥
node mock-bridge/server.mjs --port 8123 --token demo

# 终端 B：冒烟测试（可选）
node scripts/smoke.mjs --port 8123 --token demo
```

然后在 DSH Web GUI 中定义并运行插件（见 `dsh-plugin/README.md`），对话中输入：

> 连接 NX 桥 127.0.0.1:8123（token: demo），然后创建一个 100×60×40 的长方体。

### 2) 部署到 NX 2606 机器（真实桥接）

1. 拷贝 `nx-bridge/journals/NXBridgeServer.cs` 到 NX 机器。
2. 打开文件修改顶部常量：`PORT`（默认 8123）、`TOKEN`（**必须设置**，为空则拒绝所有请求）、`PREFIX_HOST`（默认 `+` 监听所有网卡）。
3. NX 中执行：**File → Execute → NX Open…**，选择 `NXBridgeServer.cs`。看到弹窗「NX Copilot 桥接服务已启动」即成功。
4. （可选）非管理员运行 NX 时若启动失败提示权限，执行一次：
   ```powershell
   netsh http add urlacl url=http://+:8123/ user=Everyone
   ```
   并在 Windows 防火墙放行 8123 端口（仅限可信局域网）。
5. 回到 DSH，对话中输入连接命令（IP 为 NX 机器地址）。

### 3) 典型对话示例

| 你说 | 系统行为 |
| --- | --- |
| 连接 192.168.1.50:8123，token abc | `nx_bridge_connect` → 返回 NX 版本、当前部件 |
| 当前模型里有什么？ | `nx_bridge_call model.tree` → 体与特征列表 |
| 在原点建一个 100×60×40 的长方体 | `feature.block`（带 Undo 标记，可 Ctrl+Z） |
| 在 (50,30,20) 处建直径 20 高 30 的圆柱 | `feature.cylinder` |
| 给我建一个法兰盘：外径 120 的圆柱 + 4 个均布孔 | 生成 NXOpen journal → `nx_bridge_journal journal.run` 编译执行；若编译报错，把错误返回给模型自动修复重试 |
| 把刚才那个特征删掉 | `feature.suppress` |

## 仓库结构

```
├── README.md                    # 本文件
├── LICENSE                      # MIT
├── .gitignore
├── docs/
│   ├── architecture.md          # 架构、模块职责、数据流、安全模型
│   ├── protocol.md              # JSON-RPC 2.0 协议规范（方法全表 + 示例）
│   ├── copilot-features.md      # Design Copilot 能力拆解 → 本方案对照与差距
│   └── nx-open-notes.md         # NXOpen 要点与 journal 代码生成准则（给大模型用）
├── nx-bridge/
│   ├── README.md                # NX 侧部署/配置/故障排查
│   └── journals/
│       └── NXBridgeServer.cs    # 单文件 NXOpen journal（核心交付物）
├── mock-bridge/
│   └── server.mjs               # Node 模拟桥（同协议）
├── dsh-plugin/
│   ├── host.js                  # Cordis 插件 Host 半部分（工具集）
│   ├── client.js                # Cordis 插件 Client 半部分（状态面板）
│   └── README.md                # 插件安装/使用说明
├── examples/
│   └── journals/                # 示例：模型生成的 journal 长什么样
└── scripts/
    └── smoke.mjs                # 协议冒烟测试（对任意 bridge 运行）
```

## 与西门子 Design Copilot 的对照

| 能力 | 西门子 Design Copilot | 本方案 |
| --- | --- | --- |
| 自然语言 → 设计意图 | 内置于 NX，云端 Industrial Copilot 推理 | DSH 内 DeepSeek 推理（本地/私有部署可选） |
| 特征创建 | 支持（孔、草图等） | v1 内置 block/cylinder/sphere + journal.run 自由扩展 |
| 模型问答 | 支持 | `model.tree` + `measure.distance` + journal |
| 数据出网 | 发送至西门子云 | 仅局域网 NX↔DSH，可完全不出网 |
| 许可成本 | 需 Design Copilot 订阅 | NX + DSH + DeepSeek API（或本地模型） |
| 可定制 | 黑盒 | 全开源，方法表随意扩展 |

能力差距与路线图见 [docs/copilot-features.md](docs/copilot-features.md)。

## 安全模型

- 桥接服务默认**只监听可信网卡**，`TOKEN` 必填（`Authorization: Bearer`），空 token 直接拒绝。
- 所有写操作包在 NX Undo 标记内，可 Ctrl+Z 回退。
- `journal.run` 会执行任意 NXOpen 代码，等同把建模机器权限交给大模型 —— **只在可信局域网使用**，并建议在生产环境用独立 NX 会话/测试部件先行验证。

## GitHub 分享

本仓库计划发布到 GitHub 并打上 `dsh-plugin` 等 topic。发布步骤见 `dsh-plugin/README.md` 末尾。推荐 topics：

```
dsh-plugin, deepseek-harness, nx, nxopen, siemens-nx, design-copilot, copilot, cad, ai
```

## License

MIT，见 [LICENSE](LICENSE)。
