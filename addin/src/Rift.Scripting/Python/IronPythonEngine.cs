// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

using IronPython.Hosting;
using IronPython.Runtime.Exceptions;

using Microsoft.Scripting;
using Microsoft.Scripting.Hosting;
using Microsoft.Scripting.Runtime;

using Rift.Core;

namespace Rift.Scripting.Python;

/// <summary>
/// Embedded IronPython 3.4 engine (ADR-016). One engine per add-in; one run at a time (the Revit API thread).
/// <para>
/// Timeout invariant (ADR-026 §7), enforced here and not by the callers: the cooperative timeout is a trace hook that keeps
/// raising <c>KeyboardInterrupt</c> once the deadline has passed. The hook only fires in non-adaptive compilation (P-D4: with
/// the default options a <c>while True</c> froze Revit), so the engine is always created with <c>NoAdaptiveCompilation</c>
/// and <c>FullFrames</c>, counts trace events at construction (<see cref="TimeoutAvailable"/>), and checks again at the
/// start of every run, on the run's thread, that the hook it just installed delivers events. When either check fails,
/// <see cref="Run"/> throws <see cref="TimeoutUnavailableException"/> before any line of the script executes.
/// <c>sys.settrace</c> is replaced so that a script cannot remove the hook.
/// </para>
/// <para>
/// ADR-036: an assistant's script (<see cref="ScriptTrust.Agent"/>) is checked by <see cref="PythonPolicy"/> before it is
/// compiled and runs with the restrictions of <see cref="ImportGuard"/> (façade <c>System</c>, no <c>open</c>, copies of
/// modules, guarded <c>getattr</c>). Between two runs a sentinel trace hook stays installed on the run thread: Python code
/// that runs there outside a run — a handler or callback a script left behind (F-R1) — is stopped at its first line and
/// counted (<see cref="DeferredCodeStops"/>, <see cref="DeferredCodeStopped"/>). The C# engine does the same with its
/// checkpoints (ADR-035 §3).
/// </para>
/// </summary>
public sealed class IronPythonEngine : IScriptEngine
{
    private const string ScriptFileName = "<agent-script>";
    private const string LiveProbe = "_n = 0\nfor _i in range(3):\n    _n += _i\n";
    private const string TraceSelfCheck = "s = 0\nfor i in range(50):\n    s += i\ndef f(n):\n    return n + 1\nf(1)\n";
    private const string SysLock = """
import sys as _sys
def _refuse_settrace(*args, **kwargs):
    raise RuntimeError("RIFT: sys.settrace is reserved for the cooperative timeout")
_sys.settrace = _refuse_settrace
_sys.setprofile = _refuse_settrace
""";

    private readonly ScriptEngine _engine;
    private readonly object _runLock = new();
    private readonly Action<ScriptEngine, TracebackDelegate?> _setTrace;
    private readonly int _maxOutputBytes;
    private readonly Dictionary<string, object?> _namespaces = new(StringComparer.Ordinal);
    private readonly IReadOnlySet<string> _refused;
    private readonly TracebackDelegate _betweenRuns;
    private List<KeyValuePair<string, object>>? _facadeTypes;
    private CompiledCode? _liveProbe;
    private volatile bool _running;
    private int _deferredStops;

    public IronPythonEngine(IEnumerable<Assembly> assembliesToExpose, string? stdLibPath, int maxOutputBytes = BoundedWriter.DefaultMaxBytes)
        : this(assembliesToExpose, stdLibPath, maxOutputBytes, null, null)
    {
    }

    /// <param name="refusedMembers">Member names refused in assistant scripts (ADR-036); <see cref="ScriptPolicy.Default"/> when null.</param>
    public IronPythonEngine(IEnumerable<Assembly> assembliesToExpose, string? stdLibPath, IReadOnlySet<string>? refusedMembers)
        : this(assembliesToExpose, stdLibPath, BoundedWriter.DefaultMaxBytes, null, refusedMembers)
    {
    }

    /// <param name="setTrace">Test seam: how the trace hook is installed. A neutralised hook must make every run refused.</param>
    internal IronPythonEngine(IEnumerable<Assembly> assembliesToExpose, string? stdLibPath, int maxOutputBytes, Action<ScriptEngine, TracebackDelegate?>? setTrace,
        IReadOnlySet<string>? refusedMembers = null)
    {
        _refused = refusedMembers ?? ScriptPolicy.Default;
        _betweenRuns = BetweenRuns;
        var options = new Dictionary<string, object>
        {
            ["Tracing"] = true,
            ["Frames"] = true,
            ["FullFrames"] = true,
            // The interpreter path does not emit trace points: force compilation so the trace hook (our timeout) fires.
            ["NoAdaptiveCompilation"] = true,
            ["RecursionLimit"] = 1000,
            ["IgnoreEnvironment"] = true,
            ["NoSite"] = true,
        };
        if (!string.IsNullOrEmpty(stdLibPath))
        {
            options["SearchPaths"] = new[] { stdLibPath };
        }
        _engine = IronPython.Hosting.Python.CreateEngine(options);
        _setTrace = setTrace ?? ((e, d) => e.SetTrace(d));
        _maxOutputBytes = maxOutputBytes;
        _engine.Runtime.LoadAssembly(typeof(object).Assembly);
        // The assemblies behind the façade System (ADR-036): collections, LINQ, regular expressions.
        _engine.Runtime.LoadAssembly(typeof(HashSet<>).Assembly);
        _engine.Runtime.LoadAssembly(typeof(System.Linq.Enumerable).Assembly);
        _engine.Runtime.LoadAssembly(typeof(System.Text.RegularExpressions.Regex).Assembly);
        _engine.Runtime.LoadAssembly(typeof(System.Collections.ObjectModel.ObservableCollection<>).Assembly);
        foreach (Assembly asm in assembliesToExpose)
        {
            _engine.Runtime.LoadAssembly(asm);
        }
        _engine.CreateScriptSourceFromString(SysLock, "<" + Brand.Id + "-sys>", SourceCodeKind.File).Execute(_engine.CreateScope());

        TraceEventsCompiledBefore = CountTraceEvents(compileAfterSetTrace: false);
        TraceEventsCompiledAfter = CountTraceEvents(compileAfterSetTrace: true);
        TimeoutAvailable = TraceEventsCompiledBefore > 0 || TraceEventsCompiledAfter > 0;
        // P-D4 variant: when only code compiled under an active hook is traced, scripts are compiled at run time.
        LateCompile = TraceEventsCompiledBefore == 0 && TraceEventsCompiledAfter > 0;
    }

    public string Language => "python";

    public string EngineVersion => _engine.LanguageVersion.ToString();

    public bool TimeoutAvailable { get; }

    public bool LateCompile { get; }

    public int TraceEventsCompiledBefore { get; }

    public int TraceEventsCompiledAfter { get; }

    /// <summary>How many times Python code left behind by a script was stopped outside a run (ADR-036, F-R1).</summary>
    public int DeferredCodeStops => Volatile.Read(ref _deferredStops);

    /// <summary>Raised on the run thread each time the sentinel stops deferred code, with the code's file and line.</summary>
    public event Action<string>? DeferredCodeStopped;

    /// <summary>The hosted DLR engine (the import guard and the narrower use its parser).</summary>
    internal ScriptEngine Engine => _engine;

    /// <summary>
    /// Counts the trace events delivered for a short, finite loop. Returns 0 when the trace hook never fires — in that case
    /// the cooperative timeout cannot work and <see cref="Run"/> refuses scripts.
    /// </summary>
    public int CountTraceEvents(bool compileAfterSetTrace)
    {
        int events = 0;
        TracebackDelegate? Counter(TraceBackFrame frame, string result, object payload)
        {
            events++;
            return Counter;
        }
        lock (_runLock)
        {
            try
            {
                CompiledCode? code = compileAfterSetTrace ? null : _engine.CreateScriptSourceFromString(TraceSelfCheck, "<trace-probe>", SourceCodeKind.Statements).Compile();
                _setTrace(_engine, Counter);
                code ??= _engine.CreateScriptSourceFromString(TraceSelfCheck, "<trace-probe>", SourceCodeKind.Statements).Compile();
                code.Execute(_engine.CreateScope());
            }
            finally
            {
                _setTrace(_engine, null);
            }
        }
        return events;
    }

    /// <summary>Kept for the P-D4 probe and older callers: the AST-based <see cref="ExceptNarrower"/>.</summary>
    public static string NarrowBareExcept(string source) => ExceptNarrower.Narrow(source);

    public CompileResult Compile(string source, string fileName) => Compile(source, fileName, ScriptTrust.Agent);

    public CompileResult Compile(string source, string fileName, ScriptTrust trust)
    {
        ArgumentNullException.ThrowIfNull(source);
        string sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        if (trust == ScriptTrust.Agent)
        {
            IReadOnlyList<PythonViolation> violations = PythonPolicy.Check(_engine, source, _refused);
            if (violations.Count > 0)
            {
                return new CompileResult(false, null, violations[0].Line, 0, PythonPolicy.Message(violations)) { ErrorType = "PolicyError" };
            }
        }
        try
        {
            string narrowed = ExceptNarrower.Narrow(_engine, source);
            ScriptSource src = _engine.CreateScriptSourceFromString(narrowed, ScriptFileName, SourceCodeKind.Statements);
            CompiledCode code = src.Compile();
            // Under late compilation the run recompiles the narrowed text with the hook active.
            object handle = LateCompile ? narrowed : code;
            return new CompileResult(true, new CompiledScript(Language, sha, handle) { Trust = trust }, 0, 0, null);
        }
        catch (SyntaxErrorException ex)
        {
            return new CompileResult(false, null, ex.Line, ex.Column, ex.Message);
        }
    }

    public RunResult Run(CompiledScript script, ScriptContext context, TimeSpan timeout, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(context);
        if (script.Handle is not CompiledCode and not string)
        {
            throw new ArgumentException("not an IronPython script", nameof(script));
        }
        if (!TimeoutAvailable)
        {
            throw new TimeoutUnavailableException("the trace hook delivered no event at start-up: scripts are refused (TIMEOUT_UNAVAILABLE)");
        }
        lock (_runLock)
        {
            _running = true;
            try
            {
                return RunLocked(script, context, timeout, cancellation);
            }
            finally
            {
                _running = false;
                // ADR-036: what the script left behind cannot run on this thread once the run is over.
                _setTrace(_engine, _betweenRuns);
            }
        }
    }

    private RunResult RunLocked(CompiledScript script, ScriptContext context, TimeSpan timeout, CancellationToken cancellation)
    {
        {
            var sw = Stopwatch.StartNew();
            var stdout = new BoundedWriter(_maxOutputBytes, context.Stdout);
            var stderr = new BoundedWriter(_maxOutputBytes, context.Stderr);
            var refused = new List<string>();
            ScriptScope scope = PrepareScope(context, refused, script.Trust);

            ScriptScope sys = _engine.GetSysModule();
            object? oldOut = sys.GetVariable("stdout");
            object? oldErr = sys.GetVariable("stderr");
            sys.SetVariable("stdout", new PythonTextSink(stdout));
            sys.SetVariable("stderr", new PythonTextSink(stderr));

            var deadline = new TraceDeadline(timeout, cancellation);
            ScriptError? error = null;
            bool timedOut = false;
            object? value = null;
            try
            {
                _setTrace(_engine, deadline.OnTrace);
                // Invariant check on the run's own thread: the hook just installed must deliver events.
                _liveProbe ??= _engine.CreateScriptSourceFromString(LiveProbe, "<live-probe>", SourceCodeKind.Statements).Compile();
                _liveProbe.Execute(_engine.CreateScope());
                if (deadline.Events == 0)
                {
                    throw new TimeoutUnavailableException("the trace hook is not live on this thread: the script was not run (TIMEOUT_UNAVAILABLE)");
                }
                CompiledCode code = script.Handle is string lateSource
                    ? _engine.CreateScriptSourceFromString(lateSource, ScriptFileName, SourceCodeKind.Statements).Compile()
                    : (CompiledCode)script.Handle;
                code.Execute(scope);
                scope.TryGetVariable("result", out value);
            }
            catch (TimeoutUnavailableException)
            {
                throw;
            }
            catch (KeyboardInterruptException)
            {
                timedOut = true;
            }
            catch (Exception ex) when (ex.InnerException is KeyboardInterruptException || deadline.Expired)
            {
                timedOut = true;
            }
            catch (Exception ex)
            {
                ExceptionOperations eo = _engine.GetService<ExceptionOperations>();
                // Python type name (ValueError), not the .NET one (ValueErrorException); .NET exceptions keep theirs.
                eo.GetExceptionMessage(ex, out string message, out string typeName);
                error = new ScriptError(string.IsNullOrEmpty(typeName) ? ex.GetType().Name : typeName, string.IsNullOrEmpty(message) ? ex.Message : message,
                    eo.FormatException(ex), ex) { Line = ScriptLine(eo, ex) };
            }
            finally
            {
                _setTrace(_engine, null);
                sys.SetVariable("stdout", oldOut);
                sys.SetVariable("stderr", oldErr);
            }
            sw.Stop();
            var leftovers = new List<object>();
            foreach (string name in scope.GetVariableNames())
            {
                if (!name.StartsWith("__", StringComparison.Ordinal) && scope.TryGetVariable(name, out object? o) && o is not null)
                {
                    leftovers.Add(o);
                }
            }
            JsonNodeResult converted = timedOut || error is not null ? default : ConvertResult(value, context);
            if (converted.Error is not null)
            {
                error = converted.Error;
            }
            return new RunResult(converted.Value, stdout.ToString(), stderr.ToString(), error, timedOut, sw.Elapsed)
            {
                StdoutTruncated = stdout.Truncated,
                StderrTruncated = stderr.Truncated,
                RefusedImports = refused,
                Leftovers = leftovers,
            };
        }
    }

    public void Dispose()
    {
        _engine.Runtime.Shutdown();
    }

    private ScriptScope PrepareScope(ScriptContext context, List<string> refused, ScriptTrust trust)
    {
        ScriptScope scope = _engine.CreateScope();
        scope.SetVariable("uiapp", context.UiApp);
        scope.SetVariable("app", context.App);
        scope.SetVariable("uidoc", context.UiDoc);
        scope.SetVariable("doc", context.Doc);
        scope.SetVariable("inputs", PyResultConverter.ToPython(context.Inputs));
        scope.SetVariable("report_progress", context.ReportProgress);
        if (trust == ScriptTrust.Shipped)
        {
            foreach (KeyValuePair<string, object?> extra in context.Extras)
            {
                scope.SetVariable(extra.Key, extra.Value is ScriptNamespace ns ? ResolveNamespace(ns.Name) : extra.Value);
            }
            ImportGuard.Install(scope, _engine, name => refused.Add(name), source => ExceptNarrower.Narrow(_engine, source));
            return scope;
        }

        // ADR-036: an assistant's script. Code built from strings is checked like the script itself.
        string CheckAndNarrow(string source)
        {
            IReadOnlyList<PythonViolation> violations = PythonPolicy.Check(_engine, source, _refused);
            if (violations.Count > 0)
            {
                throw new ArgumentException(PythonPolicy.Message(violations));
            }
            return ExceptNarrower.Narrow(_engine, source);
        }
        var restrictions = new AgentRestrictions(name => PythonPolicy.NameRefusal(name, _refused), FacadeTypes());
        object? facade = ImportGuard.Install(scope, _engine, name => refused.Add(name), CheckAndNarrow, restrictions);
        foreach (KeyValuePair<string, object?> extra in context.Extras)
        {
            object? value = extra.Value is ScriptNamespace ns
                ? (ns.Name == "System" ? facade : ns.Name.StartsWith("System.", StringComparison.Ordinal) ? null : ResolveNamespace(ns.Name))
                : extra.Value;
            scope.SetVariable(extra.Key, value);
        }
        return scope;
    }

    /// <summary>The types of the façade System (ADR-036), resolved once from the real namespace; missing names are left out.</summary>
    private List<KeyValuePair<string, object>> FacadeTypes()
    {
        if (_facadeTypes is not null)
        {
            return _facadeTypes;
        }
        var found = new List<KeyValuePair<string, object>>();
        object? system = ResolveNamespace("System");
        if (system is not null)
        {
            foreach (string dotted in ImportGuard.SystemFacade)
            {
                object? node = system;
                foreach (string part in dotted.Split('.'))
                {
                    if (node is null || !_engine.Operations.TryGetMember(node, part, out object? next))
                    {
                        node = null;
                        break;
                    }
                    node = next;
                }
                if (node is not null)
                {
                    found.Add(new KeyValuePair<string, object>(dotted, node));
                }
            }
        }
        _facadeTypes = found;
        return found;
    }

    /// <summary>
    /// The sentinel between runs (ADR-036, F-R1): Python code of this engine that starts on the run thread while no run is
    /// in progress was left behind by a script (an event handler, a callback). It is stopped at its first trace event, and
    /// again at every later one, like a script past its deadline.
    /// </summary>
    private TracebackDelegate? BetweenRuns(TraceBackFrame frame, string result, object payload)
    {
        if (_running)
        {
            return null;
        }
        Interlocked.Increment(ref _deferredStops);
        string where;
        try
        {
            where = frame.f_code.co_filename + ":" + Convert.ToString(frame.f_lineno, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            where = "?";
        }
        try
        {
            DeferredCodeStopped?.Invoke(where);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A listener must not keep deferred code alive.
        }
        throw new KeyboardInterruptException(Brand.Product + ": code left behind by a script tried to run after its run had ended; it was stopped (ADR-036)");
    }

    /// <summary>Imports a namespace or module with the engine's own (unguarded) builtins; null when it cannot be imported.</summary>
    private object? ResolveNamespace(string name)
    {
        if (_namespaces.TryGetValue(name, out object? cached))
        {
            return cached;
        }
        object? resolved = null;
        try
        {
            ScriptScope setup = _engine.CreateScope();
            _engine.CreateScriptSourceFromString("import " + name + " as _ns", "<" + Brand.Id + "-extras>", SourceCodeKind.Statements).Execute(setup);
            resolved = setup.GetVariable("_ns");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            resolved = null;
        }
        _namespaces[name] = resolved;
        return resolved;
    }

    private static int? ScriptLine(ExceptionOperations eo, Exception ex)
    {
        int? line = null;
        try
        {
            foreach (DynamicStackFrame frame in eo.GetStackFrames(ex))
            {
                if (frame.GetFileName() == ScriptFileName)
                {
                    line = frame.GetFileLineNumber();
                }
            }
        }
        catch (Exception probe) when (probe is not OutOfMemoryException)
        {
            return null;
        }
        if (line is null && ex is SyntaxErrorException se)
        {
            line = se.Line;
        }
        return line;
    }

    private static JsonNodeResult ConvertResult(object? value, ScriptContext context)
    {
        try
        {
            return new JsonNodeResult(PyResultConverter.ToJson(value, context.Converter), null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new JsonNodeResult(null, new ScriptError(ex.GetType().Name, "the result could not be converted to JSON: " + ex.Message, null, ex));
        }
    }

    private readonly record struct JsonNodeResult(System.Text.Json.Nodes.JsonNode? Value, ScriptError? Error);

    /// <summary>What <c>sys.stdout</c>/<c>sys.stderr</c> are during a run: a Python file-like object over a bounded writer.</summary>
    public sealed class PythonTextSink
    {
        private readonly BoundedWriter _writer;

        internal PythonTextSink(BoundedWriter writer)
        {
            _writer = writer;
        }

#pragma warning disable CA1822, IDE1006 // Python protocol: lower-case instance methods
        public string encoding => "utf-8";

        public int write(object? s)
        {
            string text = s?.ToString() ?? string.Empty;
            _writer.Write(text);
            return text.Length;
        }

        public void writelines(System.Collections.IEnumerable lines)
        {
            ArgumentNullException.ThrowIfNull(lines);
            foreach (object? line in lines)
            {
                write(line);
            }
        }

        public void flush()
        {
        }

        public bool isatty() => false;

        public bool writable() => true;
#pragma warning restore CA1822, IDE1006
    }

    private sealed class TraceDeadline
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly TimeSpan _timeout;
        private readonly CancellationToken _cancellation;
        private int _events;

        public TraceDeadline(TimeSpan timeout, CancellationToken cancellation)
        {
            _timeout = timeout;
            _cancellation = cancellation;
        }

        public bool Expired { get; private set; }

        public int Events => _events;

        public TracebackDelegate? OnTrace(TraceBackFrame frame, string result, object payload)
        {
            _events++;
            if (Expired || (_events % 64 == 0 && (_clock.Elapsed > _timeout || _cancellation.IsCancellationRequested)))
            {
                Expired = true;
                // Surfaces in Python as KeyboardInterrupt (a BaseException): `except Exception` does not catch it,
                // and handlers that could have caught it were narrowed at compile time. Raised again at every event.
                throw new KeyboardInterruptException(Brand.Product + ": script deadline exceeded (cooperative timeout)");
            }
            return OnTrace;
        }
    }
}
