// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;

namespace Rift.Scripting.CSharp;

/// <summary>How the script's method looks to the compiler: the usings and the typed parameters the host passes.</summary>
/// <param name="Usings">Namespaces imported by the wrapper (the script cannot add <c>using</c> lines).</param>
/// <param name="Parameters">Declaration of <c>doc, uidoc, uiapp, app</c>: Revit types in Revit, <c>object</c> in tests.</param>
/// <param name="References">Paths of the assemblies the script compiles against, besides the shared framework.</param>
public sealed record CSharpHostShape(IReadOnlyList<string> Usings, string Parameters, IReadOnlyList<string> References)
{
    public static CSharpHostShape Plain { get; } = new(
        new[] { "System", "System.Collections.Generic", "System.Linq", "System.Text" },
        "object doc, object uidoc, object uiapp, object app", Array.Empty<string>());

    private static readonly string[] s_revitUsings =
        { "System", "System.Collections.Generic", "System.Linq", "System.Text", "Autodesk.Revit.DB", "Autodesk.Revit.UI" };

    public static CSharpHostShape Revit(IReadOnlyList<string> references) => new(
        s_revitUsings,
        "Document doc, UIDocument uidoc, UIApplication uiapp, Autodesk.Revit.ApplicationServices.Application app", references);
}

/// <summary>
/// C# scripts beside IronPython (ADR-035): the script is the body of a method that returns its answer
/// (<c>return new { walls = n };</c>), with <c>doc, uidoc, uiapp, app, inputs, print, report_progress</c> in scope. It is
/// parsed alone (so that it cannot close the wrapper), checked by <see cref="CSharpPolicy"/>, rewritten with timeout
/// checks (<see cref="CheckpointRewriter"/>), compiled by Roslyn and loaded into a collectible context that is unloaded
/// after the run. It runs under the same read and write guards as Python scripts, on the Revit API thread.
/// </summary>
public sealed class CSharpEngine : IScriptEngine
{
    public const string ScriptFileName = "<agent-script>";
    private const string ClassName = "RiftScript";

    private readonly CSharpHostShape _shape;
    private readonly int _maxOutputBytes;
    private readonly Lazy<IReadOnlyList<MetadataReference>> _references;
    private readonly object _runLock = new();

    public CSharpEngine(CSharpHostShape shape, int maxOutputBytes = 1024 * 1024)
    {
        _shape = shape;
        _maxOutputBytes = maxOutputBytes;
        _references = new Lazy<IReadOnlyList<MetadataReference>>(BuildReferences, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string Language => "csharp";

    public bool TimeoutAvailable => true;

    /// <summary>Lines of the wrapper before the script's first line.</summary>
    private int HeaderLines => _shape.Usings.Count + 8;

    public CompileResult Compile(string source, string fileName)
    {
        ArgumentNullException.ThrowIfNull(source);
        string sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();

        // 1. The script alone, as one block: it cannot close the method or declare a type.
        string blockText = "{\n" + source + "\n}";
        var options = new CSharpParseOptions(LanguageVersion.CSharp12, DocumentationMode.None);
        StatementSyntax parsed = SyntaxFactory.ParseStatement(blockText, 0, options, consumeFullText: true);
        if (parsed is not BlockSyntax || parsed.GetDiagnostics().FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error) is { } syntaxError)
        {
            Diagnostic? d = parsed.GetDiagnostics().FirstOrDefault(x => x.Severity == DiagnosticSeverity.Error);
            int line = d is null ? 1 : d.Location.GetLineSpan().StartLinePosition.Line;
            return new CompileResult(false, null, Math.Max(1, line), 0, d?.GetMessage(CultureInfo.InvariantCulture) ?? "the script must be a method body");
        }
        IReadOnlyList<CSharpViolation> syntaxViolations = CSharpPolicy.CheckSyntax(parsed, 1);
        if (syntaxViolations.Count > 0)
        {
            return Refused(syntaxViolations);
        }

        // 2. The wrapper, with the script at a known line.
        string wrapper = Wrapper(blockText);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(wrapper, options, fileName == "<agent-script>" ? ScriptFileName : fileName, Encoding.UTF8);
        var compilation = CSharpCompilation.Create("rift_script_" + sha[..12], new[] { tree }, _references.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug, allowUnsafe: false,
                nullableContextOptions: NullableContextOptions.Disable));
        BlockSyntax body = Body(tree);
        if (body.ToString() != blockText)
        {
            return new CompileResult(false, null, 1, 0, "the script must be a method body");
        }
        Diagnostic? error = compilation.GetDiagnostics().FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
        if (error is not null)
        {
            return new CompileResult(false, null, ScriptLine(error.Location), error.Location.GetLineSpan().StartLinePosition.Character + 1,
                error.Id + ": " + error.GetMessage(CultureInfo.InvariantCulture));
        }
        SemanticModel model = compilation.GetSemanticModel(tree);
        IReadOnlyList<CSharpViolation> symbolViolations = CSharpPolicy.CheckSymbols(body, model, HeaderLines);
        if (symbolViolations.Count > 0)
        {
            return Refused(symbolViolations);
        }

        // 3. Timeout checks, catch filters and transaction tracking, then emit.
        BlockSyntax rewritten = (BlockSyntax)new CheckpointRewriter(model).Visit(body)!;
        SyntaxTree final = tree.WithRootAndOptions(tree.GetRoot().ReplaceNode(body, rewritten), options);
        CSharpCompilation emitted = compilation.ReplaceSyntaxTree(tree, final);
        using var pe = new MemoryStream();
        EmitResult result = emitted.Emit(pe, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded));
        if (!result.Success)
        {
            Diagnostic d = result.Diagnostics.First(x => x.Severity == DiagnosticSeverity.Error);
            return new CompileResult(false, null, ScriptLine(d.Location), 0, "internal rewrite error " + d.Id + ": " + d.GetMessage(CultureInfo.InvariantCulture));
        }
        return new CompileResult(true, new CompiledScript(Language, sha, pe.ToArray()), 0, 0, null);
    }

    public RunResult Run(CompiledScript script, ScriptContext context, TimeSpan timeout, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(context);
        if (script.Handle is not byte[] image)
        {
            throw new ArgumentException("not a C# script", nameof(script));
        }
        lock (_runLock)
        {
            var sw = Stopwatch.StartNew();
            var stdout = new BoundedWriter(_maxOutputBytes, context.Stdout);
            var stderr = new BoundedWriter(_maxOutputBytes, context.Stderr);
            var tracked = new List<object>();
            var gate = new RunGate(timeout, cancellation);
            var alc = new ScriptLoadContext();
            ScriptError? error = null;
            bool timedOut = false;
            JsonNode? value = null;
            try
            {
                Assembly assembly = alc.LoadFromStream(new MemoryStream(image));
                Type type = assembly.GetType(ClassName, throwOnError: true)!;
                type.GetField("__rift_check")!.SetValue(null, (Action)gate.Check);
                type.GetField("__rift_ok")!.SetValue(null, (Func<bool>)gate.Ok);
                type.GetField("__rift_tracked")!.SetValue(null, (Action<object>)tracked.Add);
                MethodInfo run = type.GetMethod("Run")!;
                Action<object?> print = o => stdout.WriteLine(o?.ToString() ?? "null");
                object? answer = run.Invoke(null, new[]
                {
                    context.Doc, context.UiDoc, context.UiApp, context.App, ToNet(context.Inputs), print,
                    (Action<int, string>)((p, m) => context.ReportProgress(p, m)),
                });
                value = ToJson(answer, context.Converter, gate, 0);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is ScriptStopped || gate.Stopped)
            {
                timedOut = true;
            }
            catch (ScriptStopped)
            {
                timedOut = true;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                Exception inner = ex.InnerException;
                error = new ScriptError(inner.GetType().Name, inner.Message, Trace(inner), inner) { Line = Line(inner) };
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException or InvalidCastException)
            {
                error = new ScriptError(ex.GetType().Name, ex.Message, null, ex);
            }
            finally
            {
                gate.Finish();
                alc.Unload();
            }
            sw.Stop();
            return new RunResult(timedOut || error is not null ? null : value, stdout.ToString(), stderr.ToString(), error, timedOut, sw.Elapsed)
            {
                StdoutTruncated = stdout.Truncated,
                StderrTruncated = stderr.Truncated,
                Leftovers = tracked,
            };
        }
    }

    public void Dispose()
    {
    }

    private string Wrapper(string blockText)
    {
        var sb = new StringBuilder();
        foreach (string u in _shape.Usings)
        {
            sb.Append("using ").Append(u).Append(";\n");
        }
        sb.Append("public static class ").Append(ClassName).Append("\n{\n");
        sb.Append("public static System.Action __rift_check;\n");
        sb.Append("public static System.Func<bool> __rift_ok;\n");
        sb.Append("public static System.Action<object> __rift_tracked;\n");
        sb.Append("public static T __rift_track<T>(T created) { __rift_tracked(created); return created; }\n");
        sb.Append("public static object Run(").Append(_shape.Parameters)
          .Append(", IReadOnlyDictionary<string, object> inputs, System.Action<object> print, System.Action<int, string> report_progress)\n");
        sb.Append(blockText);
        sb.Append("\n}\n");
        return sb.ToString();
    }

    private static BlockSyntax Body(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "Run").Body!;

    /// <summary>Script line k is line <see cref="HeaderLines"/> + k of the wrapper (1-based).</summary>
    private int ScriptLine(Location location) => Math.Max(1, location.GetLineSpan().StartLinePosition.Line + 1 - HeaderLines);

    private static CompileResult Refused(IReadOnlyList<CSharpViolation> violations)
    {
        CSharpViolation first = violations[0];
        string message = "not allowed in RIFT C# scripts: " + string.Join("; ", violations.Take(5).Select(v => "line " + v.Line.ToString(CultureInfo.InvariantCulture) + " " + v.What + " (" + v.Reason + ")"));
        return new CompileResult(false, null, first.Line, 0, message);
    }

    private int? Line(Exception ex)
    {
        var trace = new StackTrace(ex, fNeedFileInfo: true);
        foreach (StackFrame frame in trace.GetFrames())
        {
            if (frame.GetFileName() == ScriptFileName && frame.GetFileLineNumber() > 0)
            {
                return Math.Max(1, frame.GetFileLineNumber() - HeaderLines);
            }
        }
        return null;
    }

    private static string Trace(Exception ex) => ex.GetType().Name + ": " + ex.Message;

    private IReadOnlyList<MetadataReference> BuildReferences()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (string p in tpa.Split(Path.PathSeparator))
            {
                string name = Path.GetFileNameWithoutExtension(p);
                if (name is "System.Private.CoreLib" or "System.Runtime" or "netstandard" or "System.Collections" or "System.Linq"
                    or "System.Text.RegularExpressions" or "System.Runtime.Extensions" or "System.ObjectModel" or "System.Collections.NonGeneric"
                    or "mscorlib")
                {
                    paths.Add(p);
                }
            }
        }
        paths.Add(typeof(object).Assembly.Location);
        foreach (string r in _shape.References)
        {
            paths.Add(r);
        }
        return paths.Where(File.Exists).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToArray();
    }

    private static object? ToNet(JsonNode? node) => node switch
    {
        null => null,
        JsonObject o => o.ToDictionary(kv => kv.Key, kv => ToNet(kv.Value)),
        JsonArray a => a.Select(ToNet).ToList(),
        JsonValue v => v.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.String => v.GetValue<string>(),
            System.Text.Json.JsonValueKind.Number => long.TryParse(v.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)
                ? (object)l
                : (object)double.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture),
            _ => v.ToJsonString(),
        },
        _ => node.ToJsonString(),
    };

    private static JsonNode? ToJson(object? value, ForeignConverter? converter, RunGate gate, int depth)
    {
        gate.Check();
        if (depth > 32)
        {
            return JsonValue.Create("<too deep>");
        }
        switch (value)
        {
            case null:
                return null;
            case string s:
                return JsonValue.Create(s);
            case bool b:
                return JsonValue.Create(b);
            case char c:
                return JsonValue.Create(c.ToString());
            case int or long or short or byte or sbyte or ushort or uint:
                return JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            case ulong u:
                return JsonValue.Create(u);
            case float or double or decimal:
                double d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return double.IsFinite(d) ? JsonValue.Create(d) : JsonValue.Create(d.ToString(CultureInfo.InvariantCulture));
            case Enum e:
                return JsonValue.Create(e.ToString());
            case JsonNode n:
                return n.DeepClone();
        }
        if (converter is not null && converter(value, out JsonNode? converted))
        {
            return converted;
        }
        if (value is IDictionary dict)
        {
            var o = new JsonObject();
            foreach (DictionaryEntry e in dict)
            {
                o[Convert.ToString(e.Key, CultureInfo.InvariantCulture) ?? "null"] = ToJson(e.Value, converter, gate, depth + 1);
            }
            return o;
        }
        if (value is ITuple tuple)
        {
            var a = new JsonArray();
            for (int i = 0; i < tuple.Length; i++)
            {
                a.Add(ToJson(tuple[i], converter, gate, depth + 1));
            }
            return a;
        }
        if (value is IEnumerable list)
        {
            var a = new JsonArray();
            foreach (object? item in list)
            {
                a.Add(ToJson(item, converter, gate, depth + 1));
            }
            return a;
        }
        Type t = value.GetType();
        if (t.Name.Contains("AnonymousType", StringComparison.Ordinal))
        {
            var o = new JsonObject();
            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                o[p.Name] = ToJson(p.GetValue(value), converter, gate, depth + 1);
            }
            return o;
        }
        return JsonValue.Create(value.ToString());
    }

    /// <summary>Thrown by the checks once the run must stop; the injected catch filters let it through.</summary>
    private sealed class ScriptStopped : Exception
    {
        public ScriptStopped()
            : base("the script was stopped (timeout, cancellation or recursion too deep)")
        {
        }
    }

    private sealed class RunGate
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly TimeSpan _timeout;
        private readonly CancellationToken _cancellation;
        private volatile bool _stopped;
        private volatile bool _finished;

        public RunGate(TimeSpan timeout, CancellationToken cancellation)
        {
            _timeout = timeout;
            _cancellation = cancellation;
        }

        public bool Stopped => _stopped;

        public void Check()
        {
            if (_stopped || _finished || _clock.Elapsed > _timeout || _cancellation.IsCancellationRequested)
            {
                _stopped = true;
                throw new ScriptStopped();
            }
            if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
            {
                _stopped = true;
                throw new ScriptStopped();
            }
        }

        public bool Ok() => !_stopped && !_finished;

        /// <summary>After the run, any delegate the script left behind throws at its first check.</summary>
        public void Finish() => _finished = true;
    }

    private sealed class ScriptLoadContext : AssemblyLoadContext
    {
        public ScriptLoadContext()
            : base("rift-script", isCollectible: true)
        {
        }

        // Everything else (the shared framework, RevitAPI) resolves in the default context: one type identity.
        protected override Assembly? Load(AssemblyName assemblyName) => null;
    }
}
