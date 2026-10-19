// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Text.Json.Nodes;
using System.Threading;

using Rift.Core.Json;
using Rift.Core.Protocol;

namespace Rift.Scripting.Runs;

/// <summary>
/// <c>op: compile</c>: compiles a script with the engine, on the pipe thread, without running it. A syntax error is a normal
/// result <c>{ok: false, line, column, message}</c> so that the server can return the line and never send <c>run</c>.
/// </summary>
public sealed class CompileOp : IOperationHandler
{
    private readonly Func<IScriptEngine> _engine;
    private readonly Func<IScriptEngine>? _csharp;

    public CompileOp(Func<IScriptEngine> engine, Func<IScriptEngine>? csharp = null)
    {
        _engine = engine;
        _csharp = csharp;
    }

    public string Op => "compile";

    public bool NeedsApiThread => false;

    public JsonNode? Execute(RequestEnvelope request, IProgress<(int Percent, string Message)> progress, CancellationToken cancellation)
    {
        string script = RunArgs.Script(request);
        string language = RunArgs.Language(request);
        IScriptEngine engine = language == "csharp"
            ? (_csharp ?? throw new OperationException(ErrorCodes.BadRequest, "C# scripts are not available in this host", refused: true))()
            : _engine();
        CompileResult c = engine.Compile(script, "<agent-script>");
        var node = new JsonObject
        {
            ["ok"] = c.Ok,
            ["language"] = language,
            ["sha256"] = c.Script?.Sha256,
            ["timeout_available"] = engine.TimeoutAvailable,
        };
        if (!c.Ok)
        {
            node["line"] = c.Line;
            node["column"] = c.Column;
            node["message"] = Sanitizer.Sanitize(c.Message, 2048);
            if (c.ErrorType is not null)
            {
                node["type"] = c.ErrorType;
            }
        }
        return node;
    }
}
