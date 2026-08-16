# NX Copilot — 基于 NX 2606 的 Design Copilot 替代方案（DeepSeek Harness 为 AI 终端）

用自然语言驱动西门子 NX 2606 完成建模任务的 AI 副驾驶（Copilot）替代方案。以 **DeepSeek Harness (DSH)** 作为 AI 终端（对话、DeepSeek 大模型、工具调用），通过 **NXOpen 桥接服务**与 NX 2606 会话通信：理解设计意图 → 查询模型 → 创建特征 → 生成并执行 NXOpen journal 代码。

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


## 快速开始

### 2) 部署到 NX 2606

1.`nx-bridge/journals/NXBridgeServer.cs` 到 NX 机器。
2. 打开文件修改顶部常量：`PORT`（默认 8123）、`TOKEN`（**必须设置**，为空则拒绝所有请求）、`PREFIX_HOST`。
3. NX 中执行：**File → Execute → NX Open…**，选择 `NXBridgeServer.cs`。看到弹窗「NX Copilot 桥接服务已启动」即成功。
4. （可选）非管理员运行 NX 时若启动失败提示权限，执行一次：
   ```powershell
   netsh http add urlacl url=http://+:8123/ user=Everyone
   ```
   并在 Windows 防火墙放行 8123 端口（仅限可信局域网）。
5. 回到 DSH，对话中输入连接命令（IP 为 NX 机器地址）

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
