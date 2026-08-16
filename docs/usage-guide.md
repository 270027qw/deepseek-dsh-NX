# 使用指南 —— 上传 GitHub 后，别人如何上手

本仓库发布到 GitHub（topic: `dsh-plugin`）后，其他使用者按下面四步即可跑通「用 DeepSeek Harness 驱动 NX 2606 建模」。全程无需西门子 Design Copilot 订阅。

## 你需要准备

| 项 | 说明 |
| --- | --- |
| NX 2606 机器 | 已安装 NX 2606 的 Windows 电脑（桥接服务运行在这台机器上） |
| DeepSeek Harness | 已运行的 DSH（Web GUI + DeepSeek 模型），可装在任何能访问 NX 机器的电脑上 |
| Node.js | 仅本机联调（mock 桥）需要；生产只跑 NX 机器上的桥，不需要 Node |

## 第 1 步：克隆仓库

```powershell
git clone https://github.com/<owner>/<repo>.git
```

## 第 2 步：在 NX 2606 机器上启动桥（一次性 ~5 分钟）

1. 把 `nx-bridge/journals/NXBridgeServer.cs` 拷到 NX 机器。
2. 编辑文件顶部 `NXBridgeConfig`：**设置 `Token`**（如 `"my-token"`），确认 `PrefixHost`/`Port`。
3. 打开 NX 与一个部件，菜单 **File → Execute → NX Open…** 选择该文件。
4. 弹窗「NX Copilot 桥接服务已启动」= 成功。
   - 非管理员若报权限：`netsh http add urlacl url=http://+:8123/ user=Everyone`（一次即可）
   - 其他机器连不上：防火墙放行 8123（仅可信局域网）

> 想先不碰 NX 体验？第 2 步可换成本机跑模拟桥：
> `node mock-bridge/server.mjs --port 8123 --token demo`

## 第 3 步：在 DSH 中加载插件（一次性 ~1 分钟）

1. 把本仓库目录作为 DSH 的工作区打开（或把 `dsh-plugin/host.js`、`dsh-plugin/client.js` 放进工作区）。
2. 开一个新会话，对 AI 说下面这句话（AI 会自动读取文件并定义、运行插件）：

   > 用 `dsh-plugin/host.js` 的内容作为 code.host、`dsh-plugin/client.js` 的内容作为 code.client，定义并运行 NX Copilot 插件（idPrefix 用 nxcp）。

3. 看到 `cordis_run` 卡片里的**桥接状态面板**即为成功；若提示审批，点「允许」。

> 提示：动态插件定义在当前会话进程内，DSH 重启后重新说上面那句话即可；源码在仓库里，随时可重建。

## 第 4 步：连接并开始建模

在 DSH 对话框里直接说（也可以直接在 Run 卡片的面板上填地址和 token 点「连接」）：

> 连接 NX 桥 192.168.1.50:8123，token 是 my-token

然后就是自然语言建模：

```
当前模型里有什么？
在原点建一个 100×60×40 的长方体
在 (50,30,40) 建一个直径 20 高 30 的圆柱
给我生成并执行一个法兰盘：外径 120 高 20 的圆柱 + 中心 φ30 通孔 + 4 个均布 φ10 孔
把刚才那个圆柱抑制掉
```

AI 会依次调用 `nx_bridge_connect` / `nx_bridge_call` / `nx_bridge_journal`；
生成代码若有编译错误，AI 会拿到错误信息自动修复重试（在 NX 里每一步都可以 Ctrl+Z 回退）。

## 常见问题（给使用者的速查）

| 问题 | 解决 |
| --- | --- |
| 连接报「无法连接桥接服务」 | 检查 NX 机器 IP/端口/防火墙；桥是否真的在运行（弹窗） |
| 报「unauthorized」 | `Token` 不匹配：桥端 `NXBridgeConfig.Token` 与对话里给的 token 要一致 |
| 报「NX 中没有打开的部件」 | 在 NX 里打开/新建部件，或用 `part.open {file}` |
| 建模没反应 | 看 Run 卡片状态面板的「最近操作 / 最近错误」；错误都会以 JSON 返回给对话 |
| journal.run 报「未找到 csc.exe」 | NX 机器缺 .NET Framework 4.x 编译器；把生成的代码存成 `.cs` 手动 File→Execute |
| 想要别的建模能力（孔/草图/阵列…） | 直接自然语言提需求，AI 会用 journal.run 生成代码实现；能力边界见 `docs/copilot-features.md` |

## 安全提醒（务必阅读）

- `journal.run` 会把建模机器的操作权交给大模型 —— **只连可信局域网**，`Token` 必设。
- 生产环境建议先用测试部件验证模型生成的代码，或后续启用「人工确认执行」模式。
- 桥只监听配置的网卡；不要把 8123 暴露到公网。
