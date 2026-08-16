# 协议规范 —— NX Copilot Bridge JSON-RPC 2.0 over HTTP

DSH 插件与 NX 桥（`NXBridgeServer.cs` / `mock-bridge/server.mjs`）之间的通信协议。

## 传输

- 端点：`POST http://<host>:<port>/rpc`
- 健康检查：`GET http://<host>:<port>/ping` → `200 "pong"`
- 鉴权：请求头 `Authorization: Bearer <token>`；桥接端 `Token` 为空时跳过校验（不推荐）
- 请求体：JSON-RPC 2.0，上限 4MB；响应 `Content-Type: application/json; charset=utf-8`
- 单位：**一律毫米**（坐标/尺寸）；英制部件由桥自动换算

## 请求 / 响应格式

```jsonc
// 请求
{ "jsonrpc": "2.0", "id": 1, "method": "feature.block", "params": { "origin": [0,0,0], "lengthX": 100 } }

// 成功
{ "jsonrpc": "2.0", "id": 1, "result": { "feature": { "journalId": "BLOCK(3)", "name": "底板" } } }

// 失败（方法内异常 → -32000；鉴权失败 → -32001）
{ "jsonrpc": "2.0", "id": 1, "error": { "code": -32000, "message": "NX 中没有打开的部件…", "detail": "…" } }
```

## 方法表（v1）

### 会话与部件

| 方法 | 参数 | 返回要点 |
| --- | --- | --- |
| `server.ping` | – | `{ pong:true, nxVersion }` |
| `server.stop` | – | `{ stopped:true }`（停止监听并结束 journal） |
| `session.info` | – | `{ nxVersion, processId, server, requests, workPart:{fullPath,name,units,modified}\|null }` |
| `part.open` | `{ file }` | `{ alreadyOpen, part:{…}, loadStatus }` |
| `part.save` | – | `{ saved:true }`（保存所有已修改部件） |
| `part.closeAll` | – | `{ closed:true }` |

### 模型查询

| 方法 | 参数 | 返回要点 |
| --- | --- | --- |
| `model.tree` | – | `{ part:{…}, bodies:[{name,type,solid,faces,edges}], features:[{name,type,journalId}] }` |
| `measure.distance` | `{ p1:[x,y,z], p2:[x,y,z] }` | `{ distance, units:"mm" }`（坐标欧氏距离；面/边测量走 journal.run） |

### 特征创建（全部带 Undo 标记，NX 中可 Ctrl+Z）

| 方法 | 参数 | 返回要点 |
| --- | --- | --- |
| `feature.block` | `{ origin:[x,y,z], lengthX, lengthY, lengthZ, name? }` | `{ feature:{ journalId, name } }` |
| `feature.cylinder` | `{ origin, direction:[dx,dy,dz], diameter, height, name? }` | 同上 |
| `feature.sphere` | `{ origin, diameter, name? }` | 同上 |
| `feature.suppress` | `{ featureId }` | `{ journalId, name, suppressed:true }` |
| `feature.unsuppress` | `{ featureId }` | `{ journalId, name, suppressed:false }` |

### 交互与代码执行

| 方法 | 参数 | 返回要点 |
| --- | --- | --- |
| `ui.message` | `{ text }` | `{ shown:true }`（NX 弹窗） |
| `journal.run` | `{ code }` | `{ ok:true, result }` 或 `{ ok:false, compileErrors:[…] }` / `{ ok:false, runtimeError }` |

### journal.run 代码约定

`code` 是 NXOpen C# **语句序列**，宿主包装为：

```csharp
using System; using System.Collections.Generic; using NXOpen;
public static class NxCopilotJournal {
    public static Session theSession;   // 已预置
    public static UI theUI;             // 已预置
    public static string RESULT;        // 结束时把结果赋给它
    public static void Run() { <code> }
}
```

示例（创建孔 = 圆柱减除）：

```csharp
Session.UndoMarkId mark = theSession.SetUndoMark(Session.MarkVisibility.Visible, "hole");
Part work = theSession.Parts.Work;
var b = work.Features.CreateCylinderBuilder(null);
b.Origin = new Point3d(50, 30, 0);
b.Direction = new Vector3d(0, 0, 1);
b.Diameter.SetFormula("10");
b.Height.SetFormula("20");
b.BooleanOption.Type = NXOpen.Features.Feature.BooleanType.Subtract;
b.BooleanOption.Target = work.Bodies.ToArray()[0];
var nxo = b.Commit();
b.Destroy();
theSession.UpdateManager.DoUpdate(mark);
theSession.DeleteUndoMark(mark, null);
RESULT = "hole created: " + nxo.JournalIdentifier;
```

> `BooleanOption` 等未在内置方法中的能力均可经此通道实现；编译错误会原样返回，
> 大模型可据此修正代码重试。
