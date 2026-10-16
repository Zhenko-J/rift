// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;

using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using Rift.Core;
using Rift.Core.Config;
using Rift.Core.Control;
using Rift.Core.Discovery;
using Rift.Core.Jobs;
using Rift.Core.Journal;
using Rift.Core.Lease;
using Rift.Core.Logging;
using Rift.Core.Protocol;
using Rift.Core.Protocol.Ops;
using Rift.Core.Security;
using Rift.Core.Transport;
using Rift.Revit.Dispatch;
using Rift.Revit.Ops;
using Rift.Revit.Ui;
using Rift.Scripting;
using Rift.Scripting.Presets;
using Rift.Scripting.Python;
using Rift.Scripting.Runs;
using Rift.Scripting.Tools;
using Rift.Ui;

namespace Rift.Revit;

/// <summary>
/// Entry point of the isolated side (ADR-021), instantiated by Rift.Addin through reflection. Assembles the
/// configuration, the secret, the discovery file, the dispatcher with its busy probe, the script engine (created off the UI
/// thread, timeout self-check included), the run journal, the router (lease, job, context, compile, run, tool), the listener
/// and the activity card; M2 adds the write guard, the Keep/Discard dialog and the per-document history.
/// </summary>
public sealed class Host : IDisposable
{
    private static readonly string[] s_capabilities = { "context", "lease", "job", "compile", "run", "tool" };
    private static readonly string s_root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Brand.Id);
    private static int s_generation;

    private FileLog _log = FileLog.Null;
    private SecretStore? _secrets;
    private DiscoveryFile? _discovery;
    private PipeListener? _listener;
    private RevitDispatcher? _dispatcher;
    private LeaseManager? _lease;
    private PauseGate? _pause;
    private ActivityCard? _card;
    private RiftConfig _config = RiftConfig.Default;
    private Lazy<IronPythonEngine>? _engine;
    private RunJournal? _journal;
    private InstanceInfo? _info;
    private string _version = "?";
    private string? _lastRefusal;
    private long _lastUnauthorizedCardTicks = long.MinValue / 2;

    /// <param name="bootstrapPath">Path of <c>Rift.Addin.dll</c>, which holds the ribbon commands (ADR-029 §1).</param>
    public void Start(UIControlledApplication app, string bootstrapPath)
    {
        int pid = Environment.ProcessId;
        _log = new FileLog(Path.Combine(s_root, "logs", "addin-" + pid.ToString(CultureInfo.InvariantCulture) + ".log"));
        _version = typeof(Host).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0";
        int generation = ++s_generation;
        _log.Write("start revit=" + app.ControlledApplication.VersionNumber + " build=" + app.ControlledApplication.VersionBuild
            + " addin=" + _version + " generation=" + generation.ToString(CultureInfo.InvariantCulture)
            + " alc=" + (System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(Host).Assembly)?.Name ?? "?")
            + " revitapi_alc=" + (System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(UIControlledApplication).Assembly)?.Name ?? "?"));

        _config = RiftConfig.Load(Path.Combine(s_root, "config.json"), _log);
        try
        {
            Ribbon.Build(app, bootstrapPath, _config.Developer, _log);
        }
        catch (Exception ex)
        {
            // The ribbon is a convenience: the listener must start even if the panel cannot be built.
            _log.Write("ribbon unavailable: " + ex);
        }
        try
        {
            _card = new ActivityCard(app.MainWindowHandle, _log) { Enabled = _config.ActivityCard };
            _log.Write("activity card created on thread " + Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            _log.Write("activity card unavailable: " + ex);
            _card = null;
        }

        var busy = new BusyProbe(app.MainWindowHandle);
        _dispatcher = new RevitDispatcher(generation, _log) { BusyProbe = busy.Reason };
        _dispatcher.Create();

        StartEngine();
        _journal = new RunJournal(Path.Combine(s_root, "runs"), RedactionContext.Current, TimeSpan.FromDays(14), _log);
        RunJournal journal = _journal;
        _ = Task.Run(() => _log.Write("journal purge: " + journal.Purge().ToString(CultureInfo.InvariantCulture) + " run folder(s) deleted"));

        _discovery = new DiscoveryFile(Path.Combine(s_root, "instances"), _log);
        int purged = _discovery.PurgeDeadPids(IsAlive);
        _secrets = new SecretStore(_discovery.KeyPathFor(pid), _log);

        _lease = new LeaseManager(TimeSpan.FromSeconds(60));
        _lease.Granted += holder =>
        {
            _log.Write("lease granted to " + holder.Name + " " + holder.Version);
            EnqueueUi(_ => _card?.Show(CardContent.Connected(holder.Name + " " + holder.Version)));
        };

        var jobs = new JobTable(TimeSpan.FromMinutes(10));
        // The pause (ADR-029 §2): the ribbon toggles it; the router refuses every call with PAUSED while it holds.
        _pause = new PauseGate(_lease, jobs);
        _pause.Changed += p =>
        {
            bool paused = p.State == ServerState.Paused;
            _log.Write(paused ? "server paused by " + p.By : "server resumed");
            EnqueueUi(_ =>
            {
                Ribbon.SetPaused(paused, p.SinceUtc);
                _card?.Show(paused ? CardContent.Paused(p) : CardContent.Resumed());
            });
        };
        Func<IScriptEngine> engine = Engine;
        Func<IRunHost> runHost = () => RevitRunHost.Current(() => _config, _log);
        var runQuery = new RunQueryOp(engine, runHost, _journal, _log);
        var router = new RequestRouter(
            new IOperationHandler[]
            {
                new LeaseOp(_lease),
                new JobOp(jobs),
                new ContextOp(_version, () => _config, EngineState),
                new CompileOp(engine),
                runQuery,
                new ToolOp(new IToolHandler[]
                {
                    new AuditOp(PresetCatalog.Default, runQuery, _journal),
                    new HistoryOp(_journal, runHost),
                    new TypedToolOp(TypedToolCatalog.Default, runQuery),
                }),
            },
            jobs, _dispatcher, () => new TargetInfo(pid, _secrets.Generation, null), _log,
            startTimeout: TimeSpan.FromSeconds(_config.StartTimeoutS), lease: _lease, pause: _pause);
        router.Completed += (label, status, code, message) =>
        {
            if (code == ErrorCodes.LeaseHeld)
            {
                _lastRefusal = code + " (" + label + ") at " + Now();
                _log.Write("refused " + label + ": " + code + " - " + message);
            }
            // A successful lease is announced by Granted ("Assistant connected"); everything else reaches the card,
            // refusals included, with the same code and message as the client (ADR-023).
            if (label != "lease" || code is not null)
            {
                EnqueueUi(_ => _card?.Show(CardContent.Result(label, status, code, message)));
            }
        };

        var auth = new RequestAuthenticator(_secrets, new NonceCache(ProtocolVersion.NonceLifetime));
        _listener = new PipeListener(PipeListener.DefaultName(pid), auth, router, _log);
        _listener.Unauthorized += OnUnauthorized;
        _listener.Start();

        DateTimeOffset started;
        using (Process me = Process.GetCurrentProcess())
        {
            started = new DateTimeOffset(me.StartTime.ToUniversalTime());
        }
        _info = new InstanceInfo(pid, started, "pipe", _listener.PipeName, app.ControlledApplication.VersionNumber,
            app.ControlledApplication.VersionBuild, "ironpython-3.4", _version, _secrets.Generation, ProtocolVersion.Current,
            s_capabilities, DateTimeOffset.UtcNow);
        string path = _discovery.Write(_info);
        // Ten consecutive authentication failures rotate the secret (ADR-002/003): the discovery file follows the new
        // generation so that a client re-reading it knows the key file changed.
        _secrets.Rotated += OnRotated;
        _log.Write("listening on \\\\.\\pipe\\" + _listener.PipeName + " discovery=" + path + " purged=" + purged.ToString(CultureInfo.InvariantCulture));
    }

    public void Stop()
    {
        if (_secrets is not null)
        {
            _secrets.Rotated -= OnRotated;
        }
        _listener?.Dispose();
        _listener = null;
        _discovery?.Delete(Environment.ProcessId);
        _secrets?.Delete();
        _card?.Dispose();
        _card = null;
        if (_engine is { IsValueCreated: true } created)
        {
            try
            {
                created.Value.Dispose();
            }
            catch (Exception ex)
            {
                _log.Write("engine dispose failed: " + ex.Message);
            }
        }
        _log.Write("stop");
    }

    public void Dispose() => Stop();

    /// <summary>Called by the Status ribbon button (which lives in the bootstrap assembly).</summary>
    public string Status()
    {
        PauseInfo? pause = _pause?.State;
        string server = pause is { State: ServerState.Paused }
            ? "paused by you since " + (pause.SinceUtc?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "?") + " (every call gets PAUSED)"
            : "on";
        return StatusReport.Format(_version, _listener?.PipeName, _listener?.Served ?? 0, _lease?.Holder, _lease?.Until,
            _secrets?.Generation ?? 0, _lastRefusal, _card is null ? "unavailable" : _card.Enabled ? "on" : "off", _log.Path, EngineLine(), server);
    }

    /// <summary>
    /// The single entry point of the ribbon commands (UI gate 3), relayed by the bootstrap through reflection:
    /// <c>server.toggle</c> pauses or resumes. <paramref name="arg"/> is the command's UIApplication. Never throws;
    /// returns an error text for the bootstrap to show, or null.
    /// </summary>
    public string? Invoke(string command, object? arg)
    {
        try
        {
            switch (command)
            {
                case "server.toggle":
                    if (_pause is null)
                    {
                        return Brand.Product + ": the server did not start (see the add-in log)";
                    }
                    PauseInfo now = _pause.Toggle("you");
                    // The ribbon also follows Changed through EnqueueUi; the command context is a valid API context, so the
                    // button changes at once (UI gate 3, uncertainty 4).
                    Ribbon.SetPaused(now.State == ServerState.Paused, now.SinceUtc);
                    return null;
                default:
                    return Brand.Product + ": unknown command " + command;
            }
        }
        catch (Exception ex)
        {
            _log.Write("invoke " + command + " failed: " + ex);
            return Brand.Product + ": " + command + " failed: " + ex.Message;
        }
    }

    /// <summary>Whether the activity card is shown (the Status dialog offers the switch).</summary>
    public bool ActivityCardEnabled => _card?.Enabled ?? _config.ActivityCard;

    /// <summary>Path of this session's add-in log (shown and copied by the Status dialog).</summary>
    public string? LogPath => _log.Path;

    /// <summary>Switches the activity card and remembers the choice in <c>config.json</c> (ADR-023).</summary>
    public void SetActivityCard(bool enabled)
    {
        if (_card is not null)
        {
            _card.Enabled = enabled;
            if (!enabled)
            {
                _card.Hide();
            }
        }
        _config = _config with { ActivityCard = enabled };
        try
        {
            _config.Save(Path.Combine(s_root, "config.json"));
            _log.Write("activity card " + (enabled ? "on" : "off") + " (saved)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write("activity card " + (enabled ? "on" : "off") + " (config.json not saved: " + ex.Message + ")");
        }
    }

    /// <summary>J0 prototype P-D4, called by the "Probe Python" ribbon button with the command's UIApplication.</summary>
    public string ProbePython(object uiapp)
    {
        try
        {
            string report = Rift.Revit.ProbePython.Run((UIApplication)uiapp);
            _log.Write("probe python:\n" + report);
            return report;
        }
        catch (Exception ex)
        {
            _log.Write("probe python failed: " + ex);
            return "probe failed: " + ex;
        }
    }

    /// <summary>
    /// Creates the IronPython engine on a worker thread (≈ 0.5 s, kept off Revit's start-up): the engine counts trace events
    /// itself and refuses every run when the cooperative timeout cannot work (ADR-016, ADR-026 §7).
    /// </summary>
    private void StartEngine()
    {
        string baseDir = Path.GetDirectoryName(typeof(Host).Assembly.Location) ?? string.Empty;
        string lib = Path.Combine(baseDir, "lib");
        FileLog log = _log;
        _engine = new Lazy<IronPythonEngine>(() =>
        {
            var e = new IronPythonEngine(new[] { typeof(Document).Assembly, typeof(UIDocument).Assembly }, Directory.Exists(lib) ? lib : null);
            log.Write("engine " + e.EngineVersion + " trace events before=" + e.TraceEventsCompiledBefore.ToString(CultureInfo.InvariantCulture)
                + " after=" + e.TraceEventsCompiledAfter.ToString(CultureInfo.InvariantCulture)
                + " timeout_available=" + e.TimeoutAvailable + " late_compile=" + e.LateCompile + " stdlib=" + Directory.Exists(lib));
            return e;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
        Lazy<IronPythonEngine> lazy = _engine;
        _ = Task.Run(() =>
        {
            try
            {
                _ = lazy.Value;
            }
            catch (Exception ex)
            {
                log.Write("engine creation failed: " + ex);
            }
        });
    }

    private IScriptEngine Engine() => (_engine ?? throw new InvalidOperationException("engine not started")).Value;

    private JsonNode? EngineState()
    {
        if (_engine is not { IsValueCreated: true } lazy)
        {
            return new JsonObject { ["name"] = "ironpython-3.4", ["state"] = "starting" };
        }
        IronPythonEngine e = lazy.Value;
        return new JsonObject
        {
            ["name"] = "ironpython-3.4",
            ["state"] = "ready",
            ["timeout_available"] = e.TimeoutAvailable,
            ["late_compile"] = e.LateCompile,
        };
    }

    private string EngineLine()
    {
        if (_engine is not { IsValueCreated: true } lazy)
        {
            return "starting";
        }
        IronPythonEngine e = lazy.Value;
        return "IronPython " + e.EngineVersion + (e.TimeoutAvailable ? ", timeout available" : ", TIMEOUT UNAVAILABLE (runs refused)");
    }

    /// <summary>Queues a card update on the API thread; never throws towards the pipe thread (a dead event only loses the card).</summary>
    private void EnqueueUi(Action<UIApplication> action)
    {
        try
        {
            _dispatcher?.EnqueueUi(action);
        }
        catch (OperationException ex)
        {
            _log.Write("card update dropped: " + ex.Code);
        }
    }

    private void OnUnauthorized(AuthResult reason)
    {
        _lastRefusal = ErrorCodes.Unauthorized + " (" + reason + ") at " + Now();
        // A flood of bad frames must not flood Revit with external events: at most one card every 2 s.
        long now = Environment.TickCount64;
        long last = System.Threading.Interlocked.Read(ref _lastUnauthorizedCardTicks);
        if (now - last < 2000 || System.Threading.Interlocked.CompareExchange(ref _lastUnauthorizedCardTicks, now, last) != last)
        {
            return;
        }
        string message = reason.ToString();
        EnqueueUi(_ => _card?.Show(CardContent.Result("request", ResponseEnvelope.ErrorStatus, ErrorCodes.Unauthorized, message)));
    }

    private void OnRotated(int generation)
    {
        InstanceInfo? info = _info;
        if (info is null || _discovery is null)
        {
            return;
        }
        try
        {
            _info = info with { Generation = generation, WrittenUtc = DateTimeOffset.UtcNow };
            _discovery.Write(_info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write("discovery rewrite after rotation failed: " + ex.Message);
        }
    }

    private static string Now() => DateTimeOffset.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static bool IsAlive(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
