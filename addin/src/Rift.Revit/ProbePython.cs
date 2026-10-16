// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using Rift.Core;
using Rift.Scripting;
using Rift.Scripting.Python;

namespace Rift.Revit;

/// <summary>
/// J0 prototype P-D4: IronPython 3.4 hosted inside Revit, in the isolated load context, with pyRevit loaded.
/// Safety rule learnt on the first attempt (Revit frozen on `while True`): never run an unbounded script
/// unless the trace hook has been proven to fire in this engine — the timeout depends on it.
/// </summary>
internal static class ProbePython
{
    public static string Run(UIApplication uiapp)
    {
        var report = new StringBuilder();
        string outPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Brand.Id, "proto",
            "P-D4_" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        void Line(string s)
        {
            report.Append(s).Append('\n');
            File.WriteAllText(outPath, report.ToString(), Encoding.UTF8);   // written after every step: survives a freeze
        }

        string baseDir = Path.GetDirectoryName(typeof(ProbePython).Assembly.Location) ?? string.Empty;
        string lib = Path.Combine(baseDir, "lib");
        Line("stdlib: " + (Directory.Exists(lib) ? "present (" + Directory.GetFiles(lib, "*.py").Length.ToString(CultureInfo.InvariantCulture) + " modules)" : "absent → " + lib));

        var sw = Stopwatch.StartNew();
        using var engine = new IronPythonEngine(new[] { typeof(Document).Assembly, typeof(UIDocument).Assembly }, Directory.Exists(lib) ? lib : null);
        Line("engine created in " + sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms, version " + engine.EngineVersion
             + ", alc=" + (System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(IronPythonEngine).Assembly)?.Name ?? "?"));

        // Gate: does the trace hook fire at all? (compiled before vs after SetTrace)
        int before = engine.CountTraceEvents(compileAfterSetTrace: false);
        int after = engine.CountTraceEvents(compileAfterSetTrace: true);
        Line("trace events: compiled-before-settrace=" + before.ToString(CultureInfo.InvariantCulture) + " compiled-after-settrace=" + after.ToString(CultureInfo.InvariantCulture));
        bool traceWorks = before > 0 || after > 0;
        bool lateCompile = before == 0 && after > 0;

        Document? doc = uiapp.ActiveUIDocument?.Document;
        var ctx = new ScriptContext(uiapp, uiapp.Application, uiapp.ActiveUIDocument, doc, new JsonObject(), (_, _) => { }, TextWriter.Null, TextWriter.Null);

        Step(engine, ctx, Line, "import json", "import json, re, math\nresult = {'json': json.dumps({'a': 1}), 'pi': math.pi}\n", TimeSpan.FromSeconds(30), lateCompile);
        Step(engine, ctx, Line, "revit api", "import Autodesk.Revit.DB as DB\nfrom Autodesk.Revit.DB import FilteredElementCollector, Level\n"
            + "n = FilteredElementCollector(doc).OfClass(Level).GetElementCount() if doc is not None else -1\n"
            + "result = {'doc': doc.Title if doc is not None else None, 'levels': n, 'version': app.VersionNumber}\n", TimeSpan.FromSeconds(30), lateCompile);
        Step(engine, ctx, Line, "f-string", "x = 42\nresult = f'value={x}'\n", TimeSpan.FromSeconds(10), lateCompile);
        Step(engine, ctx, Line, "walrus (expect syntax error)", "if (n := 1):\n    pass\nresult = n\n", TimeSpan.FromSeconds(10), lateCompile);
        Step(engine, ctx, Line, "unicode", "result = {'name': u'élévation \U0001F600', 'len': len(u'élévation')}\n", TimeSpan.FromSeconds(10), lateCompile);
        Step(engine, ctx, Line, "import clr (expect refused by the import guard)", "import clr\nresult = 'clr importable'\n", TimeSpan.FromSeconds(10), lateCompile);
        Step(engine, ctx, Line, "api exception", "import Autodesk.Revit.DB as DB\nresult = doc.GetElement(DB.ElementId(999999999)) if doc is not None else None\nraise ValueError('deliberate')\n", TimeSpan.FromSeconds(10), lateCompile);
        Step(engine, ctx, Line, "loop 200k (tracing on, bounded)", "s = 0\nfor i in range(200000):\n    s += i\nresult = s\n", TimeSpan.FromSeconds(120), lateCompile);

        if (traceWorks)
        {
            // Bounded first: ~4 M iterations/s were measured, so 50 M iterations take ~12 s and MUST be cut at the 1 s deadline.
            Step(engine, ctx, Line, "timeout bounded loop (50M iters, 1 s)", "i = 0\nwhile i < 50000000:\n    i += 1\nresult = i\n", TimeSpan.FromSeconds(1), lateCompile);
            Step(engine, ctx, Line, "timeout under bare except (50M iters, 1 s)", "i = 0\nwhile i < 50000000:\n    try:\n        i += 1\n    except:\n        pass\nresult = i\n", TimeSpan.FromSeconds(1), lateCompile);
            Step(engine, ctx, Line, "timeout inside a function (50M iters, 1 s)", "def spin():\n    i = 0\n    while i < 50000000:\n        i += 1\n    return i\nresult = spin()\n", TimeSpan.FromSeconds(1), lateCompile);
        }
        else
        {
            Line("timeout tests SKIPPED: the trace hook never fired, an unbounded script would freeze Revit");
        }

        Line("report: " + outPath);
        return report.ToString();
    }

    private static void Step(IronPythonEngine engine, ScriptContext ctx, Action<string> line, string name, string code, TimeSpan timeout, bool lateCompile)
    {
        var sw = Stopwatch.StartNew();
        CompileResult c = engine.Compile(code, "<probe>");
        if (!c.Ok)
        {
            line(name + ": compile error line " + c.Line.ToString(CultureInfo.InvariantCulture) + ": " + c.Message);
            return;
        }
        CompiledScript script = lateCompile ? new CompiledScript("python", c.Script!.Sha256, code) : c.Script!;
        RunResult r = engine.Run(script, ctx, timeout, CancellationToken.None);
        string outcome = r.TimedOut ? "TIMED OUT" : r.Error is null ? "ok" : "error " + r.Error.Type + ": " + r.Error.Message;
        string value = r.Value is null ? "-" : r.Value.ToJsonString();
        if (value.Length > 120)
        {
            value = value[..120] + "…";
        }
        line(name + ": " + outcome + " in " + sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms → " + value);
    }
}
