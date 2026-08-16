# NX Copilot — 基于 NX 2606 的 Design Copilot 替代方案

驱动西门子 NX 2606 完成建模任务的 AI 方案。以 **DeepSeek Harness (DSH)** 为终端 ，通过 **NXOpen 桥接服务**与 NX 2606 会话通信。
本代码含大量AI编写 ，如有问题请联系作者或修改

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
Harmess中创建新对话将仓库链接提交至对话框

## 仓库结构
```
├── README.md                    # 本文件
├── LICENSE                      # MIT
├── .gitignore
├── docs/
│   ├── architecture.md          # 架构、模块职责、数据流、安全模型
│   ├── protocol.md              # JSON-RPC 2.0 协议规范
│   ├── copilot-features.md      # Design Copilot
│   └── nx-open-notes.md         # NXOpen 要点与 journal 代码生成准则
├── nx-bridge/
│   ├── README.md                # NX 侧部署/配置/故障排查
│   └── journals/
│       └── NXBridgeServer.cs    # 单文件 NXOpen journal
├── mock-bridge/
│   └── server.mjs               # Node 模拟桥
├── dsh-plugin/
│   ├── host.js                  # Cordis 插件 Host 半部分（工具集）
│   ├── client.js                # Cordis 插件 Client 半部分（状态面板）
├── examples/
│   └── journals/                # 示例
└── scripts/
    └── smoke.mjs                # 协议测试
```
