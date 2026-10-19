// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

using Rift.Core.Jobs;
using Rift.Core.Journal;
using Rift.Core.Logging;
using Rift.Core.Protocol;
using Rift.Scripting;
using Rift.Scripting.Python;
using Rift.Scripting.Runs;
using Rift.Scripting.Tools;

namespace Rift.Tests;

/// <summary>
/// M3 plan tests (ADR-032): the typed tool catalogue loads every embedded script, each compiles under IronPython 3.4 and
/// imports only allowed modules, reads never open a transaction and writes always do; routing through <c>op: tool</c> sends
/// reads through the query path and writes and batches through the modify path, with the tool's arguments as <c>inputs</c>.
/// </summary>
public sealed class TypedToolTests : IClassFixture<EngineFixture>, IDisposable
{
    /// <summary>The typed tools the registry ships: M3, and the Revit-window tools of M4 (result_ref is served by the server alone, batch by TypedToolOp).</summary>
    public static readonly string[] Expected =
    {
        "list_view_templates", "get_view_template", "apply_view_template", "duplicate_view_template", "delete_view_template",
        "list_view_filters", "create_view_filter", "apply_filter_to_view", "set_filter_overrides", "override_element_graphics",
        "clear_element_overrides", "list_shared_parameters", "list_project_parameters", "list_category_parameters",
        "create_shared_parameter", "create_project_parameter", "bind_shared_parameter", "list_schedules", "get_schedule_data",
        "get_schedule_definition", "create_schedule", "add_schedule_field", "list_sheets", "list_titleblocks",
        "get_titleblock_parameters", "create_sheet", "place_view_on_sheet", "renumber_sheets", "list_materials",
        "get_material_properties", "create_material", "assign_material_to_element", "list_loaded_families", "list_family_types",
        "get_family_instances", "duplicate_family_type", "rename_family_type", "list_levels", "list_views", "get_element",
        "get_element_parameters", "find_elements", "get_view_elements", "set_element_parameters", "set_type_parameters",
        "delete_elements", "get_warnings_summary", "find_untagged_elements", "audit_model", "purge_unused", "survey",
        "select_elements", "show_element", "activate_view",
    };

    private readonly EngineFixture _fixture;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rift-typed-" + Guid.NewGuid().ToString("N"));

    public TypedToolTests(EngineFixture fixture)
    {
        _fixture = fixture;
    }

    // ── catalogue ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_catalogue_holds_exactly_the_shipped_typed_tools()
    {
        Assert.Equal(Expected.OrderBy(n => n, StringComparer.Ordinal), TypedToolCatalog.Default.Names);
        Assert.True(TypedToolCatalog.PreludeLines > 50);
    }

    public static IEnumerable<object[]> Tools() => TypedToolCatalog.Default.All.Select(t => new object[] { t.Name });

    [Theory]
    [MemberData(nameof(Tools))]
    public void Each_tool_compiles_and_imports_only_allowed_modules(string name)
    {
        TypedTool tool = TypedToolCatalog.Default.Find(name)!;
        CompileResult compiled = _fixture.Engine.Compile(tool.Source, name + ".py", ScriptTrust.Shipped);
        Assert.True(compiled.Ok, name + ": line " + (compiled.Line - TypedToolCatalog.PreludeLines) + " of the tool: " + compiled.Message);
        foreach (Match m in Regex.Matches(tool.Source, @"^\s*(?:import|from)\s+([\w.]+)", RegexOptions.Multiline))
        {
            string top = m.Groups[1].Value.Split('.')[0];
            Assert.Contains(top, ImportGuard.AllowedModules);
            Assert.DoesNotMatch(@"^System\.(Diagnostics|Net|Reflection|Threading|Runtime)", m.Groups[1].Value);
        }
        Assert.DoesNotMatch(@"\.(Save|SaveAs|Close|SynchronizeWithCentral)\(", tool.Source);
        Assert.DoesNotContain("OpenAndActivateDocument", tool.Source, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void Reads_never_open_a_transaction_and_writes_always_do(string name)
    {
        TypedTool tool = TypedToolCatalog.Default.Find(name)!;
        string body = tool.Source.Split("# ---- tool body", 2)[1];
        Assert.Contains("result =", body, StringComparison.Ordinal);
        if (tool.Writes)
        {
            // A write either changes the model in one named transaction, or (create_shared_parameter) writes outside the
            // model and must then honour the dry run itself.
            Assert.True(body.Contains("with tx(\"" + name + "\")", StringComparison.Ordinal) || body.Contains("if rift_dry_run", StringComparison.Ordinal), name);
        }
        else
        {
            Assert.DoesNotContain("tx(", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Transaction(", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Acceptance_findings_of_2026_10_16_stay_fixed()
    {
        static string Body(string name) => TypedToolCatalog.Default.Find(name)!.Source.Split("# ---- tool body", 2)[1];

        // Revit refuses {}[]|;<>?`~ in a sheet number: the temporary number of a swap must avoid them.
        Match temp = Regex.Match(Body("renumber_sheets"), "SheetNumber = \"([^\"]*)\"");
        Assert.True(temp.Success);
        Assert.DoesNotMatch(@"[{}\[\]|;<>?`~]", temp.Groups[1].Value);
        // Viewport.Create returns None for an empty view even when CanAddViewToSheet says yes.
        Assert.Contains("if placed is None", Body("place_view_on_sheet"), StringComparison.Ordinal);
        // ReInsert of an existing binding keeps its categories and is refused when schedules lose columns.
        string bind = Body("bind_shared_parameter");
        Assert.Contains("existing.Categories", bind, StringComparison.Ordinal);
        Assert.Contains("schedule_fields()", bind, StringComparison.Ordinal);
        // A material set through the type is reported as a type change.
        Assert.Contains("\"types_changed\"", Body("assign_material_to_element"), StringComparison.Ordinal);
    }

    [Fact]
    public void Writes_and_reads_are_the_ones_the_registry_expects()
    {
        string[] writes = TypedToolCatalog.Default.All.Where(t => t.Writes).Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[]
        {
            "add_schedule_field", "apply_filter_to_view", "apply_view_template", "assign_material_to_element", "bind_shared_parameter",
            "clear_element_overrides", "create_material", "create_project_parameter", "create_schedule", "create_shared_parameter",
            "create_sheet", "create_view_filter", "delete_elements", "delete_view_template", "duplicate_family_type",
            "duplicate_view_template", "override_element_graphics", "place_view_on_sheet", "purge_unused", "rename_family_type",
            "renumber_sheets", "set_element_parameters", "set_filter_overrides", "set_type_parameters",
        }, writes);
    }

    [Fact]
    public void A_script_without_its_header_is_a_build_error()
    {
        var bad = new TypedToolCatalog(new[] { new TypedTool("x", false, "result = 1\n") });
        Assert.NotNull(bad.Find("x"));
        Assert.Throws<InvalidOperationException>(() => TypedToolCatalog.Load(typeof(TypedToolTests).Assembly));
    }

    // ── routing ──────────────────────────────────────────────────────────────────────────────

    private sealed class Host : IRunHost
    {
        public Host()
        {
            Doc = new RunModifyOpTests.FakeDocument(Write);
            Target = new RunTarget(new object(), new object(), null, Doc, "PLAN_SANDBOX", @"C:\w\PLAN_SANDBOX.rvt", "sandbox");
        }

        public RunQueryOpTests.FakeGuard Read { get; } = new();

        public RunModifyOpTests.FakeWriteGuard Write { get; } = new();

        public RunModifyOpTests.FakeDocument Doc { get; }

        public RunTarget Target { get; }

        public IReadOnlyDictionary<string, object?> Extras { get; } = new Dictionary<string, object?>();

        public ForeignConverter? Converter => null;

        public IRunGuard CreateGuard() => Read;

        public IWriteGuard CreateWriteGuard() => Write;
    }

    private sealed class Runner : IJobRunner
    {
        public void Enqueue(Job job) => _ = Task.Run(() =>
        {
            if (!JobTable.MarkStarted(job))
            {
                return;
            }
            try
            {
                JobTable.MarkDone(job, job.Work(new Progress<(int, string)>(), job.Token), null);
            }
            catch (OperationException ex)
            {
                JobTable.MarkDone(job, null, new ErrorInfo(ex.Code, null, ex.Message), ex.Refused, ex.Details);
            }
        });

        public string? BusyReason() => null;
    }

    private static readonly TypedToolCatalog s_inline = new(new[]
    {
        new TypedTool("echo", false, "# rift-tool: read\nresult = {'got': inputs.get('x'), 'title': doc.Title}\n"),
        new TypedTool("touch", true, "# rift-tool: write\ndoc.Change(int(inputs['id']), 'Views')\nresult = {'touched': inputs['id']}\n"),
        new TypedTool("make", true, "# rift-tool: write\ndoc.Add(int(inputs['id']), 'Sheets')\nresult = {'made': inputs['id']}\n"),
        new TypedTool("boom", true, "# rift-tool: write\ndoc.Add(1, 'Views')\nraise ValueError('template not found')\n"),
    });

    private RequestRouter Router(Host host)
    {
        IScriptEngine e = _fixture.Engine;
        var jobs = new JobTable(TimeSpan.FromMinutes(1));
        var journal = new RunJournal(Path.Combine(_dir, "runs"), new RedactionContext("someone", "somehost"), TimeSpan.FromDays(14), FileLog.Null);
        var run = new RunQueryOp(() => e, () => host, journal, FileLog.Null);
        return new RequestRouter(new IOperationHandler[] { run, new ToolOp(new IToolHandler[] { new TypedToolOp(s_inline, run) }) },
            jobs, new Runner(), () => new TargetInfo(1, 1, null), FileLog.Null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60));
    }

    private static ResponseEnvelope Tool(RequestRouter router, string name, JsonObject inputs, JsonObject? write = null, bool expected = true)
    {
        var args = new JsonObject { ["name"] = name, ["inputs"] = inputs };
        foreach (KeyValuePair<string, JsonNode?> kv in write ?? new JsonObject())
        {
            args[kv.Key] = kv.Value?.DeepClone();
        }
        var body = new JsonObject
        {
            ["v"] = "2.0", ["id"] = "t", ["op"] = "tool", ["timeout_s"] = 60, ["args"] = args,
            ["expected_document"] = expected ? new JsonObject { ["title"] = "PLAN_SANDBOX" } : null,
        };
        return router.Handle(Encoding.UTF8.GetBytes(body.ToJsonString()), CancellationToken.None);
    }

    [Fact]
    public void A_read_tool_runs_under_the_read_guard_with_its_arguments()
    {
        var host = new Host();
        ResponseEnvelope r = Tool(Router(host), "echo", new JsonObject { ["x"] = 7 });
        Assert.Equal("ok", r.Status);
        Assert.False(r.Result!["writes"]!.GetValue<bool>());
        Assert.Equal(7L, r.Result["result"]!["got"]!.GetValue<long>());
        Assert.Equal("PLAN_SANDBOX", r.Result["result"]!["title"]!.GetValue<string>());
        Assert.Equal("ok", r.Result["run"]!["outcome"]!.GetValue<string>());
        Assert.Equal((1, 1), (host.Read.Armed, host.Read.Disarmed));
        Assert.Equal(0, host.Write.Armed);
    }

    [Fact]
    public void A_write_tool_is_a_dry_run_by_default_and_needs_the_expected_document()
    {
        var host = new Host();
        RequestRouter router = Router(host);
        ResponseEnvelope refused = Tool(router, "touch", new JsonObject { ["id"] = 5 }, expected: false);
        Assert.Equal(ErrorCodes.ExpectedDocumentRequired, refused.Error!.Code);

        ResponseEnvelope dry = Tool(router, "touch", new JsonObject { ["id"] = 5 });
        Assert.Equal("ok", dry.Status);
        Assert.True(dry.Result!["writes"]!.GetValue<bool>());
        Assert.Equal("rolled_back", dry.Result["run"]!["outcome"]!.GetValue<string>());
        Assert.Equal("dry_run", dry.Result["run"]!["reason"]!.GetValue<string>());
        Assert.Equal(1, dry.Result["run"]!["changes"]!["modified"]!.GetValue<int>());
        Assert.Equal(5L, dry.Result["result"]!["touched"]!.GetValue<long>());
        Assert.Equal("touch", host.Write.ArmedTitle);

        ResponseEnvelope kept = Tool(router, "touch", new JsonObject { ["id"] = 6 }, new JsonObject { ["dry_run"] = false });
        Assert.Equal("committed", kept.Result!["run"]!["outcome"]!.GetValue<string>());
        Assert.True(host.Write.KeepAsked);
    }

    [Fact]
    public void A_failing_write_tool_rolls_back_with_its_message_in_the_details()
    {
        var host = new Host();
        ResponseEnvelope r = Tool(Router(host), "boom", new JsonObject(), new JsonObject { ["dry_run"] = false });
        Assert.Equal(ErrorCodes.ScriptError, r.Error!.Code);
        Assert.Contains("template not found", r.Result!["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(host.Write.KeepAsked);
    }

    [Fact]
    public void A_batch_chains_write_tools_in_one_modify_run()
    {
        var host = new Host();
        var items = new JsonArray
        {
            new JsonObject { ["tool"] = "touch", ["inputs"] = new JsonObject { ["id"] = 1 } },
            new JsonObject { ["tool"] = "make", ["inputs"] = new JsonObject { ["id"] = 2 } },
            new JsonObject { ["tool"] = "touch", ["inputs"] = new JsonObject { ["id"] = 3 } },
        };
        ResponseEnvelope r = Tool(Router(host), "batch", new JsonObject { ["items"] = items }, new JsonObject { ["dry_run"] = false });
        Assert.Equal("ok", r.Status);
        Assert.Equal("committed", r.Result!["run"]!["outcome"]!.GetValue<string>());
        JsonArray results = r.Result["result"]!["items"]!.AsArray();
        Assert.Equal(new[] { "touch", "make", "touch" }, results.Select(x => x!["tool"]!.GetValue<string>()));
        Assert.Equal(2, r.Result["run"]!["changes"]!["modified"]!.GetValue<int>());
        Assert.Equal(1, r.Result["run"]!["changes"]!["added"]!.GetValue<int>());
        Assert.Equal(1, host.Write.Armed);
        Assert.StartsWith("batch: touch, make", host.Write.ArmedTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void A_batch_with_a_failing_item_keeps_nothing()
    {
        var host = new Host();
        var items = new JsonArray
        {
            new JsonObject { ["tool"] = "touch", ["inputs"] = new JsonObject { ["id"] = 1 } },
            new JsonObject { ["tool"] = "boom", ["inputs"] = new JsonObject() },
        };
        ResponseEnvelope r = Tool(Router(host), "batch", new JsonObject { ["items"] = items }, new JsonObject { ["dry_run"] = false });
        Assert.Equal(ErrorCodes.ScriptError, r.Error!.Code);
        Assert.False(host.Write.KeepAsked);
    }

    [Theory]
    [InlineData("echo")]
    [InlineData("nope")]
    public void A_batch_accepts_only_typed_write_tools(string tool)
    {
        var host = new Host();
        var items = new JsonArray { new JsonObject { ["tool"] = tool, ["inputs"] = new JsonObject() } };
        ResponseEnvelope r = Tool(Router(host), "batch", new JsonObject { ["items"] = items });
        Assert.Equal("refused", r.Status);
        Assert.Equal(ErrorCodes.BadRequest, r.Error!.Code);
        Assert.Equal(0, host.Write.Armed);
    }

    [Fact]
    public void An_unknown_typed_tool_is_refused_with_the_list()
    {
        ResponseEnvelope r = Tool(Router(new Host()), "nope", new JsonObject());
        Assert.Equal("refused", r.Status);
        Assert.Contains("echo", r.Error!.Message, StringComparison.Ordinal);
        Assert.Contains("batch", r.Error.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
