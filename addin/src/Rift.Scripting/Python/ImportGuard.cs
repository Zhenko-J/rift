// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using Microsoft.Scripting;
using Microsoft.Scripting.Hosting;

using Rift.Core;

namespace Rift.Scripting.Python;

/// <summary>
/// ADR-016 / ADR-026 §7: a script gets its own <c>__builtins__</c> whose <c>__import__</c> only lets through a closed subset
/// of the standard library plus the .NET roots <c>System</c> and <c>Autodesk</c> (minus process, network, reflection,
/// interop and threading namespaces), and refuses by name <c>clr</c>, <c>ctypes</c>, <c>subprocess</c>, sockets and the
/// other modules of ADR-016. <c>exec</c>, <c>eval</c> and <c>compile</c> are wrapped so that code built from strings is
/// narrowed like the script itself (<see cref="ExceptNarrower"/>) and inherits the guarded builtins. Imports made by the
/// standard-library modules themselves use the real builtins. This is a guard rail, not a security boundary: <c>System</c>
/// is reachable from a script (SECURITY.md).
/// </summary>
public static class ImportGuard
{
    private static readonly string[] s_refused =
    {
        "clr", "ctypes", "_ctypes", "subprocess", "socket", "_socket", "multiprocessing", "shutil",
        "winreg", "msvcrt", "urllib", "http", "ftplib", "smtplib",
    };

    /// <summary>The closed subset: pure data and text modules, no file system, process, network or interpreter access.</summary>
    private static readonly string[] s_allowed =
    {
        "__future__", "abc", "array", "base64", "binascii", "bisect", "calendar", "cmath", "codecs", "collections",
        "contextlib", "copy", "csv", "datetime", "decimal", "difflib", "enum", "fnmatch", "fractions", "functools",
        "hashlib", "heapq", "itertools", "json", "keyword", "math", "numbers", "operator", "pprint", "random", "re",
        "reprlib", "statistics", "string", "struct", "textwrap", "time", "traceback", "types", "typing", "unicodedata",
        "System", "Autodesk",
    };

    private static readonly string[] s_refusedNamespaces =
    {
        "System.Diagnostics", "System.Net", "System.Reflection", "System.Runtime.InteropServices", "System.Runtime.Loader",
        "System.IO.Pipes", "System.Threading", "System.Management", "System.CodeDom", "Microsoft.Win32",
    };

    private const string Prelude = """
import builtins as _builtins
import sys as _sys

def guarded_builtins(check, narrow, on_refused):
    real_import = _builtins.__import__
    real_exec = _builtins.exec
    real_eval = _builtins.eval
    real_compile = _builtins.compile
    guarded = dict(_builtins.__dict__)

    def __import__(name, globals=None, locals=None, fromlist=(), level=0):
        reason = check(name, fromlist, level)
        if reason is not None:
            on_refused(name)
            raise ImportError(reason)
        return real_import(name, globals, locals, fromlist, level)

    def _source(source):
        if isinstance(source, (bytes, bytearray)):
            source = bytes(source).decode('utf-8')
        if isinstance(source, str):
            source = narrow(source)
        return source

    def _scopes(g, l):
        if g is None:
            frame = _sys._getframe(2)
            g = frame.f_globals
            if l is None:
                l = frame.f_locals
        if isinstance(g, dict) and '__builtins__' not in g:
            g['__builtins__'] = guarded
        return g, l

    def exec(source, globals=None, locals=None):
        g, l = _scopes(globals, locals)
        return real_exec(_source(source), g, l)

    def eval(source, globals=None, locals=None):
        g, l = _scopes(globals, locals)
        return real_eval(_source(source), g, l)

    def compile(source, filename, mode, flags=0, dont_inherit=False, optimize=-1):
        return real_compile(_source(source), filename, mode, flags, dont_inherit, optimize)

    guarded['__import__'] = __import__
    guarded['exec'] = exec
    guarded['eval'] = eval
    guarded['compile'] = compile
    return guarded
""";

    private static readonly ConditionalWeakTable<ScriptEngine, object> s_factories = new();

    public static IReadOnlyCollection<string> RefusedModules => s_refused;

    public static IReadOnlyCollection<string> AllowedModules => s_allowed;

    public static IReadOnlyCollection<string> RefusedNamespaces => s_refusedNamespaces;

    /// <summary>
    /// Why <c>import name</c> (with <paramref name="fromlist"/> and <paramref name="level"/>) is refused, or null when it is
    /// allowed. Pure function, also used by tests.
    /// </summary>
    public static string? Check(string? name, object? fromlist, int level)
    {
        if (level != 0)
        {
            return "relative imports are not available in a " + Brand.Product + " script";
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            return "empty module name";
        }
        foreach (string refused in s_refused)
        {
            if (IsSameOrChild(name, refused))
            {
                return "import of '" + name + "' is refused in a " + Brand.Product + " script (ADR-016)";
            }
        }
        string top = name.Split('.')[0];
        if (!s_allowed.Contains(top, StringComparer.Ordinal))
        {
            return "module '" + top + "' is not in the standard-library subset available to " + Brand.Product + " scripts";
        }
        foreach (string full in Names(name, fromlist))
        {
            foreach (string ns in s_refusedNamespaces)
            {
                if (IsSameOrChild(full, ns))
                {
                    return "import of '" + full + "' is refused in a " + Brand.Product + " script (process, network, reflection, interop or threading)";
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Gives <paramref name="scope"/> guarded builtins. <paramref name="onRefused"/> receives the name of every refused
    /// import (the script gets an <c>ImportError</c>); <paramref name="narrow"/> rewrites code passed to exec/eval/compile.
    /// </summary>
    public static void Install(ScriptScope scope, ScriptEngine engine, Action<string> onRefused, Func<string, string>? narrow = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(onRefused);
        object factory = s_factories.GetValue(engine, CreateFactory);
        Func<string?, object?, int, string?> check = Check;
        Func<string, string> narrowFn = narrow ?? (s => ExceptNarrower.Narrow(engine, s));
        Action<string> refused = onRefused;
        object guarded = engine.Operations.Invoke(factory, check, narrowFn, refused);
        scope.SetVariable("__builtins__", guarded);
    }

    private static object CreateFactory(ScriptEngine engine)
    {
        ScriptScope setup = engine.CreateScope();
        engine.CreateScriptSourceFromString(Prelude, "<" + Brand.Id + "-import-guard>", SourceCodeKind.File).Execute(setup);
        return setup.GetVariable("guarded_builtins");
    }

    private static IEnumerable<string> Names(string name, object? fromlist)
    {
        yield return name;
        if (fromlist is null or string)
        {
            if (fromlist is string single && single.Length > 0)
            {
                yield return name + "." + single;
            }
            yield break;
        }
        if (fromlist is IEnumerable items)
        {
            foreach (object? item in items)
            {
                if (item?.ToString() is { Length: > 0 } s && s != "*")
                {
                    yield return name + "." + s;
                }
            }
        }
    }

    private static bool IsSameOrChild(string name, string root) =>
        string.Equals(name, root, StringComparison.Ordinal) || name.StartsWith(root + ".", StringComparison.Ordinal);
}
