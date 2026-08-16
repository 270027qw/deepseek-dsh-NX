# dsh-plugin —— NX Copilot 插件（DeepSeek Harness AI 终端侧）

在 DeepSeek Harness (DSH) 中通过**动态 Cordis 插件**挂载到当前会话的 AI 终端，为模型提供 NX 桥接工具与状态面板。

## 安装（在当前 DSH 会话中）

插件通过 DSH 的 `cordis_define` 工具定义（定义后可在任意新会话通过 `@pluginId` 引用）：

1. 在 DSH 对话框中要求 AI 执行：
   > 用 `dsh-plugin/host.js` 的内容作为 code.host、`dsh-plugin/client.js` 的内容作为 code.client，定义并运行 NX Copilot 插件（idPrefix 用 `nxcp`）。
2. AI 定义并运行后，`cordis_run` 卡片内会出现**桥接状态面板**（连接地址/token/状态），同时模型获得三个新工具：
   - `nx_bridge_connect` — 连接桥并获取会话信息
   - `nx_bridge_call` — 调用桥接方法（模型查询/特征创建/测量…）
   - `nx_bridge_journal` — 把 NXOpen C# 代码交给桥编译执行

> 动态插件是进程级的：DSH 重启后需重新定义（源码在本仓库，随时可重建）。

## 工具用法示例

```
连接 NX 桥 192.168.1.50:8123（token: mytoken）
→ nx_bridge_connect { host: "192.168.1.50", port: 8123, token: "mytoken" }

当前模型里有什么？
→ nx_bridge_call { method: "model.tree" }

在原点建一个 100×60×40 的长方体
→ nx_bridge_call { method: "feature.block", params: { origin: [0,0,0], lengthX: 100, lengthY: 60, lengthZ: 40, name: "底板" } }

给我生成并执行一个法兰盘代码：外径120高20的圆柱，中心4个φ10通孔
→ nx_bridge_journal { name: "法兰盘", code: "<NXOpen C# 语句序列>" }
   （编译错误会返回给模型自动修复重试）
```

## 传输与安全

- 插件经 DSH 的 `subprocess` 服务执行内联 Node HTTP 客户端调用桥接端 `POST /rpc`（JSON-RPC 2.0）。
- 令牌经 `Authorization: Bearer` 传递；桥接端必须配置 `TOKEN`，否则拒绝请求。
- 桥接方法表与参数见 [docs/protocol.md](../docs/protocol.md)；`journal.run` 会执行任意 NXOpen 代码，**仅限可信局域网**使用。

## 发布到 GitHub（dsh-plugin topic）

本仓库整体就是可发布的仓库。发布后把插件说明的链接放在 README 顶部，并在仓库页面添加 topics：

```
dsh-plugin, deepseek-harness, nx, nxopen, siemens-nx, design-copilot, copilot, cad, ai
```

Topics 也可以在 GitHub 网页或 CLI 设置：

```powershell
gh repo edit <owner>/<repo> --add-topic dsh-plugin,deepseek-harness,nx,nxopen,design-copilot,copilot
```

这样其他 DSH 用户搜索 `dsh-plugin` topic 即可发现本项目，并按本文件「安装」一节复用。
