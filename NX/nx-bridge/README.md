# nx-bridge —— NX 2606 侧桥接服务

在 NX 会话内以 journal 方式运行的 HTTP JSON-RPC 服务，是 DSH（AI 终端）与 NXOpen 之间的桥梁。

## 部署步骤

1. 把 `journals/NXBridgeServer.cs` 拷贝到 NX 2606 机器（任意目录）。
2. 用文本编辑器打开，修改文件顶部的 `NXBridgeConfig` 常量：
   - `Token`：**必须设置**（留空 = 关闭鉴权，不推荐）。
   - `PrefixHost`：默认 `"+"` 监听所有网卡；只允许本机访问可改 `"127.0.0.1"`。
   - `Port`：默认 `8123`。
3. 启动 NX，打开或新建一个部件。
4. NX 菜单 **File → Execute → NX Open…**，选择 `NXBridgeServer.cs`。
5. 看到弹窗「NX Copilot 桥接服务已启动」即成功（含监听地址与 NX 版本）。
   默认 `Detach=false`：journal 保持运行状态（NX 状态栏可见），收到 `server.stop` 请求后自动结束。

## 常见问题

| 现象 | 处理 |
| --- | --- |
| 启动弹窗提示监听失败 / 权限不足 | 以管理员身份在 NX 机器执行一次：`netsh http add urlacl url=http://+:8123/ user=Everyone`，然后重试 |
| 其他机器连不上 | Windows 防火墙放行 8123 端口（仅可信局域网）；确认 `PrefixHost` 不是 `127.0.0.1` |
| 希望服务随 NX 启动自动运行 | 把 journal 放入 NX 启动脚本（如 `startup` 目录或用户默认模板目录）或使用 NX 的「启动时执行 journal」设置 |
| 端口被占用 | 修改 `Port` 并同步修改 DSH 侧连接参数 |
| 操作报「NX 中没有打开的部件」 | 先打开/新建部件，或调用 `part.open {file}` |
| journal.run 报「未找到 csc.exe」 | 该机器缺少 .NET Framework 4.x 编译器；把生成的代码另存 `.cs` 用 File→Execute→NX Open 手动运行 |

## 内置方法（v1）

`session.info`、`part.open`、`part.save`、`part.closeAll`、`model.tree`、
`feature.block`、`feature.cylinder`、`feature.sphere`、`feature.suppress`、`feature.unsuppress`、
`measure.distance`、`ui.message`、`journal.run`、`server.ping`、`server.stop`。

完整参数与示例见 [docs/protocol.md](../docs/protocol.md)。

## 设计要点

- **异常隔离**：每个请求独立 try/catch，错误以 JSON-RPC error 返回，绝不崩溃 NX。
- **可回退**：所有写操作包在 Undo 标记内，NX 中 Ctrl+Z 可回退。
- **单位换算**：参数/返回值一律毫米，英制部件自动换算。
- **journal.run**：用 `csc.exe`（.NET Framework 自带）把模型生成的代码编译为程序集并在 NX 进程内执行，
  编译错误逐条返回 —— 这是「自由建模能力」的通道，也是 AI 自纠错的闭环。
- **零额外依赖**：内置极简 JSON 解析器，不引用 System.Web.Extensions 等程序集，
  保证在 NX 的 journal 编译环境（.NET Framework 4.8 + NXOpen 引用）下可直接编译。
