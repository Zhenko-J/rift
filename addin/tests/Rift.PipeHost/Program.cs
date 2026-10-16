// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Rift.Core.Discovery;
using Rift.Core.Journal;
using Rift.Core.Jobs;
using Rift.Core.Lease;
using Rift.Core.Logging;
using Rift.Core.Protocol;
using Rift.Core.Protocol.Ops;
using Rift.Core.Security;
using Rift.Core.Transport;
using Rift.Scripting;
using Rift.Scripting.Python;
using Rift.Scripting.Presets;
using Rift.Scripting.Runs;

namespace Rift.PipeHost;

/// <summary>What a script sees as <c>doc</c> in PipeHost (public: IronPython only binds members of public types).</summary>
public sealed class FakeDocument
{
    private readonly FakeGuard _guard;

    internal FakeDocument(FakeGuard guard)
    {
        _guard = guard;
    }

    public string Title => "FAKE_SANDBOX";

    public void Commit() => _guard.Transactions++;

    public void Save() => _guard.Saves++;
}

/// <summary>Write guard stand-in (M2): <c>doc.Commit()</c> counts as one modified element of category "Views".</summary>
public sealed class FakeWriteGuard : IWriteGuard
{
    private readonly FakeGuard _counters;

    internal FakeWriteGuard(FakeGuard counters)
    {
        _counters = counters;
    }

    public ChangeReceipt Changes => ChangeReceipt.From(
        Array.Empty<(long, string, string?)>(),
        Enumerable.Range(1, _counters.Transactions).Select(i => ((long)i, "Views", (string?)("view " + i))),
        Array.Empty<long>(),
        _counters.Transactions);

    public bool FailureRolledBack => false;

    public IReadOnlyList<string> Dialogs { get; } = Array.Empty<string>();

    public void Arm(string title)
    {
        _counters.Transactions = 0;
        _counters.Saves = 0;
    }

    public GuardReport Disarm(IReadOnlyList<object> leftovers, bool keep) =>
        new(_counters.Transactions > 0, _counters.Transactions, _counters.Saves > 0 ? new[] { "save" } : Array.Empty<string>(),
            keep ? Array.Empty<string>() : new[] { "target" }, "group", Array.Empty<string>()) { Kept = keep };
}

public sealed class FakeGuard : IRunGuard
{
    public int Transactions { get; set; }

    public int Saves { get; set; }

    public void Arm()
    {
        Transactions = 0;
        Saves = 0;
    }

    public GuardReport Disarm(IReadOnlyList<object> leftovers) =>
        new(Transactions > 0 || Saves > 0, Transactions, Saves > 0 ? new[] { "save" } : Array.Empty<string>(), new[] { "document" }, "group", Array.Empty<string>());
}

/// <summary>
/// Development host (ADR-021): the real PipeListener, authenticator (window, nonces, rotation), secret store, discovery
/// file, router with the lease and job table, with a fake Revit API thread and a <see cref="FakeHandler"/> answering
/// canned <c>context</c> and <c>stats</c> (how many times the fake ran: proves that refused frames execute nothing), and the
/// real <c>compile</c>/<c>run</c> operations with the real IronPython engine over a fake document and a fake read guard
/// (<c>doc.Commit()</c> stands for a committed transaction, <c>doc.Save()</c> for a blocked save).
/// Prints PIPE, SECRET (hex), PID, DIR (instances directory) then READY on stdout so that the real CPython client can drive
/// it (CI integration test, plan test 21). Deletes its <c>.json</c>/<c>.key</c> on exit, like <c>Host.Stop</c>. Not distributed.
/// Usage: Rift.PipeHost [seconds-to-live (default 30)] [instances-dir (default: a temp folder)].
/// </summary>
internal static class Program
{
    private static readonly string[] s_capabilities = { "context", "lease", "job", "compile", "run", "tool" };

    /// <summary>
    /// Stand-ins for the audit presets (the real ones import the Revit API): same tool name and document name as p1, a float
    /// whose text must survive (594.0) and a control character the sanitiser must strip; and a merge step like p6 that reads
    /// the p1 document from the collector, so that <c>audit.all</c> is exercised end to end.
    /// </summary>
    private static readonly PresetCatalog s_presets = new(new[]
    {
        new Preset("audit.p1", null, "01_vues_lignes_cartouches", "audit p1 (PipeHost stand-in)")
        {
            Source = "import json, datetime\n"
                + "report_progress(50, 'title blocks')\n"
                + "data = {'cartouches': [{'id': 7, 'type': 'A1\\u0007', 'width_mm': 594.0}], '_document': doc.Title,"
                + " '_generated': str(datetime.datetime.now())}\n"
                + "rift_collector.Put('01_vues_lignes_cartouches', json.dumps(data, ensure_ascii=False))\n",
        },
        new Preset("audit.p6", null, "00_TOUT", "audit p6 (PipeHost stand-in)")
        {
            Merge = true,
            Source = "import json, datetime\n"
                + "merged = {}\n"
                + "text = rift_collector.Text('01_vues_lignes_cartouches')\n"
                + "if text is not None:\n"
                + "    for k, v in json.loads(text).items():\n"
                + "        if not k.startswith('_'):\n"
                + "            merged[k] = v\n"
                + "data = {'document': doc.Title, 'generated': str(datetime.datetime.now()), 'langue': 'fake',"
                + " 'resume': dict((k, len(v)) for k, v in merged.items() if isinstance(v, list)), 'data': merged}\n"
                + "rift_collector.Put('00_TOUT', json.dumps(data, ensure_ascii=False))\n",
        },
    });

    private sealed class FakeRunHost : IRunHost
    {
        private readonly FakeGuard _guard = new();

        public FakeRunHost()
        {
            Target = new RunTarget(new object(), new object(), null, new FakeDocument(_guard), "FAKE_SANDBOX", @"C:\work\FAKE_SANDBOX.rvt", "sandbox");
        }

        public RunTarget Target { get; }

        public IReadOnlyDictionary<string, object?> Extras { get; } = new Dictionary<string, object?> { ["System"] = new ScriptNamespace("System") };

        public ForeignConverter? Converter => null;

        public IRunGuard CreateGuard() => _guard;

        public IWriteGuard CreateWriteGuard() => new FakeWriteGuard(_guard);
    }

    private sealed class FakeApiThread : IJobRunner
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

    /// <summary>Canned answers in place of the Revit operations; counts executions.</summary>
    private sealed class FakeHandler : IOperationHandler
    {
        private static int s_contextRuns;

        public FakeHandler(string op)
        {
            Op = op;
        }

        public string Op { get; }

        public bool NeedsApiThread => Op == "context";

        public JsonNode? Execute(RequestEnvelope request, IProgress<(int Percent, string Message)> progress, CancellationToken cancellation)
        {
            if (Op == "stats")
            {
                return new JsonObject { ["context_runs"] = Volatile.Read(ref s_contextRuns) };
            }
            Interlocked.Increment(ref s_contextRuns);
            return new JsonObject
            {
                ["revit"] = new JsonObject { ["version"] = "fake", ["build"] = "PipeHost" },
                ["document"] = new JsonObject { ["title"] = "FAKE_SANDBOX", ["path"] = null },
                ["echo"] = request.Args?.DeepClone(),
            };
        }
    }

    private static int Main(string[] args)
    {
        int ttl = args.Length > 0 && int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out int s) ? s : 30;
        int pid = Environment.ProcessId;
        string dir = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "rift-pipehost-" + pid.ToString(CultureInfo.InvariantCulture));
        var log = new FileLog(Path.Combine(dir, "pipehost-" + pid.ToString(CultureInfo.InvariantCulture) + ".log"));
        var discovery = new DiscoveryFile(dir, log);
        var secrets = new SecretStore(discovery.KeyPathFor(pid), log);
        var auth = new RequestAuthenticator(secrets, new NonceCache(ProtocolVersion.NonceLifetime));
        var jobs = new JobTable(TimeSpan.FromMinutes(5));
        var lease = new LeaseManager(TimeSpan.FromSeconds(60));
        var pause = new Rift.Core.Control.PauseGate(lease, jobs);
        using var engine = new IronPythonEngine(Array.Empty<System.Reflection.Assembly>(), Path.Combine(AppContext.BaseDirectory, "lib"));
        var journal = new RunJournal(Path.Combine(dir, "runs"), RedactionContext.Current, TimeSpan.FromDays(1), log);
        var host = new FakeRunHost();
        var runQuery = new RunQueryOp(() => engine, () => host, journal, log);
        var router = new RequestRouter(
            new IOperationHandler[]
            {
                new LeaseOp(lease), new JobOp(jobs), new FakeHandler("context"), new FakeHandler("stats"),
                new CompileOp(() => engine), runQuery, new ToolOp(new IToolHandler[] { new AuditOp(s_presets, runQuery, journal), new HistoryOp(journal, () => host) }),
            },
            jobs, new FakeApiThread(), () => new TargetInfo(pid, secrets.Generation, null), log, lease: lease, pause: pause);
        // RIFT_PIPEHOST_PAUSED=1: start paused, as if the user had clicked the ribbon toggle (UI tranche 1, test 22).
        if (Environment.GetEnvironmentVariable("RIFT_PIPEHOST_PAUSED") == "1")
        {
            pause.Pause("you");
        }
        using var listener = new PipeListener("rift-pipehost-" + pid.ToString(CultureInfo.InvariantCulture), auth, router, log);

        DateTimeOffset started;
        using (Process me = Process.GetCurrentProcess())
        {
            started = new DateTimeOffset(me.StartTime.ToUniversalTime());
        }
        var info = new InstanceInfo(pid, started, "pipe", listener.PipeName, "fake", "PipeHost", "none", "0.0.0-pipehost",
            secrets.Generation, ProtocolVersion.Current, s_capabilities, DateTimeOffset.UtcNow);
        discovery.Write(info);
        secrets.Rotated += generation => discovery.Write(info with { Generation = generation, WrittenUtc = DateTimeOffset.UtcNow });
        listener.Start();

        Console.WriteLine("PIPE " + listener.PipeName);
        Console.WriteLine("SECRET " + Convert.ToHexString(secrets.Current).ToLowerInvariant());
        Console.WriteLine("PID " + pid.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine("DIR " + dir);
        Console.WriteLine("READY");
        Console.Out.Flush();
        Thread.Sleep(TimeSpan.FromSeconds(ttl));
        Console.WriteLine("SERVED " + listener.Served.ToString(CultureInfo.InvariantCulture));
        discovery.Delete(pid);
        secrets.Delete();
        return 0;
    }
}
