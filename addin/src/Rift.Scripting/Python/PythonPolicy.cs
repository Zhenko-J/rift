// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using IronPython.Compiler.Ast;

using Microsoft.Scripting.Hosting;

namespace Rift.Scripting.Python;

/// <summary>One construct of a Python script that RIFT refuses (ADR-036), with its line in the script.</summary>
public sealed record PythonViolation(int Line, string What, string Reason);

/// <summary>
/// ADR-036: what an assistant's Python script may not name, checked on IronPython's own syntax tree in the add-in before
/// the script is compiled (the server lint is only a first pass). Refused, as an attribute, a name, an imported name or a
/// string constant: the members of <see cref="ScriptPolicy"/> (Revit events, posted commands, updaters, external events,
/// opening or copying documents: F-R1, F-R14). Refused as an attribute or a name: .NET reflection members
/// (<see cref="ReflectionMembers"/>) and every double-underscore name outside <see cref="AllowedDunders"/> (F-R2). The guarded
/// <c>getattr</c> family applies the same rules at run time to names built from strings (<see cref="ImportGuard"/>).
/// Scripts shipped with the add-in are not checked. A guard rail with a run-time backstop, not a proof: see SECURITY.md.
/// </summary>
public static class PythonPolicy
{
    /// <summary>Reflection on .NET objects reaches files, processes and everything the façade leaves out.</summary>
    public static readonly IReadOnlySet<string> ReflectionMembers = new HashSet<string>(StringComparer.Ordinal)
    {
        "Assembly", "Module", "GetMethod", "GetMethods", "GetField", "GetFields", "GetProperty", "GetProperties",
        "GetMember", "GetMembers", "GetEvent", "GetEvents", "GetConstructor", "GetConstructors", "GetInterface",
        "GetInterfaces", "GetNestedType", "GetNestedTypes", "GetTypeInfo", "GetRuntimeMethod", "GetRuntimeMethods",
        "GetRuntimeProperty", "GetRuntimeProperties", "GetRuntimeField", "GetRuntimeFields", "GetRuntimeEvents",
        "DeclaredMembers", "DeclaredMethods", "DeclaredFields", "DeclaredProperties", "DeclaredConstructors",
        "InvokeMember", "CreateInstance", "CreateDelegate", "DynamicInvoke", "MakeGenericMethod", "MethodHandle",
        "TypeHandle", "FieldHandle", "LoadFrom", "LoadFile", "UnsafeLoadFrom", "ReflectionOnlyLoad",
    };

    /// <summary>Frame, code and generator internals: they lead back to the interpreter's own globals.</summary>
    public static readonly IReadOnlySet<string> InterpreterMembers = new HashSet<string>(StringComparer.Ordinal)
    {
        "f_globals", "f_locals", "f_builtins", "f_back", "f_code", "f_trace", "tb_frame", "tb_next", "gi_frame", "gi_code",
        "cr_frame", "cr_code", "ag_frame", "ag_code", "co_code", "co_consts", "co_names",
    };

    /// <summary>Double-underscore names a script may use: object protocol, operators, context managers, module name.</summary>
    public static readonly IReadOnlySet<string> AllowedDunders = new HashSet<string>(StringComparer.Ordinal)
    {
        "__name__", "__doc__", "__class__", "__module__", "__qualname__", "__all__", "__slots__", "__main__",
        "__init__", "__new__", "__str__", "__repr__", "__format__", "__len__", "__iter__", "__next__", "__reversed__",
        "__enter__", "__exit__", "__call__", "__bool__", "__hash__", "__contains__",
        "__getitem__", "__setitem__", "__delitem__", "__missing__",
        "__eq__", "__ne__", "__lt__", "__le__", "__gt__", "__ge__",
        "__add__", "__radd__", "__sub__", "__rsub__", "__mul__", "__rmul__", "__truediv__", "__rtruediv__",
        "__floordiv__", "__rfloordiv__", "__mod__", "__rmod__", "__pow__", "__neg__", "__pos__", "__abs__",
        "__int__", "__float__", "__round__", "__index__",
    };

    /// <summary>Checks <paramref name="source"/> with <paramref name="engine"/>'s parser; empty when it is allowed or does not parse.</summary>
    public static IReadOnlyList<PythonViolation> Check(ScriptEngine engine, string source, IReadOnlySet<string> refused)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(refused);
        PythonAst? ast = ExceptNarrower.Parse(engine, source);
        if (ast is null)
        {
            return Array.Empty<PythonViolation>();
        }
        var walker = new PolicyWalker(source, refused);
        ast.Walk(walker);
        return walker.Found;
    }

    /// <summary>Why a script may not reach member <paramref name="name"/> (attribute, imported or built name), or null.</summary>
    public static string? NameRefusal(string name, IReadOnlySet<string> refused)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (ScriptPolicy.Reason(name, refused) is { } deferred)
        {
            return deferred;
        }
        if (ReflectionMembers.Contains(name))
        {
            return "reflection is not available to scripts";
        }
        if ((IsDunder(name) && !AllowedDunders.Contains(name)) || InterpreterMembers.Contains(name))
        {
            return "interpreter internals are not available to scripts";
        }
        return null;
    }

    /// <summary>The message of a refused script, shaped like the C# one (ADR-035).</summary>
    public static string Message(IReadOnlyList<PythonViolation> violations)
    {
        ArgumentNullException.ThrowIfNull(violations);
        return "not allowed in RIFT Python scripts: " + string.Join("; ", violations.Take(5).Select(v =>
            "line " + v.Line.ToString(CultureInfo.InvariantCulture) + " " + v.What + " (" + v.Reason + ")"));
    }

    private static bool IsDunder(string name) => name.Length > 4 && name.StartsWith("__", StringComparison.Ordinal) && name.EndsWith("__", StringComparison.Ordinal);

    private sealed class PolicyWalker : PythonWalker
    {
        private readonly string _source;
        private readonly IReadOnlySet<string> _refused;
        private readonly HashSet<(int, string)> _seen = new();

        public PolicyWalker(string source, IReadOnlySet<string> refused)
        {
            _source = source;
            _refused = refused;
        }

        public List<PythonViolation> Found { get; } = new();

        public override bool Walk(MemberExpression node)
        {
            Name(node.StartIndex, node.Name, "." + node.Name);
            return true;
        }

        public override bool Walk(NameExpression node)
        {
            Name(node.StartIndex, node.Name, node.Name);
            return true;
        }

        public override bool Walk(FromImportStatement node)
        {
            foreach (string name in node.Names ?? Array.Empty<string>())
            {
                Name(node.StartIndex, name, "import " + name);
            }
            return true;
        }

        public override bool Walk(ConstantExpression node)
        {
            // getattr(obj, "Idling") and friends: a string that is exactly a refused member name.
            if (node.Value is string s && (ScriptPolicy.Reason(s, _refused) is not null || (IsDunder(s) && !AllowedDunders.Contains(s))))
            {
                Add(node.StartIndex, "'" + s + "'", NameRefusal(s, _refused)!);
            }
            return true;
        }

        private void Name(int start, string? name, string what)
        {
            if (name is not null && NameRefusal(name, _refused) is { } reason)
            {
                Add(start, what, reason);
            }
        }

        private void Add(int start, string what, string reason)
        {
            int line = LineOf(start);
            if (_seen.Add((line, what)))
            {
                Found.Add(new PythonViolation(line, what, reason));
            }
        }

        private int LineOf(int index)
        {
            int line = 1;
            int end = Math.Clamp(index, 0, _source.Length);
            for (int i = 0; i < end; i++)
            {
                if (_source[i] == '\n')
                {
                    line++;
                }
            }
            return line;
        }
    }
}
