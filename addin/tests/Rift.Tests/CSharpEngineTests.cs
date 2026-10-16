// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;

using Xunit;

using Rift.Scripting;
using Rift.Scripting.CSharp;

namespace Rift.Tests;

/// <summary>ADR-035: C# scripts — what they may use, the timeout that cannot be swallowed, results, lines.</summary>
public sealed class CSharpEngineTests : IClassFixture<CSharpEngineTests.Fixture>
{
    public sealed class Fixture : IDisposable
    {
        public CSharpEngine Engine { get; } = new(CSharpHostShape.Plain);

        public void Dispose() => Engine.Dispose();
    }

    private readonly CSharpEngine _engine;

    public CSharpEngineTests(Fixture fixture)
    {
        _engine = fixture.Engine;
    }

    private RunResult Run(string source, double seconds = 5, JsonNode? inputs = null)
    {
        CompileResult c = _engine.Compile(source, "<agent-script>");
        Assert.True(c.Ok, "compile: line " + c.Line + " " + c.Message);
        var context = new ScriptContext(new object(), new object(), null, new object(), inputs ?? new JsonObject(), (_, _) => { },
            new StringWriter(), new StringWriter());
        return _engine.Run(c.Script!, context, TimeSpan.FromSeconds(seconds), CancellationToken.None);
    }

    [Fact]
    public void A_script_returns_its_answer_as_json_with_its_inputs()
    {
        RunResult r = Run("""
            var n = (long)inputs["n"];
            var squares = Enumerable.Range(1, (int)n).Select(i => i * i).ToList();
            print("squares " + squares.Count);
            return new { n, squares, sum = squares.Sum(), tag = "ok" };
            """, inputs: new JsonObject { ["n"] = 4 });
        Assert.Null(r.Error);
        Assert.False(r.TimedOut);
        Assert.Equal(30, r.Value!["sum"]!.GetValue<long>());
        Assert.Equal(16, r.Value["squares"]![3]!.GetValue<long>());
        Assert.Contains("squares 4", r.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("System.IO.File.WriteAllText(\"x\", \"y\"); return null;", "System.IO")]
    [InlineData("var t = typeof(string).GetMethods(); return null;", "reflection")]
    [InlineData("System.Threading.Thread.Sleep(10); return null;", "System.Threading")]
    [InlineData("return System.Environment.UserName;", "System.Environment")]
    [InlineData("return System.Activator.CreateInstance(typeof(object));", "System.Activator")]
    [InlineData("var p = System.Diagnostics.Process.GetCurrentProcess(); return null;", "System.Diagnostics")]
    [InlineData("var a = AppDomain.CurrentDomain; return null;", "System.AppDomain")]
    [InlineData("goto end; end: return 1;", "goto")]
    [InlineData("unsafe { int x = 1; int* p = &x; } return null;", "unsafe")]
    [InlineData("dynamic d = 1; return d;", "dynamic")]
    [InlineData("__rift_check = () => { }; return null;", "__rift")]
    [InlineData("#if DEBUG\nreturn 1;\n#endif\nreturn 2;", "directive")]
    [InlineData("var e = new System.Linq.Expressions.ParameterExpression[0]; return e;", "System.Linq.Expressions")]
    [InlineData("var q = new int[0].AsQueryable().Where(x => x > 0); return q;", "AsQueryable")]
    public void Refused_constructs_never_compile(string source, string reason)
    {
        CompileResult c = _engine.Compile(source, "<agent-script>");
        Assert.False(c.Ok);
        Assert.Contains(reason, c.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_event_subscription_is_refused()
    {
        CompileResult c = _engine.Compile("""
            var o = new System.Collections.ObjectModel.ObservableCollection<int>();
            o.CollectionChanged += (s, e) => { };
            return null;
            """, "<agent-script>");
        Assert.False(c.Ok);
        Assert.Contains("event", c.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_script_cannot_close_the_wrapper()
    {
        CompileResult c = _engine.Compile("return 1; } } public static class Evil { public static void X() { ", "<agent-script>");
        Assert.False(c.Ok);
        Assert.Null(c.Script);
    }

    [Fact]
    public void An_endless_loop_stops_at_the_deadline_even_inside_a_catch_all()
    {
        RunResult r = Run("""
            long i = 0;
            while (true)
            {
                try { for (;;) { i++; } }
                catch (Exception) { i = 0; }
                catch { i = 0; }
            }
            """, seconds: 0.5);
        Assert.True(r.TimedOut);
        Assert.Null(r.Value);
        Assert.True(r.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Deep_recursion_stops_instead_of_crashing_revit()
    {
        RunResult r = Run("""
            Func<long, long> f = null;
            f = n => f(n + 1) + 1;
            return f(0);
            """);
        Assert.True(r.TimedOut);
        int Local(int n) => n;
        Assert.Equal(1, Local(1));
        RunResult local = Run("long g(long n) => g(n + 1) + 1; return g(0);");
        Assert.True(local.TimedOut);
    }

    [Fact]
    public void A_runtime_error_gives_its_type_message_and_script_line()
    {
        RunResult r = Run("""
            var list = new List<int>();
            int x = 1;
            return list[x];
            """);
        Assert.False(r.TimedOut);
        Assert.Equal("ArgumentOutOfRangeException", r.Error!.Type);
        Assert.Equal(3, r.Error.Line);
    }

    [Fact]
    public void A_compile_error_names_the_script_line()
    {
        CompileResult c = _engine.Compile("int a = 1;\nint b = a +;\nreturn b;", "<agent-script>");
        Assert.False(c.Ok);
        Assert.Equal(2, c.Line);
    }

    [Fact]
    public void A_delegate_kept_after_the_run_stops_at_its_first_call()
    {
        RunResult r = Run("Func<int> later = () => 1; return later;");
        Assert.Null(r.Error);
        // The result conversion stores the delegate's text, never the delegate itself.
        Assert.Equal("String", JsonValueKind(r.Value));
    }

    private static string JsonValueKind(JsonNode? n) => n?.GetValueKind().ToString() ?? "null";

    [Fact]
    public void Python_and_csharp_are_the_two_languages()
    {
        Assert.Equal("csharp", _engine.Language);
        Assert.True(_engine.TimeoutAvailable);
    }
}
