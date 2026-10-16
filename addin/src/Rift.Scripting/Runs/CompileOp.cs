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

    public CompileOp(Func<IScriptEngine> engine)
    {
        _engine = engine;
    }

    public string Op => "compile";

    public bool NeedsApiThread => false;

    public JsonNode? Execute(RequestEnvelope request, IProgress<(int Percent, string Message)> progress, CancellationToken cancellation)
    {
        string script = RunArgs.Script(request);
        string language = RunArgs.Language(request);
        IScriptEngine engine = _engine();
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
        }
        return node;
    }
}
