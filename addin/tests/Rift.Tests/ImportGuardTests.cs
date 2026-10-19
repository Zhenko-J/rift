// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;

using Xunit;

using Rift.Scripting;
using Rift.Scripting.Python;

namespace Rift.Tests;

/// <summary>Plan M1, test 9: the import guard refuses clr and the ADR-016 list, lets the closed subset through, and reports.</summary>
public sealed class ImportGuardTests : IDisposable
{
    private static readonly string[] s_clr = { "clr" };
    private static readonly string[] s_adr016 =
        { "clr", "ctypes", "_ctypes", "subprocess", "socket", "_socket", "multiprocessing", "shutil", "winreg", "msvcrt", "urllib", "http", "ftplib", "smtplib" };

    private readonly IronPythonEngine _engine = new(Array.Empty<System.Reflection.Assembly>(), Path.Combine(AppContext.BaseDirectory, "lib"));

    private RunResult Run(string code)
    {
        CompileResult c = _engine.Compile(code, "<test>");
        Assert.True(c.Ok, c.Message);
        return _engine.Run(c.Script!, new ScriptContext(new object(), new object(), null, null, new JsonObject(), (_, _) => { }, TextWriter.Null, TextWriter.Null),
            TimeSpan.FromSeconds(30), CancellationToken.None);
    }

    [Theory]
    [InlineData("import clr\n", "clr")]
    [InlineData("import ctypes\n", "ctypes")]
    [InlineData("from subprocess import Popen\n", "subprocess")]
    [InlineData("import os\n", "os")]
    [InlineData("import sys\n", "sys")]
    [InlineData("import importlib\n", "importlib")]
    [InlineData("import builtins\n", "builtins")]
    [InlineData("from System.Diagnostics import Process\n", "System.Diagnostics")]
    [InlineData("from System import Diagnostics\n", "System")]
    [InlineData("exec('import clr')\n", "clr")]
    [InlineData("exec('import socket', {})\n", "socket")]
    public void Refused_imports_raise_import_error_and_are_reported(string code, string reported)
    {
        RunResult r = Run(code);
        Assert.NotNull(r.Error);
        Assert.Equal("ImportError", r.Error!.Type);
        Assert.Contains(reported, r.RefusedImports);
    }

    [Fact]
    public void Import_clr_is_refused_even_when_the_script_catches_the_error()
    {
        RunResult r = Run("try:\n    import clr\n    result = 'imported'\nexcept ImportError as e:\n    result = str(e)\n");
        Assert.Null(r.Error);
        Assert.Contains("refused", r.Value!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(s_clr, r.RefusedImports);
    }

    [Fact]
    public void The_closed_subset_is_importable()
    {
        RunResult r = Run("import json, datetime, math, re, collections, itertools, functools\nfrom System import Guid\n"
            + "result = {'j': json.dumps({'a': 1}), 'd': datetime.date(2026, 10, 13).isoformat(), 'g': str(Guid.Empty)}\n");
        Assert.Null(r.Error);
        Assert.Equal("{\"a\": 1}", r.Value!["j"]!.GetValue<string>());
        Assert.Equal("2026-10-13", r.Value!["d"]!.GetValue<string>());
        Assert.Empty(r.RefusedImports);
    }

    [Theory]
    [InlineData("json", null, 0, true)]
    [InlineData("Autodesk.Revit.DB", null, 0, true)]
    [InlineData("System", "Guid", 0, true)]
    [InlineData("clr", null, 0, false)]
    [InlineData("urllib.request", null, 0, false)]
    [InlineData("System.Net.Http", null, 0, false)]
    [InlineData("System", "Threading", 0, false)]
    [InlineData("json", null, 1, false)]
    [InlineData("shutil", null, 0, false)]
    public void Check_is_a_pure_decision(string name, string? from, int level, bool allowed)
    {
        object? fromlist = from is null ? null : new[] { from };
        Assert.Equal(allowed, ImportGuard.Check(name, fromlist, level) is null);
    }

    [Fact]
    public void Refused_list_matches_adr_016()
    {
        Assert.Equal(s_adr016, ImportGuard.RefusedModules);
    }

    public void Dispose() => _engine.Dispose();
}
