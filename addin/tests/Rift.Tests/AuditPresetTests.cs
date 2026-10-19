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
using Rift.Scripting.Presets;
using Rift.Scripting.Runs;

namespace Rift.Tests;

/// <summary>
/// Plan M1 tests 10 (collector) and 11 (presets p1-p3 compile, import only json/datetime and Autodesk.Revit.DB, dump their
/// document), plus <c>op: tool</c> through the router with the real engine, a fake document and a fake read guard.
/// </summary>
public sealed class AuditPresetTests : IClassFixture<EngineFixture>, IDisposable
{
    private readonly EngineFixture _fixture;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rift-audit-" + Guid.NewGuid().ToString("N"));
    private readonly Host _host = new(withDocument: true);

    public AuditPresetTests(EngineFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed class Host : IRunHost
    {
        public Host(bool withDocument)
        {
            Doc = new RunQueryOpTests.FakeDocument(Guard);
            Target = new RunTarget(new object(), new object(), null, withDocument ? Doc : null, withDocument ? "FAKE_SANDBOX" : null,
                withDocument ? @"C:\work\FAKE_SANDBOX.rvt" : null, withDocument ? "sandbox" : null);
        }

        public RunQueryOpTests.FakeGuard Guard { get; } = new();

        public RunQueryOpTests.FakeDocument Doc { get; }

        public RunTarget Target { get; }

        public IReadOnlyDictionary<string, object?> Extras { get; } = new Dictionary<string, object?>();

        public ForeignConverter? Converter => null;

        public IRunGuard CreateGuard() => Guard;
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
                JobTable.MarkDone(job, job.Work(new Progress<(int, string)>(), CancellationToken.None), null);
            }
            catch (OperationException ex)
            {
                JobTable.MarkDone(job, null, new ErrorInfo(ex.Code, null, ex.Message), ex.Refused, ex.Details);
            }
        });

        public string? BusyReason() => null;
    }

    private static Preset Inline(string name, string document, string source) =>
        new(name, null, document, name) { Source = source };

    private RequestRouter Router(PresetCatalog presets, IRunHost? host = null)
    {
        IScriptEngine e = _fixture.Engine;
        var jobs = new JobTable(TimeSpan.FromMinutes(1));
        var journal = new RunJournal(_dir, new RedactionContext("someone", "somehost"), TimeSpan.FromDays(14), FileLog.Null);
        IRunHost h = host ?? _host;
        var run = new RunQueryOp(() => e, () => h, journal, FileLog.Null);
        return new RequestRouter(new IOperationHandler[] { run, new ToolOp(new IToolHandler[] { new AuditOp(presets, run, journal) }) },
            jobs, new Runner(), () => new TargetInfo(1, 1, null), FileLog.Null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60));
    }

    private static ResponseEnvelope Tool(RequestRouter router, string name)
    {
        var body = new JsonObject { ["v"] = "2.0", ["id"] = "t", ["op"] = "tool", ["timeout_s"] = 60, ["args"] = new JsonObject { ["name"] = name } };
        return router.Handle(Encoding.UTF8.GetBytes(body.ToJsonString()), CancellationToken.None);
    }

    // ── collector ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_collector_keeps_the_number_text_of_the_preset_and_a_second_put_replaces()
    {
        var c = new MemoryResultCollector();
        c.Put("01_x", "{\"width_mm\": 594.0, \"n\": 3}");
        c.Put("01_x", "{\"width_mm\": 420.0, \"ids\": [1, 2]}");
        Assert.Single(c.Documents);
        string json = c.Documents["01_x"]!.ToJsonString();
        Assert.Contains("420.0", json, StringComparison.Ordinal);
        Assert.DoesNotContain("594", json, StringComparison.Ordinal);
        Assert.Contains("420.0", MemoryResultCollector.Sanitized(c.Documents["01_x"])!.ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("x", "{}")]
    [InlineData("01-x", "{}")]
    [InlineData("../01_x", "{}")]
    [InlineData("01_x", "{not json")]
    public void The_collector_refuses_bad_names_and_invalid_json(string name, string json)
    {
        Assert.Throws<ArgumentException>(() => new MemoryResultCollector().Put(name, json));
    }

    [Fact]
    public void The_collector_hands_the_text_of_an_earlier_document_back_to_the_merge_step()
    {
        var c = new MemoryResultCollector();
        Assert.Null(c.Text("01_x"));
        c.Put("01_x", "{\"w\": 594.0}");
        c.Put("00_TOUT", "{\"data\": {}}");
        Assert.Equal("{\"w\": 594.0}", c.Text("01_x"));
        Assert.True(MemoryResultCollector.IsDocumentName("00_TOUT"));
        Assert.Equal(new[] { "00_TOUT", "01_x" }, c.Documents.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Sanitized_strips_control_and_bidi_characters_from_keys_and_values()
    {
        JsonNode node = JsonNode.Parse("{\"a\\u0007b\": \"x\\u202Ey\", \"list\": [\"\\u0001z\", 1.50, true, null]}")!;
        string clean = MemoryResultCollector.Sanitized(node)!.ToJsonString();
        Assert.Equal("{\"ab\":\"xy\",\"list\":[\"z\",1.50,true,null]}", clean);
    }

    // ── presets p1-p3 ────────────────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> Presets() => PresetCatalog.Default.All.Select(p => new object[] { p.Name });

    [Theory]
    [MemberData(nameof(Presets))]
    public void Each_preset_compiles_under_ironpython_3_and_imports_only_json_datetime_and_the_revit_db(string name)
    {
        Preset preset = PresetCatalog.Default.Find(name)!;
        string source = PresetCatalog.Default.Source(preset);
        CompileResult compiled = _fixture.Engine.Compile(source, preset.Name + ".py", ScriptTrust.Shipped);
        Assert.True(compiled.Ok, compiled.Message);

        var imports = Regex.Matches(source, @"^\s*(?:import|from)\s+([\w.]+)(.*)$", RegexOptions.Multiline)
            .Select(m => m.Value.Trim()).ToArray();
        Assert.Equal(new[] { "import json, datetime", "from Autodesk.Revit.DB import *" }, imports);
        Assert.Contains("dump(\"" + preset.Document + "\", data)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("os.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("open(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_catalogue_lists_p1_to_p6_in_the_order_of_audit_all_with_the_document_names_of_the_comparison_chain()
    {
        Assert.Equal(
            new[]
            {
                ("audit.p1", "01_vues_lignes_cartouches"), ("audit.p2", "02_notes_cotations"), ("audit.p3", "03_pochages"),
                ("audit.p4", "04_nomenclatures"), ("audit.p5", "05_parametres"), ("audit.p6", "00_TOUT"),
            },
            PresetCatalog.Default.All.Select(p => (p.Name, p.Document)).ToArray());
        Assert.Equal("audit.p6", Assert.Single(PresetCatalog.Default.All, p => p.Merge).Name);
        Assert.EndsWith("audit.all", string.Join(",", PresetCatalog.Default.Names), StringComparison.Ordinal);
        Assert.Null(PresetCatalog.Default.Find("audit.all"));
        Assert.Throws<ArgumentException>(() => new PresetCatalog(new[] { Inline("audit.all", "00_x", "pass\n") }));
    }

    [Fact]
    public void The_merge_preset_reads_the_collector_and_never_the_file_system()
    {
        string source = PresetCatalog.Default.Source(PresetCatalog.Default.Find("audit.p6")!);
        Assert.Contains("rift_collector.Text(", source, StringComparison.Ordinal);
        foreach (string name in new[] { "01_vues_lignes_cartouches", "02_notes_cotations", "03_pochages", "04_nomenclatures", "05_parametres" })
        {
            Assert.Contains("\"" + name + "\"", source, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("os.path", source, StringComparison.Ordinal);
        Assert.DoesNotContain("io.open", source, StringComparison.Ordinal);
    }

    // ── audit.all ────────────────────────────────────────────────────────────────────────────

    private static PresetCatalog ThreeWithMerge(string? failing = null) => new(new[]
    {
        Inline("audit.a", "01_a", "import json\nreport_progress(50, 'half')\nrift_collector.Put('01_a', json.dumps({'items': [1, 2], '_document': doc.Title}))\n"),
        Inline("audit.b", "02_b", failing ?? "import json\nrift_collector.Put('02_b', json.dumps({'things': [1], 'w': 594.0}))\n"),
        new Preset("audit.m", null, "00_TOUT", "merge")
        {
            Merge = true,
            Source = "import json\nmerged = {}\nfor n in ['01_a', '02_b', '09_missing']:\n    t = rift_collector.Text(n)\n    if t is not None:\n"
                + "        for k, v in json.loads(t).items():\n            if not k.startswith('_'):\n                merged[k] = v\n"
                + "rift_collector.Put('00_TOUT', json.dumps({'resume': dict((k, len(v)) for k, v in merged.items() if isinstance(v, list)), 'data': merged}))\n",
        },
    });

    [Fact]
    public void Audit_all_runs_every_preset_in_order_in_one_job_sharing_one_collector()
    {
        var progress = new List<(int, string)>();
        var router = Router(ThreeWithMerge());
        IToolHandler op = new AuditOp(ThreeWithMerge(), RunOp(), null);
        var request = new RequestEnvelope("2.0", "t", "tool", 60, null, null, new JsonObject { ["name"] = "audit.all" });
        JsonNode? direct = op.Execute("audit.all", request, new SyncProgress(progress), CancellationToken.None);
        Assert.Equal("audit.all", direct!["tool"]!.GetValue<string>());
        Assert.Equal(new[] { "audit.a", "audit.b", "audit.m" }, direct["runs"]!.AsObject().Select(kv => kv.Key));
        Assert.All(direct["runs"]!.AsObject(), kv => Assert.Equal("ok", kv.Value!["outcome"]!.GetValue<string>()));
        Assert.Equal(3, direct["timing"]!["presets"]!.AsObject().Count);
        Assert.True(direct["timing"]!["total_ms"]!.GetValue<long>() >= 0);
        JsonObject documents = direct["documents"]!.AsObject();
        Assert.Equal(new[] { "00_TOUT", "01_a", "02_b" }, documents.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal));
        // IronPython does not promise dict order: compare the entries, not the text
        JsonObject resume = documents["00_TOUT"]!["resume"]!.AsObject();
        Assert.Equal(new[] { ("items", 2), ("things", 1) }, resume.Select(kv => (kv.Key, kv.Value!.GetValue<int>())).OrderBy(e => e.Key, StringComparer.Ordinal));
        Assert.Contains("594.0", documents["00_TOUT"]!["data"]!.ToJsonString(), StringComparison.Ordinal);
        // progress spans the whole job: preset 1 reports 50 % → 16 %, preset 3 reports 100 % → 100 %
        Assert.Contains(progress, p => p.Item1 == 16 && p.Item2 == "audit.a: half");
        Assert.True(progress.Select(p => p.Item1).Zip(progress.Skip(1).Select(p => p.Item1)).All(pair => pair.First <= pair.Second), "progress is monotonic");

        // and through the router (job table, envelope)
        ResponseEnvelope r = Tool(router, "audit.all");
        Assert.Equal("ok", r.Status);
        Assert.Null(r.Result!["run"]);
        Assert.Equal(3, r.Result["runs"]!.AsObject().Count);
        // three presets per job, two jobs on the same host: the guard was armed and disarmed once per preset
        Assert.Equal((6, 6), (_host.Guard.Armed, _host.Guard.Disarmed));
    }

    [Fact]
    public void Audit_all_stops_at_the_first_failing_preset_and_names_it_with_the_earlier_runs()
    {
        ResponseEnvelope r = Tool(Router(ThreeWithMerge(failing: "doc.Commit()\nrift_collector.Put('02_b', '{}')\n")), "audit.all");
        Assert.Equal(ErrorCodes.WriteInQuery, r.Error!.Code);
        JsonNode details = r.Result!;
        Assert.Equal("audit.b", details["preset"]!.GetValue<string>());
        Assert.Equal(new[] { "audit.a" }, details["runs"]!.AsObject().Select(kv => kv.Key));
        Assert.Equal("rolled_back", details["outcome"]!.GetValue<string>());
        Assert.Equal((2, 2), (_host.Guard.Armed, _host.Guard.Disarmed));
    }

    [Fact]
    public void The_merge_preset_on_its_own_sees_an_empty_collector_and_still_dumps_its_document()
    {
        ResponseEnvelope r = Tool(Router(ThreeWithMerge()), "audit.m");
        Assert.Equal("ok", r.Status);
        Assert.Equal("{}", r.Result!["documents"]!["00_TOUT"]!["data"]!.ToJsonString());
    }

    private RunQueryOp RunOp()
    {
        IScriptEngine e = _fixture.Engine;
        IRunHost h = _host;
        return new RunQueryOp(() => e, () => h, null, FileLog.Null);
    }

    private sealed class SyncProgress : IProgress<(int Percent, string Message)>
    {
        private readonly List<(int, string)> _sink;

        public SyncProgress(List<(int, string)> sink)
        {
            _sink = sink;
        }

        public void Report((int Percent, string Message) value) => _sink.Add(value);
    }

    [Fact]
    public void Every_sort_key_of_the_presets_goes_through_str()
    {
        foreach (Preset p in PresetCatalog.Default.All)
        {
            string source = PresetCatalog.Default.Source(p);
            foreach (Match m in Regex.Matches(source, @"key=lambda x: (.+?)\)\s*$", RegexOptions.Multiline))
            {
                string key = m.Groups[1].Value;
                Assert.DoesNotMatch(@"(?<!str\()x(\[|\.get)", key);
            }
        }
    }

    // ── op: tool ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_preset_runs_under_the_read_guard_and_returns_its_document_with_the_number_text_kept()
    {
        var presets = new PresetCatalog(new[]
        {
            Inline("audit.t", "01_test", "import json\nreport_progress(50, 'half')\nrift_collector.Put('01_test', json.dumps({'_document': doc.Title, 'w': 594.0, 'n': 'a\\u0007b', 'l': [1, 2]}))\nprint('dumped')\n"),
        });
        ResponseEnvelope r = Tool(Router(presets), "audit.t");
        Assert.Equal("ok", r.Status);
        Assert.Equal("audit.t", r.Result!["tool"]!.GetValue<string>());
        string doc = r.Result["documents"]!["01_test"]!.ToJsonString();
        Assert.Contains("\"w\":594.0", doc, StringComparison.Ordinal);
        Assert.Contains("\"n\":\"ab\"", doc, StringComparison.Ordinal);
        Assert.Contains("FAKE_SANDBOX", doc, StringComparison.Ordinal);
        JsonNode run = r.Result["run"]!;
        Assert.Equal("ok", run["outcome"]!.GetValue<string>());
        Assert.Null(run["result"]);
        Assert.Equal("dumped\n", run["stdout"]!.GetValue<string>());
        Assert.Equal((1, 1), (_host.Guard.Armed, _host.Guard.Disarmed));
        string request = File.ReadAllText(Path.Combine(_dir, run["run_log"]!.GetValue<string>()["runs/".Length..], "request.json"));
        Assert.Contains("\"tool:audit.t\"", request, StringComparison.Ordinal);
    }

    [Fact]
    public void A_preset_that_writes_is_rolled_back_and_reported_as_write_in_query()
    {
        var presets = new PresetCatalog(new[] { Inline("audit.t", "01_test", "doc.Commit()\nrift_collector.Put('01_test', '{}')\n") });
        ResponseEnvelope r = Tool(Router(presets), "audit.t");
        Assert.Equal(ErrorCodes.WriteInQuery, r.Error!.Code);
        Assert.Null(r.Result!["documents"]);
    }

    [Fact]
    public void An_unknown_tool_is_refused_without_running_anything()
    {
        ResponseEnvelope r = Tool(Router(PresetCatalog.Default), "audit.p9");
        Assert.Equal("refused", r.Status);
        Assert.Equal(ErrorCodes.BadRequest, r.Error!.Code);
        Assert.Contains("audit.p1, audit.p2, audit.p3, audit.p4, audit.p5, audit.p6, audit.all", r.Error.Message, StringComparison.Ordinal);
        Assert.Equal(0, _host.Guard.Armed);
    }

    [Fact]
    public void Without_an_active_document_a_preset_is_refused_before_the_guard_is_armed()
    {
        var host = new Host(withDocument: false);
        ResponseEnvelope r = Tool(Router(PresetCatalog.Default, host), "audit.p1");
        Assert.Equal("refused", r.Status);
        Assert.Equal(ErrorCodes.NoDocument, r.Error!.Code);
        Assert.Equal(0, host.Guard.Armed);
    }

    [Fact]
    public void A_preset_that_dumps_nothing_is_an_internal_error()
    {
        var presets = new PresetCatalog(new[] { Inline("audit.t", "01_test", "x = 1\n") });
        ResponseEnvelope r = Tool(Router(presets), "audit.t");
        Assert.Equal(ErrorCodes.Internal, r.Error!.Code);
        Assert.Contains("01_test", r.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Documents_too_large_for_one_frame_are_journaled_and_returned_by_reference()
    {
        var presets = new PresetCatalog(new[]
        {
            Inline("audit.t", "01_big", "import json\nrift_collector.Put('01_big', json.dumps({'w': 594.0, 'rows': ['x' * 100 for i in range(9000)]}))\n"),
        });
        ResponseEnvelope r = Tool(Router(presets), "audit.t");
        Assert.Equal("ok", r.Status);
        Assert.Null(r.Result!["documents"]);
        Assert.True(r.Result["documents_redacted"]!.GetValue<bool>());
        Assert.True(r.Result["documents_bytes"]!.GetValue<int>() > AuditOp.InlineThresholdBytes);
        string reference = r.Result["documents_ref"]!["01_big"]!.GetValue<string>();
        Assert.StartsWith("runs/", reference, StringComparison.Ordinal);
        string text = File.ReadAllText(Path.Combine(_dir, reference["runs/".Length..]));
        Assert.Contains("\"w\":594.0", text, StringComparison.Ordinal);
        Assert.Equal(9000, JsonNode.Parse(text)!["rows"]!.AsArray().Count);
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
