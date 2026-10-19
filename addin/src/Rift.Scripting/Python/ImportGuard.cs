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

/// <summary>What an assistant's script gets instead of the raw interpreter (ADR-036).</summary>
/// <param name="Refusal">Why a member name is refused (<see cref="PythonPolicy.NameRefusal"/>), or null.</param>
/// <param name="SystemTypes">The façade <c>System</c>: dotted names under <c>System</c> and the .NET type each one is bound to.</param>
public sealed record AgentRestrictions(Func<string, string?> Refusal, IReadOnlyList<KeyValuePair<string, object>> SystemTypes);

/// <summary>
/// ADR-016 / ADR-026 §7: a script gets its own <c>__builtins__</c> whose <c>__import__</c> only lets through a closed subset
/// of the standard library plus the .NET roots <c>System</c> and <c>Autodesk</c> (minus process, network, reflection,
/// interop and threading namespaces), and refuses by name <c>clr</c>, <c>ctypes</c>, <c>subprocess</c>, sockets and the
/// other modules of ADR-016. <c>exec</c>, <c>eval</c> and <c>compile</c> are wrapped so that code built from strings is
/// narrowed like the script itself (<see cref="ExceptNarrower"/>) and inherits the guarded builtins. Imports made by the
/// standard-library modules themselves use the real builtins.
/// <para>
/// ADR-036 (F-R2), for an assistant's script (<see cref="AgentRestrictions"/>): <c>System</c> is a façade built for the run
/// from <see cref="SystemFacade"/> (no files, processes, environment, reflection, threads); a standard-library module is
/// handed over as a copy of its public names, without the functions of <see cref="ModuleDenied"/>; <c>open</c>,
/// <c>input</c>, <c>breakpoint</c>, <c>help</c>, <c>exit</c> and <c>quit</c> are gone; <c>getattr</c>, <c>setattr</c>,
/// <c>delattr</c>, <c>hasattr</c> and <c>vars</c> refuse the names <see cref="PythonPolicy"/> refuses; code built from
/// strings is checked by the policy too. Scripts shipped with the add-in keep the plain guard.
/// </para>
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

    /// <summary>
    /// The façade <c>System</c> of an assistant's script (ADR-036): value types, maths, dates, strings, conversions, enums,
    /// exceptions, delegates for LINQ, collections, LINQ, text, regular expressions and cultures — the Python counterpart of
    /// the C# allow-list (ADR-035). A name the loaded assemblies do not provide is left out.
    /// </summary>
    public static readonly IReadOnlyList<string> SystemFacade = new[]
    {
        "Object", "String", "Char", "Boolean", "Byte", "SByte", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64",
        "Single", "Double", "Decimal", "Math", "MidpointRounding", "DateTime", "DateTimeKind", "DateTimeOffset", "DayOfWeek",
        "TimeSpan", "Guid", "Convert", "BitConverter", "Enum", "Array", "Nullable", "Tuple", "ValueTuple", "Version",
        "StringComparison", "StringComparer", "StringSplitOptions", "Random", "Uri", "Action", "Func", "Predicate",
        "Comparison", "Exception", "ArgumentException", "ArgumentNullException", "ArgumentOutOfRangeException",
        "InvalidOperationException", "NotSupportedException", "NotImplementedException", "FormatException",
        "OverflowException", "KeyNotFoundException",
        "Collections.ArrayList", "Collections.Hashtable", "Collections.IEnumerable", "Collections.IList",
        "Collections.ICollection", "Collections.IDictionary",
        "Collections.Generic.List", "Collections.Generic.Dictionary", "Collections.Generic.HashSet",
        "Collections.Generic.SortedDictionary", "Collections.Generic.SortedList", "Collections.Generic.SortedSet",
        "Collections.Generic.Queue", "Collections.Generic.Stack", "Collections.Generic.LinkedList",
        "Collections.Generic.KeyValuePair", "Collections.Generic.IEnumerable", "Collections.Generic.IList",
        "Collections.Generic.ICollection", "Collections.Generic.IDictionary", "Collections.Generic.IReadOnlyList",
        "Collections.Generic.IReadOnlyCollection", "Collections.Generic.IReadOnlyDictionary", "Collections.Generic.ISet",
        "Collections.Generic.Comparer", "Collections.Generic.EqualityComparer", "Collections.Generic.KeyNotFoundException",
        "Collections.ObjectModel.ReadOnlyCollection", "Collections.ObjectModel.ObservableCollection",
        "Linq.Enumerable",
        "Text.StringBuilder", "Text.Encoding", "Text.NormalizationForm",
        "Text.RegularExpressions.Regex", "Text.RegularExpressions.RegexOptions", "Text.RegularExpressions.Match",
        "Text.RegularExpressions.MatchCollection", "Text.RegularExpressions.Group", "Text.RegularExpressions.Capture",
        "Globalization.CultureInfo", "Globalization.NumberStyles", "Globalization.CompareOptions",
        "Globalization.DateTimeStyles", "Globalization.NumberFormatInfo",
    };

    /// <summary>Functions taken out of the copies of standard-library modules (files, frames, code objects, attribute access by string).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> ModuleDenied = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["codecs"] = new[] { "open", "EncodedFile" },
        ["operator"] = new[] { "attrgetter", "methodcaller" },
        ["traceback"] = new[] { "walk_stack", "walk_tb", "print_stack", "extract_stack", "format_stack" },
        ["types"] = new[] { "FunctionType", "LambdaType", "CodeType", "FrameType", "TracebackType", "MethodType", "CellType" },
    };

    private const string Prelude = """
import builtins as _builtins
import sys as _sys
import types as _types

def build_facade(pairs):
    root = _types.ModuleType('System')
    for dotted, value in pairs:
        parts = dotted.split('.')
        node = root
        for part in parts[:-1]:
            child = node.__dict__.get(part)
            if child is None:
                child = _types.ModuleType(node.__name__ + '.' + part)
                setattr(node, part, child)
            node = child
        setattr(node, parts[-1], value)
    return root

def guarded_builtins(check, narrow, on_refused, refusal=None, facade_pairs=None, denied=None):
    real_import = _builtins.__import__
    real_exec = _builtins.exec
    real_eval = _builtins.eval
    real_compile = _builtins.compile
    real_getattr = _builtins.getattr
    real_setattr = _builtins.setattr
    real_delattr = _builtins.delattr
    real_hasattr = _builtins.hasattr
    real_vars = _builtins.vars
    guarded = dict(_builtins.__dict__)
    agent = refusal is not None
    facade = build_facade(facade_pairs) if agent else None
    copies = {}

    def _copy(module):
        key = id(module)
        if key in copies:
            return copies[key]
        name = real_getattr(module, '__name__', '?')
        copy = _types.ModuleType(name)
        copies[key] = copy
        drop = denied.get(name.split('.')[0], ()) if denied is not None else ()
        for attr in dir(module):
            if attr.startswith('_') and attr not in ('__name__', '__doc__', '__all__'):
                continue
            if attr in drop:
                continue
            try:
                value = real_getattr(module, attr)
            except AttributeError:
                continue
            if isinstance(value, _types.ModuleType):
                value = _copy(value)
            real_setattr(copy, attr, value)
        return copy

    def _facade(name, fromlist):
        node = facade
        for part in name.split('.')[1:]:
            node = node.__dict__.get(part)
            if node is None:
                on_refused(name)
                raise ImportError("'" + name + "' is not part of the System available to RIFT scripts (ADR-036)")
        if fromlist:
            for item in fromlist:
                if item != '*' and item not in node.__dict__:
                    on_refused(name + '.' + str(item))
                    raise ImportError("'" + name + '.' + str(item) + "' is not part of the System available to RIFT scripts (ADR-036)")
            return node
        return facade

    def __import__(name, globals=None, locals=None, fromlist=(), level=0):
        reason = check(name, fromlist, level)
        if reason is None and agent and fromlist:
            for item in fromlist:
                if isinstance(item, str):
                    why = refusal(item)
                    if why is not None:
                        reason = "'" + name + '.' + item + "' is refused in a RIFT script: " + why
                        break
        if reason is not None:
            on_refused(name)
            raise ImportError(reason)
        if agent and name.split('.')[0] == 'System':
            return _facade(name, fromlist)
        module = real_import(name, globals, locals, fromlist, level)
        if agent and name.split('.')[0] != 'Autodesk':
            return _copy(module)
        return module

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

    if agent:
        def _name(name):
            if isinstance(name, str):
                why = refusal(name)
                if why is not None:
                    raise AttributeError("'" + name + "' is refused in a RIFT script: " + why)

        def guarded_getattr(obj, name, *default):
            _name(name)
            return real_getattr(obj, name, *default)

        def guarded_setattr(obj, name, value):
            _name(name)
            return real_setattr(obj, name, value)

        def guarded_delattr(obj, name):
            _name(name)
            return real_delattr(obj, name)

        def guarded_hasattr(obj, name):
            _name(name)
            return real_hasattr(obj, name)

        def guarded_vars(*args):
            found = real_vars(*args)
            return {k: v for k, v in found.items() if not isinstance(k, str) or refusal(k) is None}

        for gone in ('open', 'input', 'breakpoint', 'help', 'exit', 'quit', 'copyright', 'credits', 'license'):
            guarded.pop(gone, None)
        guarded['getattr'] = guarded_getattr
        guarded['setattr'] = guarded_setattr
        guarded['delattr'] = guarded_delattr
        guarded['hasattr'] = guarded_hasattr
        guarded['vars'] = guarded_vars
    return guarded, facade
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
    /// With <paramref name="agent"/>, the script gets the restrictions of ADR-036; the façade <c>System</c> built for this
    /// run is returned (bind it to the script's <c>System</c> variable), null otherwise.
    /// </summary>
    public static object? Install(ScriptScope scope, ScriptEngine engine, Action<string> onRefused, Func<string, string>? narrow = null, AgentRestrictions? agent = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(onRefused);
        object factory = s_factories.GetValue(engine, CreateFactory);
        Func<string?, object?, int, string?> check = Check;
        Func<string, string> narrowFn = narrow ?? (s => ExceptNarrower.Narrow(engine, s));
        Action<string> refused = onRefused;
        object pair = agent is null
            ? engine.Operations.Invoke(factory, check, narrowFn, refused)
            : engine.Operations.Invoke(factory, check, narrowFn, refused, agent.Refusal, FacadePairs(agent.SystemTypes), Denied());
        var both = (IronPython.Runtime.PythonTuple)pair;
        scope.SetVariable("__builtins__", both[0]);
        return agent is null ? null : both[1];
    }

    private static object CreateFactory(ScriptEngine engine)
    {
        ScriptScope setup = engine.CreateScope();
        engine.CreateScriptSourceFromString(Prelude, "<" + Brand.Id + "-import-guard>", SourceCodeKind.File).Execute(setup);
        return setup.GetVariable("guarded_builtins");
    }

    private static IronPython.Runtime.PythonList FacadePairs(IReadOnlyList<KeyValuePair<string, object>> types)
    {
        var list = new IronPython.Runtime.PythonList();
        foreach (KeyValuePair<string, object> kv in types)
        {
            list.Add(new IronPython.Runtime.PythonList { kv.Key, kv.Value });
        }
        return list;
    }

    private static IronPython.Runtime.PythonDictionary Denied()
    {
        var dict = new IronPython.Runtime.PythonDictionary();
        foreach (KeyValuePair<string, string[]> kv in ModuleDenied)
        {
            var names = new IronPython.Runtime.PythonList();
            foreach (string name in kv.Value)
            {
                names.Add(name);
            }
            dict[kv.Key] = names;
        }
        return dict;
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
