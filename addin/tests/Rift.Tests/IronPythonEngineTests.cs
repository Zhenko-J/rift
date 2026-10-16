// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;

using Xunit;

using Rift.Scripting;
using Rift.Scripting.Python;

namespace Rift.Tests;

/// <summary>The engine behaves the same outside Revit: these tests pin the P-D4 findings.</summary>
public sealed class IronPythonEngineTests : IDisposable
{
    private readonly IronPythonEngine _engine = new(Array.Empty<System.Reflection.Assembly>(), null);

    private static ScriptContext Ctx() =>
        new(new object(), new object(), null, null, new JsonObject(), (_, _) => { }, TextWriter.Null, TextWriter.Null);

    private RunResult Run(string code, double seconds)
    {
        CompileResult c = _engine.Compile(code, "<test>");
        Assert.True(c.Ok, c.Message);
        return _engine.Run(c.Script!, Ctx(), TimeSpan.FromSeconds(seconds), CancellationToken.None);
    }

    [Fact]
    public void Trace_hook_fires()
    {
        Assert.True(_engine.CountTraceEvents(compileAfterSetTrace: false) > 0);
        Assert.True(_engine.CountTraceEvents(compileAfterSetTrace: true) > 0);
    }

    [Fact]
    public void Result_and_fstrings_round_trip()
    {
        RunResult r = Run("x = 42\nresult = {'s': f'value={x}', 'n': x, 'u': 'élévation'}\n", 10);
        Assert.Null(r.Error);
        Assert.False(r.TimedOut);
        Assert.Equal("value=42", r.Value!["s"]!.GetValue<string>());
        Assert.Equal(42, r.Value!["n"]!.GetValue<long>());
        Assert.Equal("élévation", r.Value!["u"]!.GetValue<string>());
    }

    [Fact]
    public void Walrus_is_a_syntax_error()
    {
        CompileResult c = _engine.Compile("if (n := 1):\n    pass\n", "<test>");
        Assert.False(c.Ok);
        Assert.Equal(1, c.Line);
    }

    [Fact]
    public void Deadline_cuts_a_long_loop()
    {
        RunResult r = Run("i = 0\nwhile i < 500000000:\n    i += 1\n", 0.5);
        Assert.True(r.TimedOut);
        Assert.True(r.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Deadline_survives_a_bare_except()
    {
        RunResult r = Run("i = 0\nwhile i < 500000000:\n    try:\n        i += 1\n    except:\n        pass\n", 0.5);
        Assert.True(r.TimedOut);
        Assert.True(r.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Deadline_survives_except_BaseException_and_except_Exception()
    {
        RunResult r1 = Run("i = 0\nwhile i < 500000000:\n    try:\n        i += 1\n    except BaseException as e:\n        pass\n", 0.5);
        Assert.True(r1.TimedOut);
        RunResult r2 = Run("i = 0\nwhile i < 500000000:\n    try:\n        i += 1\n    except Exception:\n        pass\n", 0.5);
        Assert.True(r2.TimedOut);
    }

    [Fact]
    public void Deadline_cuts_a_loop_inside_a_function()
    {
        RunResult r = Run("def spin():\n    i = 0\n    while i < 500000000:\n        i += 1\n    return i\nresult = spin()\n", 0.5);
        Assert.True(r.TimedOut);
    }

    [Fact]
    public void Script_errors_are_reported_not_thrown()
    {
        RunResult r = Run("raise ValueError('deliberate')\n", 5);
        Assert.NotNull(r.Error);
        Assert.Contains("deliberate", r.Error!.Message, StringComparison.Ordinal);
        Assert.False(r.TimedOut);
    }

    [Fact]
    public void Bare_except_rewrite_keeps_line_count()
    {
        string src = "try:\n    x = 1\nexcept:\n    pass\ntry:\n    y = 2\nexcept BaseException as e:\n    pass\nexcept Exception:\n    pass\n";
        string rewritten = IronPythonEngine.NarrowBareExcept(src);
        Assert.Equal(src.Split('\n').Length, rewritten.Split('\n').Length);
        Assert.Contains("except Exception:\n    pass\ntry", rewritten, StringComparison.Ordinal);
        Assert.Contains("except Exception as e:", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("BaseException", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_is_refused_without_a_live_trace_hook()
    {
        // Plan test 15a: a neutralised SetTrace gives no event at construction; Run refuses and executes nothing.
        using var dead = new IronPythonEngine(Array.Empty<System.Reflection.Assembly>(), null, BoundedWriter.DefaultMaxBytes, (e, _) => IronPython.Hosting.Python.SetTrace(e, null));
        Assert.False(dead.TimeoutAvailable);
        CompileResult c = dead.Compile("report_progress(1, 'ran')\n", "<test>");
        Assert.True(c.Ok, c.Message);
        bool ran = false;
        var ctx = new ScriptContext(new object(), new object(), null, null, new JsonObject(), (_, _) => ran = true, TextWriter.Null, TextWriter.Null);
        Assert.Throws<TimeoutUnavailableException>(() => dead.Run(c.Script!, ctx, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.False(ran);
    }

    [Fact]
    public void Run_is_refused_when_the_hook_dies_after_start_up()
    {
        // The start-up self-check passes (two counts), then the hook stops firing: the per-run check refuses.
        int installs = 0;
        using var dying = new IronPythonEngine(Array.Empty<System.Reflection.Assembly>(), null, BoundedWriter.DefaultMaxBytes,
            (e, d) => IronPython.Hosting.Python.SetTrace(e, d is not null && ++installs > 2 ? null : d));
        Assert.True(dying.TimeoutAvailable);
        CompileResult c = dying.Compile("report_progress(1, 'ran')\n", "<test>");
        bool ran = false;
        var ctx = new ScriptContext(new object(), new object(), null, null, new JsonObject(), (_, _) => ran = true, TextWriter.Null, TextWriter.Null);
        Assert.Throws<TimeoutUnavailableException>(() => dying.Run(c.Script!, ctx, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.False(ran);
    }

    [Fact]
    public void A_script_cannot_remove_the_trace_hook()
    {
        // Generous timeouts: a cold `import traceback` under tracing is slow when the suite runs classes in parallel.
        RunResult r = Run("import traceback\ntraceback.sys.settrace(None)\n", 60);
        Assert.NotNull(r.Error);
        Assert.Contains("reserved", r.Error!.Message, StringComparison.Ordinal);
        RunResult loop = Run("import traceback\ntry:\n    traceback.sys.settrace(None)\nexcept Exception:\n    pass\ni = 0\nwhile i < 500000000:\n    i += 1\n", 0.5);
        Assert.True(loop.TimedOut);
    }

    [Fact]
    public void Stdout_is_captured_and_bounded()
    {
        using var small = new IronPythonEngine(Array.Empty<System.Reflection.Assembly>(), null, 1024);
        CompileResult c = small.Compile("print('héllo')\nfor i in range(1000):\n    print('x' * 10)\nresult = 1\n", "<test>");
        RunResult r = small.Run(c.Script!, Ctx(), TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Null(r.Error);
        Assert.StartsWith("héllo\n", r.Stdout, StringComparison.Ordinal);
        Assert.True(r.StdoutTruncated);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(r.Stdout) <= 1024);
        Assert.Equal(1, r.Value!.GetValue<long>());
    }

    [Fact]
    public void Two_mebibytes_on_stdout_give_one_mebibyte_and_the_flag()
    {
        RunResult r = Run("s = 'y' * 1024\nfor i in range(2048):\n    print(s, end='')\n", 30);
        Assert.Null(r.Error);
        Assert.True(r.StdoutTruncated);
        Assert.Equal(BoundedWriter.DefaultMaxBytes, System.Text.Encoding.UTF8.GetByteCount(r.Stdout));
    }

    [Fact]
    public void Inputs_are_python_values_and_namespaces_are_bound()
    {
        var ctx = new ScriptContext(new object(), new object(), null, null, JsonNode.Parse("{\"n\": 3, \"names\": [\"a\", \"b\"], \"big\": 9000000000}")!,
            (_, _) => { }, TextWriter.Null, TextWriter.Null)
        {
            Extras = new System.Collections.Generic.Dictionary<string, object?> { ["System"] = new ScriptNamespace("System"), ["twice"] = new Func<int, int>(x => 2 * x) },
        };
        CompileResult c = _engine.Compile("result = {'n': inputs['n'] + 1, 'k': len(inputs['names']), 'big': inputs['big'], 'g': System.Guid.Empty.ToString(), 't': twice(4)}\n", "<test>");
        RunResult r = _engine.Run(c.Script!, ctx, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Null(r.Error);
        Assert.Equal(4, r.Value!["n"]!.GetValue<long>());
        Assert.Equal(2, r.Value!["k"]!.GetValue<long>());
        Assert.Equal(9000000000L, r.Value!["big"]!.GetValue<long>());
        Assert.Equal(Guid.Empty.ToString(), r.Value!["g"]!.GetValue<string>());
        Assert.Equal(8, r.Value!["t"]!.GetValue<long>());
    }

    [Fact]
    public void Runtime_errors_carry_the_script_line_and_strings_are_sanitised()
    {
        RunResult r = Run("x = 1\ny = 2\nraise ValueError('line three')\n", 5);
        Assert.Equal(3, r.Error!.Line);
        RunResult s = Run(@"result = 'a\u202eb\x07c\nd'", 5);
        Assert.Equal("abc\nd", s.Value!.GetValue<string>());
    }

    public void Dispose() => _engine.Dispose();
}
