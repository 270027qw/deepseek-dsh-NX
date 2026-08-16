// ============================================================================
// examples/journals/create-flange.cs
// 示例：大模型为「创建法兰盘」任务生成的 journal.run 代码（语句序列）。
// 用法：nx_bridge_journal { name: "法兰盘", code: <本文件语句部分> }
// ============================================================================

// —— 1. 基础体：外径 120、高 20 的圆柱（法兰主体）——
Session.UndoMarkId mark = theSession.SetUndoMark(Session.MarkVisibility.Visible, "flange");
Part work = theSession.Parts.Work;

var body = work.Features.CreateCylinderBuilder(null);
body.Origin = new Point3d(0, 0, 0);
body.Direction = new Vector3d(0, 0, 1);
body.Diameter.SetFormula("120");
body.Height.SetFormula("20");
NXObject flangeNxo = body.Commit();
body.Destroy();

// —— 2. 中心通孔：直径 30 的圆柱布尔减 ——
var centerHole = work.Features.CreateCylinderBuilder(null);
centerHole.Origin = new Point3d(0, 0, -1);
centerHole.Direction = new Vector3d(0, 0, 1);
centerHole.Diameter.SetFormula("30");
centerHole.Height.SetFormula("22");
centerHole.BooleanOption.Type = NXOpen.Features.Feature.BooleanType.Subtract;
centerHole.BooleanOption.Target = flangeNxo as NXOpen.Body;
NXObject centerNxo = centerHole.Commit();
centerHole.Destroy();

// —— 3. 4 个均布安装孔：半径 45，直径 10 ——
string[] holes = new string[] { "45,0", "0,45", "-45,0", "0,-45" };
foreach (string pos in holes)
{
    string[] parts = pos.Split(',');
    double x = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
    double y = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
    var h = work.Features.CreateCylinderBuilder(null);
    h.Origin = new Point3d(x, y, -1);
    h.Direction = new Vector3d(0, 0, 1);
    h.Diameter.SetFormula("10");
    h.Height.SetFormula("22");
    h.BooleanOption.Type = NXOpen.Features.Feature.BooleanType.Subtract;
    h.BooleanOption.Target = flangeNxo as NXOpen.Body;
    NXObject hNxo = h.Commit();
    h.Destroy();
}

// —— 4. 更新模型并返回结果 ——
theSession.UpdateManager.DoUpdate(mark);
theSession.DeleteUndoMark(mark, null);

RESULT = "flange created: " + flangeNxo.JournalIdentifier;
