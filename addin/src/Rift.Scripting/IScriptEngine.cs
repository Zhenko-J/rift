// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;

namespace Rift.Scripting;

/// <summary>
/// A script engine hosted by the add-in (ADR-016). Implementations: IronPython 3.4 (v1), Roslyn C# (v1.x).
/// The context is Revit-agnostic on purpose: this assembly never references the Revit API.
/// </summary>
public interface IScriptEngine : IDisposable
{
    /// <summary>"python" or "csharp".</summary>
    string Language { get; }

    /// <summary>
    /// False when the cooperative timeout cannot work in this engine (ADR-016, ADR-026 §7): <see cref="Run"/> then refuses
    /// every script with <see cref="TimeoutUnavailableException"/>.
    /// </summary>
    bool TimeoutAvailable { get; }

    /// <summary>Compiles without running. Safe to call outside the Revit API thread (ground truth of the server lint).</summary>
    CompileResult Compile(string source, string fileName);

    /// <summary>
    /// Runs a compiled script. Must be called on the Revit API thread, inside the run guard. Throws
    /// <see cref="TimeoutUnavailableException"/> before executing anything when the timeout cannot be enforced.
    /// </summary>
    RunResult Run(CompiledScript script, ScriptContext context, TimeSpan timeout, CancellationToken cancellation);
}

public sealed record CompiledScript(string Language, string Sha256, object Handle);

public sealed record CompileResult(bool Ok, CompiledScript? Script, int Line, int Column, string? Message);

/// <summary>A namespace or module the engine imports itself and binds to a script variable (<c>DB</c>, <c>UI</c>, <c>System</c>).</summary>
public sealed record ScriptNamespace(string Name);

/// <summary>Converts a host object (an Element, an XYZ…) to JSON; returns false when it does not know the type.</summary>
public delegate bool ForeignConverter(object value, out JsonNode? node);

/// <summary>
/// What a script sees (ADR-015): <c>uiapp, app, uidoc, doc, inputs, report_progress</c> plus <see cref="Extras"/>
/// (<c>DB, UI, System, eid</c>; a <see cref="ScriptNamespace"/> value is imported by the engine). <see cref="Stdout"/> and
/// <see cref="Stderr"/> receive what the engine keeps of the script output, which is bounded (<see cref="BoundedWriter"/>).
/// </summary>
public sealed record ScriptContext(
    object UiApp,
    object App,
    object? UiDoc,
    object? Doc,
    JsonNode Inputs,
    Action<int, string> ReportProgress,
    TextWriter Stdout,
    TextWriter Stderr)
{
    public IReadOnlyDictionary<string, object?> Extras { get; init; } = new Dictionary<string, object?>();

    /// <summary>Host-specific result conversion (Revit objects, ADR-015); tried before the generic rules.</summary>
    public ForeignConverter? Converter { get; init; }
}

public sealed record ScriptError(string Type, string Message, string? Traceback, Exception? Inner)
{
    /// <summary>Line of the script where the error was raised, when known.</summary>
    public int? Line { get; init; }
}

public sealed record RunResult(JsonNode? Value, string Stdout, string Stderr, ScriptError? Error, bool TimedOut, TimeSpan Elapsed)
{
    public bool StdoutTruncated { get; init; }

    public bool StderrTruncated { get; init; }

    /// <summary>Modules the import guard refused during the run (ADR-016).</summary>
    public IReadOnlyList<string> RefusedImports { get; init; } = Array.Empty<string>();

    /// <summary>Top-level values left in the script namespace (open transactions are looked for there, ADR-007).</summary>
    public IReadOnlyList<object> Leftovers { get; init; } = Array.Empty<object>();
}

/// <summary>
/// Thrown by <see cref="IScriptEngine.Run"/> when the trace hook that enforces the cooperative timeout does not deliver
/// events (ADR-016, ADR-026 §7). Nothing of the script has run. Reported as <c>TIMEOUT_UNAVAILABLE</c>.
/// </summary>
public sealed class TimeoutUnavailableException : InvalidOperationException
{
    public TimeoutUnavailableException()
        : base("the cooperative timeout is unavailable in this engine: scripts are refused")
    {
    }

    public TimeoutUnavailableException(string message)
        : base(message)
    {
    }

    public TimeoutUnavailableException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
