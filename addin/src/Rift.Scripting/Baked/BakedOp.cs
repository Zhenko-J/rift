// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;

using Rift.Core.Baked;
using Rift.Core.Protocol;
using Rift.Scripting.Runs;

namespace Rift.Scripting.Baked;

/// <summary>
/// <c>op: tool</c> names <c>baked.propose</c>, <c>baked.list</c> and <c>baked.run</c> (ADR-034). A proposal is compiled
/// first (a syntax error never reaches the inbox) and waits for the user in Revit; a command runs only once accepted, after
/// its SHA-256 check, through exactly the path of <c>run_query</c> (mode query) or <c>run_modify</c> (mode modify: expected
/// document, session, document policy, dry run by default, one undo entry "Agent: baked: &lt;name&gt;").
/// </summary>
public sealed class BakedOp : IToolHandler
{
    public const string Propose = "baked.propose";
    public const string List = "baked.list";
    public const string Run = "baked.run";

    private static readonly string[] s_names = { Propose, List, Run };

    private readonly BakedStore _store;
    private readonly RunQueryOp _runner;
    private readonly Func<IScriptEngine> _engine;

    public BakedOp(BakedStore store, RunQueryOp runner, Func<IScriptEngine> engine)
    {
        _store = store;
        _runner = runner;
        _engine = engine;
    }

    public string Prefix => "baked.";

    public IEnumerable<string> Names => s_names;

    public JsonNode? Execute(string name, RequestEnvelope request, IProgress<(int Percent, string Message)> progress, CancellationToken cancellation)
    {
        JsonObject inputs = request.Args?["inputs"] as JsonObject ?? new JsonObject();
        switch (name)
        {
            case Propose:
            {
                string? script = Str(inputs, "script");
                if (!string.IsNullOrWhiteSpace(script))
                {
                    CompileResult compiled = _engine().Compile(script, "baked.py");
                    if (!compiled.Ok)
                    {
                        throw new OperationException(ErrorCodes.ScriptError, "the script does not compile (line " + compiled.Line + "): nothing was proposed",
                            new JsonObject { ["line"] = compiled.Line, ["message"] = compiled.Message }, refused: true);
                    }
                }
                BakedProposal p = _store.Propose(Str(inputs, "name"), Str(inputs, "description"), Str(inputs, "mode"), script);
                return new JsonObject
                {
                    ["proposed"] = p.Name,
                    ["id"] = p.Id,
                    ["sha256"] = p.Sha256,
                    ["next"] = "the user reviews and accepts it in Revit (RIFT panel, inbox); it cannot run before",
                };
            }
            case List:
                return new JsonObject
                {
                    ["commands"] = new JsonArray(_store.Commands().Select(c => (JsonNode?)new JsonObject
                    {
                        ["name"] = c.Name,
                        ["description"] = c.Description,
                        ["mode"] = c.Mode,
                        ["sha256"] = c.Sha256,
                        ["accepted_utc"] = c.AcceptedUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    }).ToArray()),
                    ["pending"] = new JsonArray(_store.Proposals().Select(p => (JsonNode?)new JsonObject
                    {
                        ["name"] = p.Name,
                        ["mode"] = p.Mode,
                    }).ToArray()),
                };
            case Run:
            {
                BakedCommand c = _store.Find(Str(inputs, "name"));
                JsonObject scriptInputs = inputs["inputs"] is JsonObject si ? (JsonObject)si.DeepClone() : new JsonObject();
                TimeSpan timeout = TimeSpan.FromSeconds(Math.Clamp(
                    request.Args?["timeout_s"] is JsonValue t && t.TryGetValue(out int ts) ? ts : 120, 1, RunArgs.MaxTimeoutS));
                string title = "baked: " + c.Name;
                JsonObject run = c.Mode == "modify"
                    ? _runner.RunFixedModify(request, "baked:" + c.Name, c.Script, title, scriptInputs, timeout, null, progress, cancellation)
                    : _runner.RunFixed(request, "baked:" + c.Name, c.Script, title, timeout, scriptInputs, null, progress, cancellation);
                JsonNode? value = run["result"]?.DeepClone();
                run.Remove("result");
                return new JsonObject { ["command"] = c.Name, ["mode"] = c.Mode, ["sha256"] = c.Sha256, ["result"] = value, ["run"] = run };
            }
            default:
                throw new OperationException(ErrorCodes.BadRequest, "unknown baked operation " + name, refused: true);
        }
    }

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}
