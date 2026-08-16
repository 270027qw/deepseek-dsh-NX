# NXOpen 代码生成准则（给大模型 / 开发者的备忘录）

`journal.run` 的代码是大模型自由建模的通道。以下是生成 NXOpen C# 代码时的要点，
全部对照 NXOpen .NET 参考文档（NX 2206+，NX 2606 兼容）核实。

## 1. 代码形态

`journal.run` 的 `code` 是**语句序列**，宿主自动包装为：

```csharp
public static class NxCopilotJournal {
    public static Session theSession;   // 预置，直接可用
    public static UI theUI;             // 预置，直接可用
    public static string RESULT;        // 结束时赋结果
    public static void Run() { <code> }
}
```

- **不要**写 `using`、类声明、`Main` —— 只写语句。
- 结束时 `RESULT = "…";`（字符串），桥会把值返回给模型。
- 编译错误会原样返回：**根据错误修正代码重试**（这是自纠错闭环）。

## 2. 已验证 API（NX 2206+，放心使用）

```csharp
Session theSession = Session.GetSession();   // 或直接用预置 theSession
UI theUI = UI.GetUI();                        // 或直接用预置 theUI
Part work = theSession.Parts.Work;            // 当前部件（无部件会抛异常）

// 块（注意：是 CreateBlockFeatureBuilder，不是旧版 CreateBlockBuilder！）
var b = work.Features.CreateBlockFeatureBuilder(null);
b.SetOriginAndLengths(new Point3d(0,0,0), "100", "60", "40"); // 长度/宽度/高度
NXObject nxo = b.Commit();
b.Destroy();

// 圆柱
var c = work.Features.CreateCylinderBuilder(null);
c.Origin = new Point3d(50,30,0);
c.Direction = new Vector3d(0,0,1);
c.Diameter.SetFormula("20");   // 尺寸是 Expression，用 SetFormula("数值字符串")
c.Height.SetFormula("30");
NXObject nxo2 = c.Commit();
c.Destroy();

// 球
var s = work.Features.CreateSphereBuilder(null);
s.CenterPoint = new Point3d(0,0,50);
s.Diameter.SetFormula("15");
NXObject nxo3 = s.Commit();
s.Destroy();

// 命名、更新、撤销标记
nxo.SetName("MyFeature");
theSession.UpdateManager.DoUpdate(mark);   // 模型更新（新版：不是 theSession.Update()！）
theSession.DeleteUndoMark(mark, null);
```

## 3. 通用模式（任何写操作都带 Undo）

```csharp
Session.UndoMarkId mark = theSession.SetUndoMark(Session.MarkVisibility.Visible, "copilot-op");
try {
    // …… 创建/修改特征 ……
    theSession.UpdateManager.DoUpdate(mark);
    theSession.DeleteUndoMark(mark, null);
    RESULT = "ok: " + <标识>;
} catch (Exception ex) {
    theSession.DeleteUndoMark(mark, null);
    RESULT = "error: " + ex.Message;
}
```

## 4. 已知陷阱（API 迁移注意）

| 旧写法（会编译失败） | 新写法（NX 2206+） |
| --- | --- |
| `workPart.Features.CreateBlockBuilder(null)` | `CreateBlockFeatureBuilder(null)` |
| `theSession.Update()` | `theSession.UpdateManager.DoUpdate(mark)` |
| `blockBuilder.LengthX = 100` | `SetOriginAndLengths(origin, "100","60","40")` 或 `SetLength("100")/SetWidth/SetHeight` |
| `builder.Diameter = 10`（Cylinder/Sphere） | `builder.Diameter.SetFormula("10")` |
| 删除特征 `DeleteFeature(...)` | `feature.Suppress()` / `feature.Unsuppress()`（保留历史） |
| 尺寸用 double 直接赋值 | 尺寸是 `Expression`：`SetFormula("10")` 或 `Value` |

## 5. 常用能力速查（均可经 journal.run 使用）

- **孔（布尔减）**：CylinderBuilder + `b.BooleanOption.Type = Feature.BooleanType.Subtract;`
  `b.BooleanOption.Target = work.Bodies.ToArray()[0];`
- **拉伸**：`work.Features.CreateExtrudeBuilder(null)`（需草图/曲线，较复杂）
- **草图**：`NXOpen.Sketch` API；`work.Sketches.CreateSketch(...)` 后 AddLine/AddCircle
- **测量面/边**：`theSession.MeasureManager.NewDistance()`（需选对象）
- **查询体信息**：`body.IsSolidBody` / `body.GetFaces()` / `body.GetEdges()` / `body.Name`
- **遍历特征**：`work.Features.ToArray()` → `f.GetFeatureName()` / `f.FeatureType` / `f.JournalIdentifier`

## 6. 代码风格约定（生成时遵守）

- 坐标/尺寸一律毫米（英制部件桥已自动换算，代码内不再换算）。
- 语句粒度小、带注释；每步都可能抛异常，关键处 try/catch 并写入 RESULT。
- 一次 journal 只做一件事（如「建法兰」），便于编译失败时定位。
- 不要调用 UI 弹窗（`NXMessageBox`）之外的重 UI；避免无限循环。
