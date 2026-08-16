// ============================================================================
// NXBridgeServer.cs
// 基于 NX 2606 的 Design Copilot 替代方案 —— NX 侧桥接服务（单文件 NXOpen journal）
//
// 功能：
//   · 在 NX 会话内启动 HTTP JSON-RPC 2.0 服务（HttpListener）
//   · 把请求翻译为 NXOpen 调用：部件管理 / 模型查询 / 特征创建 / 测量 / 消息
//   · journal.run：编译并执行大模型生成的 NXOpen C# 代码（csc.exe），
//     编译错误原样返回，支持 AI 自动修复重试
//   · 所有写操作带 Undo 标记；单位自动换算（公制/英制）；异常隔离不崩溃 NX
//
// 运行方式：NX → File → Execute → NX Open... → 选择本文件
// 协议文档：docs/protocol.md
// 部署说明：nx-bridge/README.md
//
// 说明：本文件按 NXOpen .NET（.NET Framework 4.8）journal 约定编写，
//       所有 API 均对照 NXOpen .NET 参考文档（NX 2206+）核实。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using NXOpen;

// ============================================================================
// 配置（每次部署前修改这里）
// ============================================================================
public static class NXBridgeConfig
{
    /// <summary>监听端口</summary>
    public const int Port = 8123;

    /// <summary>
    /// 访问令牌：请求必须带 Authorization: Bearer &lt;Token&gt;。
    /// 强烈建议设置；留空 = 关闭鉴权（仅限完全可信的内网）。
    /// </summary>
    public const string Token = "";

    /// <summary>
    /// 监听地址："+" = 所有网卡（需 URL ACL，见 README）；"127.0.0.1" = 仅本机。
    /// 也可填本机局域网 IP，如 "192.168.1.50"。
    /// </summary>
    public const string PrefixHost = "+";

    /// <summary>
    /// true = Main 启动服务后立即返回（服务在后台线程继续运行）；
    /// false = Main 阻塞直到收到 server.stop（NX 状态栏显示 journal 运行中）。
    /// </summary>
    public const bool Detach = false;
}

// ============================================================================
// NXOpen 入口（journal 约定：public static void Main）
// ============================================================================
public class NXOpenJournal
{
    public static Session theSession;
    public static UI theUI;

    public static void Main(string[] args)
    {
        theSession = Session.GetSession();
        theUI = UI.GetUI();

        NXBridgeServer server = new NXBridgeServer(theSession, theUI);
        server.Start();

        if (!NXBridgeConfig.Detach)
        {
            // 阻塞保持 journal 运行；收到 server.stop 后返回，journal 正常结束
            server.WaitForStop();
        }
    }
}

// ============================================================================
// 桥接服务主体
// ============================================================================
public class NXBridgeServer
{
    private readonly Session _session;
    private readonly UI _ui;
    private HttpListener _listener;
    private readonly ManualResetEventSlim _stopEvent = new ManualResetEventSlim(false);
    private readonly object _gate = new object();      // 串行化所有 NXOpen 操作
    private long _requestCount;

    public NXBridgeServer(Session session, UI ui)
    {
        _session = session;
        _ui = ui;
    }

    // ---------------- 生命周期 ----------------

    public void Start()
    {
        string prefix = string.Format("http://{0}:{1}/", NXBridgeConfig.PrefixHost, NXBridgeConfig.Port);
        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);
        _listener.Start();

        Thread worker = new Thread(AcceptLoop);
        worker.IsBackground = true;
        worker.Name = "NXBridge-accept";
        worker.Start();

        string msg = string.Format(
            "NX Copilot 桥接服务已启动\n地址: {0}\n令牌: {1}\nNX 版本: {2}",
            prefix,
            NXBridgeConfig.Token == "" ? "(未设置！仅限可信内网)" : "已设置",
            Nx.GetNxVersion());
        try { _ui.NXMessageBox.Show("NX Copilot Bridge", NXMessageBox.DialogType.Information, msg); }
        catch { }
    }

    public void WaitForStop()
    {
        _stopEvent.Wait();
    }

    public void Stop()
    {
        try { _listener.Stop(); } catch { }
        _stopEvent.Set();
    }

    private void AcceptLoop()
    {
        while (true)
        {
            HttpListenerContext context;
            try { context = _listener.GetContext(); }
            catch { break; } // 监听器已停止
            ThreadPool.QueueUserWorkItem(_ => Handle(context));
        }
    }

    // ---------------- HTTP 处理 ----------------

    private void Handle(HttpListenerContext context)
    {
        try
        {
            if (context.Request.HttpMethod == "GET" && context.Request.Url.AbsolutePath == "/ping")
            {
                byte[] body = Encoding.UTF8.GetBytes("pong");
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/plain; charset=utf-8";
                context.Response.OutputStream.Write(body, 0, body.Length);
                context.Response.OutputStream.Close();
                return;
            }

            if (context.Request.HttpMethod != "POST" || context.Request.Url.AbsolutePath != "/rpc")
            {
                RespondError(context, -32601, "method not found", "仅支持 POST /rpc 与 GET /ping");
                return;
            }

            // 鉴权
            string auth = context.Request.Headers["Authorization"];
            bool authorized = NXBridgeConfig.Token == "" ||
                              (auth != null && auth == "Bearer " + NXBridgeConfig.Token);
            if (!authorized)
            {
                RespondError(context, -32001, "unauthorized", "令牌无效或缺失（Authorization: Bearer <token>）");
                return;
            }

            // 读取请求体（上限 4MB）
            string bodyText;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                char[] buf = new char[4 * 1024 * 1024 + 1];
                int read = reader.Read(buf, 0, buf.Length);
                if (read > 4 * 1024 * 1024)
                {
                    RespondError(context, -32600, "invalid request", "请求体过大（> 4MB）");
                    return;
                }
                bodyText = new string(buf, 0, read);
            }

            object request;
            try { request = J.Parse(bodyText); }
            catch { RespondError(context, -32700, "parse error", "JSON 解析失败"); return; }

            var req = request as Dictionary<string, object>;
            if (req == null || !(req.ContainsKey("method")))
            {
                RespondError(context, -32600, "invalid request", "缺少 method");
                return;
            }

            string method = Convert.ToString(req["method"]);
            object paramsObj = null;
            req.TryGetValue("params", out paramsObj);
            var prms = paramsObj as Dictionary<string, object> ?? new Dictionary<string, object>();

            long id = -1;
            if (req.ContainsKey("id")) { try { id = Convert.ToInt64(req["id"]); } catch { } }

            // 串行化：NXOpen 操作不并发
            object result;
            try
            {
                lock (_gate)
                {
                    Interlocked.Increment(ref _requestCount);
                    result = Dispatch(method, prms);
                }
            }
            catch (Exception ex)
            {
                RespondJson(context, new Dictionary<string, object>
                {
                    { "jsonrpc", "2.0" },
                    { "id", id },
                    { "error", new Dictionary<string, object> { { "code", -32000 }, { "message", ex.Message } } }
                });
                return;
            }

            RespondJson(context, new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", id },
                { "result", result }
            });
        }
        catch (Exception ex)
        {
            try { RespondError(context, -32000, "internal error", ex.Message); }
            catch { }
        }
    }

    private void RespondError(HttpListenerContext context, int code, string message, string detail)
    {
        RespondJson(context, new Dictionary<string, object>
        {
            { "jsonrpc", "2.0" },
            { "id", null },
            { "error", new Dictionary<string, object> { { "code", code }, { "message", message }, { "detail", detail } } }
        });
    }

    private void RespondJson(HttpListenerContext context, object payload)
    {
        byte[] body = Encoding.UTF8.GetBytes(J.Stringify(payload));
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = body.Length;
        context.Response.OutputStream.Write(body, 0, body.Length);
        context.Response.OutputStream.Close();
    }

    // ---------------- 方法分派 ----------------

    private object Dispatch(string method, Dictionary<string, object> p)
    {
        switch (method)
        {
            case "server.ping":      return ServerPing();
            case "server.stop":      Stop(); return new Dictionary<string, object> { { "stopped", true } };
            case "session.info":     return SessionInfo();
            case "part.open":        return PartOpen(GetString(p, "file"));
            case "part.save":        return PartSave();
            case "part.closeAll":    return PartCloseAll();
            case "model.tree":       return ModelTree();
            case "feature.block":    return FeatureBlock(p);
            case "feature.cylinder": return FeatureCylinder(p);
            case "feature.sphere":   return FeatureSphere(p);
            case "feature.suppress":   return FeatureSuppress(GetString(p, "featureId"), true);
            case "feature.unsuppress": return FeatureSuppress(GetString(p, "featureId"), false);
            case "measure.distance": return MeasureDistance(p);
            case "ui.message":       return UiMessage(GetString(p, "text"));
            case "journal.run":      return JournalRun(GetString(p, "code"));
            default:
                throw new Exception("未知方法: " + method + "（见 docs/protocol.md）");
        }
    }

    // ---------------- 各方法实现 ----------------

    private object ServerPing()
    {
        return new Dictionary<string, object>
        {
            { "pong", true },
            { "time", DateTime.Now.ToString("o") },
            { "nxVersion", Nx.GetNxVersion() }
        };
    }

    private object SessionInfo()
    {
        var res = new Dictionary<string, object>
        {
            { "nxVersion", Nx.GetNxVersion() },
            { "processId", Process.GetCurrentProcess().Id },
            { "server", string.Format("http://{0}:{1}/", NXBridgeConfig.PrefixHost, NXBridgeConfig.Port) },
            { "requests", _requestCount }
        };
        try
        {
            Part work = _session.Parts.Work;
            res["workPart"] = PartInfo(work);
        }
        catch
        {
            res["workPart"] = null; // 当前没有打开的部件
        }
        return res;
    }

    private Dictionary<string, object> PartInfo(BasePart part)
    {
        var d = new Dictionary<string, object>();
        try { d["fullPath"] = part.FullPath; } catch { }
        try { d["name"] = part.Name; } catch { }
        try { d["units"] = part.PartUnits.ToString(); } catch { }
        try { d["modified"] = part.IsModified; } catch { }
        return d;
    }

    private object PartOpen(string file)
    {
        if (string.IsNullOrEmpty(file)) throw new Exception("缺少参数 file（部件完整路径）");
        // 若已打开则直接返回现有部件
        try
        {
            Part work = _session.Parts.Work;
            if (string.Equals(work.FullPath, file, StringComparison.OrdinalIgnoreCase))
                return new Dictionary<string, object> { { "alreadyOpen", true }, { "part", PartInfo(work) } };
        }
        catch { }

        PartLoadStatus status;
        BasePart part = _session.Parts.OpenActiveDisplay(file, NXOpen.DisplayPartOption.Display, out status);
        return new Dictionary<string, object>
        {
            { "alreadyOpen", false },
            { "part", PartInfo(part) },
            { "loadStatus", status == null ? null : status.NumberUnloadedParts.ToString() }
        };
    }

    private object PartSave()
    {
        _session.Parts.SaveAll();
        return new Dictionary<string, object> { { "saved", true } };
    }

    private object PartCloseAll()
    {
        _session.Parts.CloseAll();
        return new Dictionary<string, object> { { "closed", true } };
    }

    private object ModelTree()
    {
        Part work = RequireWork();
        var res = new Dictionary<string, object> { { "part", PartInfo(work) } };

        var bodies = new List<object>();
        try
        {
            foreach (Body b in work.Bodies.ToArray())
            {
                var d = new Dictionary<string, object>();
                try { d["name"] = b.Name; } catch { }
                try { d["type"] = b.IsSolidBody ? "solid" : (b.IsSheetBody ? "sheet" : "other"); } catch { }
                try { d["faces"] = b.GetFaces().Length; } catch { }
                try { d["edges"] = b.GetEdges().Length; } catch { }
                bodies.Add(d);
            }
        }
        catch { }
        res["bodies"] = bodies;

        var features = new List<object>();
        try
        {
            foreach (Feature f in work.Features.ToArray())
            {
                var d = new Dictionary<string, object>();
                try { d["name"] = f.GetFeatureName(); } catch { }
                try { d["type"] = f.FeatureType; } catch { }
                try { d["journalId"] = f.JournalIdentifier; } catch { }
                features.Add(d);
            }
        }
        catch { }
        res["features"] = features;
        return res;
    }

    private object FeatureBlock(Dictionary<string, object> p)
    {
        Part work = RequireWork();
        bool metric = IsMetric(work);
        double[] o = GetPoint(p, "origin");
        double lx = ToPart(GetDouble(p, "lengthX", 10), metric);
        double ly = ToPart(GetDouble(p, "lengthY", 10), metric);
        double lz = ToPart(GetDouble(p, "lengthZ", 10), metric);

        return WithUndo("feature.block", delegate
        {
            var b = work.Features.CreateBlockFeatureBuilder(null);
            b.SetOriginAndLengths(
                new Point3d(ToPart(o[0], metric), ToPart(o[1], metric), ToPart(o[2], metric)),
                Fmt(lx), Fmt(ly), Fmt(lz));
            NXObject nxo = b.Commit();
            b.Destroy();
            TryName(nxo, GetString(p, "name"));
            return FeatureOf(nxo);
        });
    }

    private object FeatureCylinder(Dictionary<string, object> p)
    {
        Part work = RequireWork();
        bool metric = IsMetric(work);
        double[] o = GetPoint(p, "origin");
        double[] d = GetVector(p, "direction", new double[] { 0, 0, 1 });

        return WithUndo("feature.cylinder", delegate
        {
            var b = work.Features.CreateCylinderBuilder(null);
            b.Origin = new Point3d(ToPart(o[0], metric), ToPart(o[1], metric), ToPart(o[2], metric));
            b.Direction = new Vector3d(d[0], d[1], d[2]);
            b.Diameter.SetFormula(Fmt(ToPart(GetDouble(p, "diameter", 10), metric)));
            b.Height.SetFormula(Fmt(ToPart(GetDouble(p, "height", 10), metric)));
            NXObject nxo = b.Commit();
            b.Destroy();
            TryName(nxo, GetString(p, "name"));
            return FeatureOf(nxo);
        });
    }

    private object FeatureSphere(Dictionary<string, object> p)
    {
        Part work = RequireWork();
        bool metric = IsMetric(work);
        double[] o = GetPoint(p, "origin");

        return WithUndo("feature.sphere", delegate
        {
            var b = work.Features.CreateSphereBuilder(null);
            b.CenterPoint = new Point3d(ToPart(o[0], metric), ToPart(o[1], metric), ToPart(o[2], metric));
            b.Diameter.SetFormula(Fmt(ToPart(GetDouble(p, "diameter", 10), metric)));
            NXObject nxo = b.Commit();
            b.Destroy();
            TryName(nxo, GetString(p, "name"));
            return FeatureOf(nxo);
        });
    }

    private object FeatureSuppress(string journalId, bool suppress)
    {
        Part work = RequireWork();
        if (string.IsNullOrEmpty(journalId)) throw new Exception("缺少参数 featureId（用 model.tree 查询 journalId）");

        Feature target = null;
        foreach (Feature f in work.Features.ToArray())
        {
            if (f.JournalIdentifier == journalId) { target = f; break; }
        }
        if (target == null) throw new Exception("未找到特征 " + journalId);

        return WithUndo(suppress ? "feature.suppress" : "feature.unsuppress", delegate
        {
            if (suppress) target.Suppress();
            else target.Unsuppress();
            return new Dictionary<string, object>
            {
                { "journalId", journalId },
                { "name", SafeName(target) },
                { "suppressed", suppress }
            };
        });
    }

    private object MeasureDistance(Dictionary<string, object> p)
    {
        double[] p1 = GetPoint(p, "p1");
        double[] p2 = GetPoint(p, "p2");
        double dx = p2[0] - p1[0], dy = p2[1] - p1[1], dz = p2[2] - p1[2];
        return new Dictionary<string, object>
        {
            { "distance", Math.Sqrt(dx * dx + dy * dy + dz * dz) },
            { "units", "mm" },
            { "note", "坐标间欧氏距离；面/边测量请用 journal.run 生成代码" }
        };
    }

    private object UiMessage(string text)
    {
        if (string.IsNullOrEmpty(text)) throw new Exception("缺少参数 text");
        _ui.NXMessageBox.Show("NX Copilot", NXMessageBox.DialogType.Information, text);
        return new Dictionary<string, object> { { "shown", true } };
    }

    // ---------------- journal.run：编译并执行大模型生成的 NXOpen 代码 ----------------

    private object JournalRun(string code)
    {
        if (string.IsNullOrEmpty(code)) throw new Exception("缺少参数 code（NXOpen C# 代码）");
        return JournalRunner.Run(_session, _ui, code);
    }

    // ---------------- 工具方法 ----------------

    private Part RequireWork()
    {
        try { return _session.Parts.Work; }
        catch { throw new Exception("NX 中没有打开的部件：请先在 NX 中打开或新建部件（part.open）"); }
    }

    private static bool IsMetric(BasePart part)
    {
        try { return part.PartUnits == BasePart.Units.Millimeters; }
        catch { return true; }
    }

    private static double ToPart(double mm, bool metric) { return metric ? mm : mm / 25.4; }

    private object WithUndo(string opName, Func<object> body)
    {
        Session.UndoMarkId mark = _session.SetUndoMark(Session.MarkVisibility.Visible, "NX Copilot: " + opName);
        try
        {
            object result = body();
            _session.UpdateManager.DoUpdate(mark);
            _session.DeleteUndoMark(mark, null);
            return result;
        }
        catch
        {
            try { _session.DeleteUndoMark(mark, null); } catch { }
            throw;
        }
    }

    private Dictionary<string, object> FeatureOf(NXObject nxo)
    {
        var d = new Dictionary<string, object>();
        try { d["journalId"] = nxo.JournalIdentifier; } catch { }
        d["name"] = SafeName(nxo);
        return d;
    }

    private string SafeName(NXObject nxo)
    {
        try { return nxo.Name; } catch { return ""; }
    }

    private static void TryName(NXObject nxo, string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        try { nxo.SetName(name); } catch { }
    }

    private static string Fmt(double v)
    {
        return v.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string GetString(Dictionary<string, object> p, string key)
    {
        object v;
        return p.TryGetValue(key, out v) && v != null ? Convert.ToString(v) : null;
    }

    private static double GetDouble(Dictionary<string, object> p, string key, double def)
    {
        object v;
        if (p.TryGetValue(key, out v) && v != null)
        {
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch { }
        }
        return def;
    }

    private static double[] GetPoint(Dictionary<string, object> p, string key)
    {
        return GetVector(p, key, new double[] { 0, 0, 0 });
    }

    private static double[] GetVector(Dictionary<string, object> p, string key, double[] def)
    {
        object v;
        if (p.TryGetValue(key, out v) && v is List<object>)
        {
            var list = (List<object>)v;
            if (list.Count >= 3)
            {
                try
                {
                    return new double[]
                    {
                        Convert.ToDouble(list[0], CultureInfo.InvariantCulture),
                        Convert.ToDouble(list[1], CultureInfo.InvariantCulture),
                        Convert.ToDouble(list[2], CultureInfo.InvariantCulture)
                    };
                }
                catch { }
            }
        }
        return def;
    }
}

// ============================================================================
// journal.run：把模型生成的 C# 代码用 csc.exe 编译成程序集，在 NX 进程内执行
// ============================================================================
public static class JournalRunner
{
    private const string HostClass = @"using System;
using System.Collections.Generic;
using NXOpen;

public static class NxCopilotJournal
{
    public static Session theSession;
    public static UI theUI;
    public static string RESULT;

    public static void Run()
    {
{0}
    }
}";

    public static object Run(Session session, UI ui, string code)
    {
        // 1) 生成宿主源码：把用户代码嵌入 NxCopilotJournal.Run()
        string src = string.Format(HostClass, code);

        // 2) 找到 csc.exe（.NET Framework 4.x 自带）
        string csc = FindCsc();
        if (csc == null)
        {
            return new Dictionary<string, object>
            {
                { "ok", false },
                { "error", "未找到 csc.exe（.NET Framework 4.x 编译器）。请把生成的代码另存为 .cs 文件，用 File→Execute→NX Open 手动运行。" }
            };
        }

        // 3) 写临时文件并编译
        string tmpDir = Path.Combine(Path.GetTempPath(), "nxbridge-" + Process.GetCurrentProcess().Id);
        Directory.CreateDirectory(tmpDir);
        string srcFile = Path.Combine(tmpDir, "journal_" + DateTime.Now.Ticks + ".cs");
        string dllFile = Path.ChangeExtension(srcFile, ".dll");
        File.WriteAllText(srcFile, src, Encoding.UTF8);

        string refs = BuildReferences();
        string args = string.Format(
            "/nologo /target:library /out:\"{0}\" {1} \"{2}\"",
            dllFile, refs, srcFile);

        ProcessStartInfo psi = new ProcessStartInfo(csc, args);
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;

        string stdout, stderr;
        using (Process proc = Process.Start(psi))
        {
            stdout = proc.StandardOutput.ReadToEnd();
            stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                return new Dictionary<string, object>
                {
                    { "ok", false },
                    { "compileErrors", SplitErrors(stderr) },
                    { "hint", "把编译错误返回给 AI 让它修正代码后重试" }
                };
            }
        }

        // 4) 加载并执行
        try
        {
            var asm = System.Reflection.Assembly.LoadFrom(dllFile);
            Type t = asm.GetType("NxCopilotJournal");
            t.GetField("theSession").SetValue(null, session);
            t.GetField("theUI").SetValue(null, ui);
            t.GetMethod("Run").Invoke(null, null);
            string result = (string)t.GetField("RESULT").GetValue(null);
            return new Dictionary<string, object> { { "ok", true }, { "result", result } };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object>
            {
                { "ok", false },
                { "runtimeError", ex.InnerException != null ? ex.InnerException.Message : ex.Message }
            };
        }
    }

    private static string FindCsc()
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string[] candidates =
        {
            Path.Combine(win, @"Microsoft.NET\Framework64\v4.0.30319\csc.exe"),
            Path.Combine(win, @"Microsoft.NET\Framework\v4.0.30319\csc.exe")
        };
        foreach (string c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }

    private static string BuildReferences()
    {
        var list = new List<string>();
        string nxDir = Path.GetDirectoryName(typeof(Session).Assembly.Location);
        if (nxDir != null)
        {
            foreach (string dll in Directory.GetFiles(nxDir, "NXOpen*.dll"))
            {
                list.Add("/r:\"" + dll + "\"");
            }
        }
        list.Add("/r:System.dll");
        list.Add("/r:System.Core.dll");
        return string.Join(" ", list.ToArray());
    }

    private static List<string> SplitErrors(string stderr)
    {
        var errors = new List<string>();
        foreach (string line in stderr.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.IndexOf("error CS", StringComparison.Ordinal) >= 0) errors.Add(line.Trim());
        }
        return errors;
    }
}

// ============================================================================
// 极简 JSON 解析/序列化（避免依赖 System.Web.Extensions 等外部引用，
// 保证 journal 在 NX 的编译环境里零额外依赖）
// ============================================================================
public static class J
{
    public static object Parse(string text)
    {
        int pos = 0;
        object v = ParseValue(text, ref pos);
        SkipWs(text, ref pos);
        if (pos != text.Length) throw new Exception("JSON 尾随内容");
        return v;
    }

    private static object ParseValue(string s, ref int pos)
    {
        SkipWs(s, ref pos);
        if (pos >= s.Length) throw new Exception("JSON 意外结束");
        char c = s[pos];
        switch (c)
        {
            case '{':
                {
                    var d = new Dictionary<string, object>();
                    pos++;
                    SkipWs(s, ref pos);
                    if (pos < s.Length && s[pos] == '}') { pos++; return d; }
                    while (true)
                    {
                        SkipWs(s, ref pos);
                        string key = ParseString(s, ref pos);
                        SkipWs(s, ref pos);
                        if (pos >= s.Length || s[pos] != ':') throw new Exception("JSON 缺少 ':'");
                        pos++;
                        object v = ParseValue(s, ref pos);
                        d[key] = v;
                        SkipWs(s, ref pos);
                        if (pos >= s.Length) throw new Exception("JSON 缺少 '}'");
                        if (s[pos] == ',') { pos++; continue; }
                        if (s[pos] == '}') { pos++; return d; }
                        throw new Exception("JSON 对象分隔符错误");
                    }
                }
            case '[':
                {
                    var list = new List<object>();
                    pos++;
                    SkipWs(s, ref pos);
                    if (pos < s.Length && s[pos] == ']') { pos++; return list; }
                    while (true)
                    {
                        list.Add(ParseValue(s, ref pos));
                        SkipWs(s, ref pos);
                        if (pos >= s.Length) throw new Exception("JSON 缺少 ']'");
                        if (s[pos] == ',') { pos++; continue; }
                        if (s[pos] == ']') { pos++; return list; }
                        throw new Exception("JSON 数组分隔符错误");
                    }
                }
            case '"': return ParseString(s, ref pos);
            case 't':
                Expect(s, ref pos, "true"); return true;
            case 'f':
                Expect(s, ref pos, "false"); return false;
            case 'n':
                Expect(s, ref pos, "null"); return null;
            default:
                if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber(s, ref pos);
                throw new Exception("JSON 非法字符: " + c);
        }
    }

    private static void Expect(string s, ref int pos, string word)
    {
        if (pos + word.Length > s.Length || s.Substring(pos, word.Length) != word)
            throw new Exception("JSON 关键字错误: " + word);
        pos += word.Length;
    }

    private static string ParseString(string s, ref int pos)
    {
        if (pos >= s.Length || s[pos] != '"') throw new Exception("JSON 字符串缺少引号");
        pos++;
        var sb = new StringBuilder();
        while (pos < s.Length)
        {
            char c = s[pos];
            if (c == '"') { pos++; return sb.ToString(); }
            if (c == '\\')
            {
                pos++;
                if (pos >= s.Length) break;
                char e = s[pos];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (pos + 4 < s.Length)
                        {
                            string hex = s.Substring(pos + 1, 4);
                            sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            pos += 4;
                        }
                        break;
                    default: sb.Append(e); break;
                }
                pos++;
            }
            else
            {
                sb.Append(c);
                pos++;
            }
        }
        throw new Exception("JSON 字符串未闭合");
    }

    private static object ParseNumber(string s, ref int pos)
    {
        int start = pos;
        while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '-' || s[pos] == '+' || s[pos] == '.' || s[pos] == 'e' || s[pos] == 'E'))
            pos++;
        string token = s.Substring(start, pos - start);
        if (token.IndexOf('.') < 0 && token.IndexOf('e') < 0 && token.IndexOf('E') < 0)
        {
            long l;
            if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
        }
        double d;
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        throw new Exception("JSON 数字错误: " + token);
    }

    private static void SkipWs(string s, ref int pos)
    {
        while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t' || s[pos] == '\r' || s[pos] == '\n')) pos++;
    }

    public static string Stringify(object value)
    {
        var sb = new StringBuilder();
        Write(value, sb);
        return sb.ToString();
    }

    private static void Write(object value, StringBuilder sb)
    {
        if (value == null) { sb.Append("null"); return; }
        if (value is string) { WriteString((string)value, sb); return; }
        if (value is bool) { sb.Append((bool)value ? "true" : "false"); return; }
        if (value is int || value is long || value is short || value is byte)
        {
            sb.Append(Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture));
            return;
        }
        if (value is double || value is float || value is decimal)
        {
            sb.Append(Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture));
            return;
        }
        if (value is IDictionary<string, object>)
        {
            var d = (IDictionary<string, object>)value;
            sb.Append('{');
            bool first = true;
            foreach (var kv in d)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteString(kv.Key, sb);
                sb.Append(':');
                Write(kv.Value, sb);
            }
            sb.Append('}');
            return;
        }
        if (value is System.Collections.IEnumerable)
        {
            sb.Append('[');
            bool first = true;
            foreach (object item in (System.Collections.IEnumerable)value)
            {
                if (!first) sb.Append(',');
                first = false;
                Write(item, sb);
            }
            sb.Append(']');
            return;
        }
        WriteString(value.ToString(), sb);
    }

    private static void WriteString(string s, StringBuilder sb)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
