// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using IronPython.Compiler;
using IronPython.Compiler.Ast;
using IronPython.Runtime;

using Microsoft.Scripting;
using Microsoft.Scripting.Hosting;
using Microsoft.Scripting.Hosting.Providers;
using Microsoft.Scripting.Runtime;

namespace Rift.Scripting.Python;

/// <summary>
/// ADR-016 / ADR-026 §7: the cooperative timeout raises a <c>KeyboardInterrupt</c> (a <c>BaseException</c>) at every trace
/// event once the deadline has passed. A handler that catches it would keep the script alive, so every handler that can
/// catch it is rewritten to <c>except Exception</c> before compilation: bare <c>except:</c>, <c>except BaseException</c>,
/// <c>except KeyboardInterrupt</c> and any tuple that contains one of them. The rewrite walks IronPython's own AST (not the
/// text line by line), so strings, comments and nested blocks are never touched, and it preserves line numbers.
/// Known limit: an alias (<c>B = BaseException</c>, <c>except B:</c>) is not recognised; standard-library modules are not
/// rewritten.
/// </summary>
public static class ExceptNarrower
{
    private static readonly HashSet<string> s_catchAll = new(StringComparer.Ordinal) { "BaseException", "KeyboardInterrupt" };
    private static readonly Lazy<ScriptEngine> s_parserEngine = new(() => IronPython.Hosting.Python.CreateEngine());

    /// <summary>Rewrites with a private parsing engine. A source that does not parse is returned unchanged.</summary>
    public static string Narrow(string source) => Narrow(s_parserEngine.Value, source);

    /// <summary>Rewrites using <paramref name="engine"/>'s parser. A source that does not parse is returned unchanged.</summary>
    public static string Narrow(ScriptEngine engine, string source)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(source);
        if (!source.Contains("except", StringComparison.Ordinal))
        {
            return source;
        }
        PythonAst? ast = Parse(engine, source);
        if (ast is null)
        {
            return source;
        }
        var walker = new HandlerWalker(source);
        ast.Walk(walker);
        if (walker.Edits.Count == 0)
        {
            return source;
        }
        var sb = new StringBuilder(source);
        foreach ((int start, int length, string text) in walker.Edits.OrderByDescending(e => e.Start))
        {
            sb.Remove(start, length).Insert(start, text);
        }
        return sb.ToString();
    }

    private static PythonAst? Parse(ScriptEngine engine, string source)
    {
        ScriptSource src = engine.CreateScriptSourceFromString(source, "<narrow>", SourceCodeKind.File);
        SourceUnit unit = HostingHelpers.GetSourceUnit(src);
        var context = (PythonContext)HostingHelpers.GetLanguageContext(engine);
        var sink = new CountingSink();
        var compilerContext = new CompilerContext(unit, context.GetCompilerOptions(), sink);
        try
        {
            using Parser parser = Parser.CreateParser(compilerContext, (PythonOptions)context.Options);
            PythonAst ast = parser.ParseFile(false);
            return sink.Errors == 0 ? ast : null;
        }
        catch (SyntaxErrorException)
        {
            return null;
        }
    }

    private sealed class CountingSink : ErrorSink
    {
        public int Errors { get; private set; }

        public override void Add(SourceUnit source, string message, SourceSpan span, int errorCode, Severity severity)
        {
            if (severity is Severity.Error or Severity.FatalError)
            {
                Errors++;
            }
        }
    }

    private sealed class HandlerWalker : PythonWalker
    {
        private readonly string _source;

        public HandlerWalker(string source)
        {
            _source = source;
        }

        public List<(int Start, int Length, string Text)> Edits { get; } = new();

        public override bool Walk(TryStatementHandler node)
        {
            if (node.Test is null)
            {
                // Bare `except:` → `except Exception:` (inserted right after the keyword, same line).
                int at = node.StartIndex;
                if (at >= 0 && at + 6 <= _source.Length && string.CompareOrdinal(_source, at, "except", 0, 6) == 0)
                {
                    Edits.Add((at + 6, 0, " Exception"));
                }
            }
            else if (CatchesInterrupt(node.Test))
            {
                int start = node.Test.StartIndex;
                int end = node.Test.EndIndex;
                if (start >= 0 && end > start && end <= _source.Length)
                {
                    string original = _source[start..end];
                    int newlines = original.Count(c => c == '\n');
                    // A multi-line tuple keeps its line breaks inside parentheses so that every later line keeps its number.
                    string replacement = newlines == 0 ? "Exception" : "(Exception" + new string('\n', newlines) + ")";
                    Edits.Add((start, end - start, replacement));
                }
            }
            return true;
        }

        private static bool CatchesInterrupt(Expression test) => test switch
        {
            NameExpression name => s_catchAll.Contains(name.Name),
            MemberExpression member => s_catchAll.Contains(member.Name),     // builtins.BaseException
            ParenthesisExpression paren => CatchesInterrupt(paren.Expression),
            TupleExpression tuple => tuple.Items.Any(CatchesInterrupt),
            _ => false,
        };
    }
}
