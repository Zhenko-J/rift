// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;

using Xunit;

using Rift.Scripting;
using Rift.Scripting.Python;

namespace Rift.Tests;

/// <summary>ADR-036: the script policy of assistant scripts (F-R2: façade System, no open, module copies; F-R1: deferred code).</summary>
public sealed class ScriptPolicyTests : IDisposable
{
    private readonly IronPythonEngine _engine = new(Array.Empty<System.Reflection.Assembly>(), Path.Combine(AppContext.BaseDirectory, "lib"));

    public void Dispose() => _engine.Dispose();

    private ScriptContext Context(IReadOnlyDictionary<string, object?>? extras = null) =>
        new(new FakeApp(), new object(), null, null, new JsonObject(), (_, _) => { }, TextWriter.Null, TextWriter.Null)
        {
            Extras = extras ?? new Dictionary<string, object?> { ["System"] = new ScriptNamespace("System") },
        };

    private RunResult Run(string code, ScriptTrust trust = ScriptTrust.Agent, IReadOnlyDictionary<string, object?>? extras = null)
    {
        CompileResult c = _engine.Compile(code, "<test>", trust);
        Assert.True(c.Ok, c.Message);
        return _engine.Run(c.Script!, Context(extras), TimeSpan.FromSeconds(30), CancellationToken.None);
    }

    private CompileResult Refused(string code)
    {
        CompileResult c = _engine.Compile(code, "<test>");
        Assert.False(c.Ok);
        Assert.Equal("PolicyError", c.ErrorType);
        Assert.Null(c.Script);
        return c;
    }

    // ── F-R1: deferred code ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("def f(s, e):\n    pass\nuiapp.Idling += f\n", "Idling")]
    [InlineData("h = uiapp.DocumentChanged\n", "DocumentChanged")]
    [InlineData("uiapp.PostCommand(None)\n", "PostCommand")]
    [InlineData("app.OpenDocumentFile('C:/x.rvt')\n", "OpenDocumentFile")]
    [InlineData("from Autodesk.Revit.UI import ExternalEvent\n", "ExternalEvent")]
    [InlineData("from Autodesk.Revit.DB import UpdaterRegistry\n", "UpdaterRegistry")]
    [InlineData("x = getattr(uiapp, 'Idling')\n", "Idling")]
    public void Deferred_code_is_refused_before_compilation(string code, string what)
    {
        CompileResult c = Refused(code);
        Assert.Contains(what, c.Message, StringComparison.Ordinal);
        Assert.Contains("RIFT Python scripts", c.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_event_name_built_at_run_time_is_refused_by_getattr()
    {
        RunResult r = Run("name = 'Idl' + 'ing'\nresult = getattr(uiapp, name)\n");
        Assert.NotNull(r.Error);
        Assert.Equal("AttributeError", r.Error!.Type);
        Assert.Contains("Revit events", r.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Event_names_found_by_reflection_are_refused_too()
    {
        using var engine = new IronPythonEngine(Array.Empty<System.Reflection.Assembly>(), Path.Combine(AppContext.BaseDirectory, "lib"),
            ScriptPolicy.WithEvents(new[] { "SomethingNew" }));
        CompileResult c = engine.Compile("x = uiapp.SomethingNew\n", "<test>");
        Assert.False(c.Ok);
        Assert.Contains("SomethingNew", c.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_callback_left_behind_is_stopped_after_the_run_and_does_nothing()
    {
        var sink = new List<int>();
        Func<int>? held = null;
        var extras = new Dictionary<string, object?>
        {
            ["System"] = new ScriptNamespace("System"),
            ["sink"] = sink,
            ["keep"] = new Action<Func<int>>(f => held = f),
        };
        RunResult r = Run("def later():\n    sink.Add(1)\n    return 1\nkeep(later)\nresult = 'kept'\n", extras: extras);
        Assert.Null(r.Error);
        Assert.NotNull(held);

        Exception ex = Assert.ThrowsAny<Exception>(() => held!());
        Assert.Contains("after its run had ended", ex.Message, StringComparison.Ordinal);
        Assert.Empty(sink);
        Assert.Equal(1, _engine.DeferredCodeStops);

        // The next run is not affected by the sentinel.
        RunResult next = Run("result = 1 + 1\n");
        Assert.Null(next.Error);
        Assert.Equal(2, next.Value!.GetValue<long>());
    }

    [Fact]
    public void The_sentinel_reports_where_the_deferred_code_was()
    {
        var seen = new List<string>();
        _engine.DeferredCodeStopped += seen.Add;
        Func<int>? held = null;
        var extras = new Dictionary<string, object?> { ["keep"] = new Action<Func<int>>(f => held = f) };
        Run("keep(lambda: 1)\n", extras: extras);
        Assert.ThrowsAny<Exception>(() => held!());
        Assert.Single(seen);
        Assert.Contains("<agent-script>", seen[0], StringComparison.Ordinal);
    }

    // ── F-R2: files, System, builtins, modules ──────────────────────────────────────────────

    [Fact]
    public void Open_is_not_available()
    {
        RunResult r = Run("result = open\n");
        Assert.NotNull(r.Error);
        Assert.Equal("NameError", r.Error!.Type);
    }

    [Theory]
    [InlineData("import System.IO\n")]
    [InlineData("from System.IO import File\n")]
    [InlineData("from System import Environment\n")]
    [InlineData("from System import Type\n")]
    [InlineData("from System import Activator\n")]
    [InlineData("import System.Diagnostics\n")]
    public void System_imports_outside_the_facade_are_refused(string code)
    {
        RunResult r = Run(code);
        Assert.NotNull(r.Error);
        Assert.Equal("ImportError", r.Error!.Type);
        Assert.NotEmpty(r.RefusedImports);
    }

    [Theory]
    [InlineData("result = System.IO\n")]
    [InlineData("result = System.Environment\n")]
    [InlineData("result = System.AppDomain\n")]
    [InlineData("result = System.Threading\n")]
    public void The_system_variable_is_the_facade(string code)
    {
        RunResult r = Run(code);
        Assert.NotNull(r.Error);
        Assert.Equal("AttributeError", r.Error!.Type);
    }

    [Fact]
    public void The_facade_keeps_what_scripts_need()
    {
        RunResult r = Run("""
from System.Collections.Generic import List, HashSet, Dictionary
from System.Text.RegularExpressions import Regex
import System.Linq
l = List[int]()
l.Add(3)
l.Add(4)
h = HashSet[str]()
h.Add('a')
d = Dictionary[str, int]()
d['k'] = 5
result = {
    'max': System.Math.Max(2, 7),
    'list': l.Count,
    'set': h.Count,
    'dict': d['k'],
    'regex': Regex.IsMatch('A-12', r'^A-\d+$'),
    'guid': len(System.Guid.NewGuid().ToString()),
    'year': System.DateTime(2026, 10, 19).Year,
    'sum': System.Linq.Enumerable.Contains[int](l, 4),
}
""");
        Assert.True(r.Error is null, r.Error?.Message);
        JsonObject v = r.Value!.AsObject();
        Assert.Equal(7, v["max"]!.GetValue<long>());
        Assert.Equal(2, v["list"]!.GetValue<long>());
        Assert.Equal(1, v["set"]!.GetValue<long>());
        Assert.Equal(5, v["dict"]!.GetValue<long>());
        Assert.True(v["regex"]!.GetValue<bool>());
        Assert.Equal(36, v["guid"]!.GetValue<long>());
        Assert.Equal(2026, v["year"]!.GetValue<long>());
        Assert.True(v["sum"]!.GetValue<bool>());
    }

    [Fact]
    public void A_script_cannot_change_the_facade_of_the_next_run()
    {
        Assert.Null(Run("System.Math = None\n").Error);
        RunResult r = Run("result = System.Math.Max(1, 2)\n");
        Assert.Null(r.Error);
        Assert.Equal(2, r.Value!.GetValue<long>());
    }

    [Theory]
    [InlineData("result = (1).GetType().Assembly\n", "reflection")]
    [InlineData("result = uiapp.GetType().GetMethods()\n", "reflection")]
    [InlineData("result = (1).__class__.__mro__\n", "interpreter internals")]
    [InlineData("import json\nresult = json.dumps.__globals__\n", "interpreter internals")]
    [InlineData("result = __builtins__\n", "interpreter internals")]
    [InlineData("result = getattr(1, '__subclasses__')\n", "interpreter internals")]
    public void Reflection_and_interpreter_internals_are_refused(string code, string reason)
    {
        CompileResult c = Refused(code);
        Assert.Contains(reason, c.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dunder_name_built_at_run_time_is_refused_by_getattr()
    {
        RunResult r = Run("name = '__' + 'dict' + '__'\nresult = getattr(1, name)\n");
        Assert.NotNull(r.Error);
        Assert.Equal("AttributeError", r.Error!.Type);
    }

    [Fact]
    public void Modules_are_copies_of_their_public_names()
    {
        RunResult r = Run("""
import json, codecs, operator
names = vars(json)
result = {
    'dumps': json.dumps({'a': 1}),
    'private': len([k for k in names if k.startswith('__') and k not in ('__name__', '__doc__', '__all__')]),
    'codecs_open': hasattr(codecs, 'open'),
    'attrgetter': hasattr(operator, 'attrgetter'),
}
""");
        Assert.True(r.Error is null, r.Error?.Message);
        JsonObject v = r.Value!.AsObject();
        Assert.Equal("{\"a\": 1}", v["dumps"]!.GetValue<string>());
        Assert.Equal(0, v["private"]!.GetValue<long>());
        Assert.False(v["codecs_open"]!.GetValue<bool>());
        Assert.False(v["attrgetter"]!.GetValue<bool>());
    }

    [Fact]
    public void Code_built_from_strings_is_checked_by_the_policy()
    {
        RunResult r = Run("exec('x = (1).__class__.__mro__')\n");
        Assert.NotNull(r.Error);
        Assert.Contains("RIFT Python scripts", r.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Shipped_scripts_keep_the_real_system()
    {
        RunResult r = Run("import System.IO as IO\nresult = IO.Path.GetFileName('C:/a/b.txt')\n", ScriptTrust.Shipped);
        Assert.True(r.Error is null, r.Error?.Message);
        Assert.Equal("b.txt", r.Value!.GetValue<string>());
    }

    [Fact]
    public void Ordinary_scripts_still_run()
    {
        RunResult r = Run("""
class Box:
    def __init__(self, n):
        self.n = n
    def __repr__(self):
        return 'Box(%d)' % self.n
import re, math, collections
c = collections.Counter('abca')
result = [repr(Box(2)), math.floor(2.5), c['a'], bool(re.match(r'\d', '7')), __name__ is not None]
""");
        Assert.True(r.Error is null, r.Error?.Message);
        Assert.Equal("[\"Box(2)\",2,2,true,true]", r.Value!.ToJsonString());
    }

    [Theory]
    [InlineData("PostCommand", "deferred code")]
    [InlineData("OpenDocumentFile", "open, create or copy documents")]
    [InlineData("CopyModel", "open, create or copy documents")]
    [InlineData("Idling", "Revit events")]
    [InlineData("Name", null)]
    public void Shared_reasons_are_the_same_in_both_languages(string name, string? reason)
    {
        string? found = ScriptPolicy.Reason(name, ScriptPolicy.Default);
        if (reason is null)
        {
            Assert.Null(found);
        }
        else
        {
            Assert.Contains(reason, found, StringComparison.Ordinal);
        }
    }

    /// <summary>Stands in for UIApplication: an object with an event, a method and a property.</summary>
    public sealed class FakeApp
    {
        public event EventHandler? Idling;

        public string Name => "fake";

        public void PostCommand(object? id) => Idling?.Invoke(this, EventArgs.Empty);
    }
}
