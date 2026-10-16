// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading;

using Autodesk.Revit.UI;

using Rift.Core;
using Rift.Core.Jobs;
using Rift.Core.Logging;
using Rift.Core.Protocol;

namespace Rift.Revit.Dispatch;

/// <summary>
/// The single <see cref="IExternalEventHandler"/> of the add-in (ADR-008/025). Created in <c>Host.Start</c> (valid API
/// context), raised from the pipe thread, executed by Revit on the API thread when it is idle. <see cref="Execute"/>
/// never lets an exception escape: Revit would refuse the handler for the rest of the session.
/// </summary>
internal sealed class RevitDispatcher : IExternalEventHandler, IJobRunner
{
    private readonly ConcurrentQueue<Job> _jobs = new();
    private readonly ConcurrentQueue<Action<UIApplication>> _uiActions = new();
    private readonly FileLog _log;
    private readonly int _generation;
    private readonly string _alc;
    private ExternalEvent? _event;

    public RevitDispatcher(int generation, FileLog log)
    {
        _generation = generation;
        _log = log;
        _alc = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(RevitDispatcher).Assembly)?.Name ?? "?";
    }

    /// <summary>The UIApplication of the job being executed; valid on the API thread, inside <see cref="Execute"/> only.</summary>
    [ThreadStatic]
    private static UIApplication? s_current;

    public static UIApplication Current => s_current ?? throw new InvalidOperationException("not on the Revit API thread");

    public int Generation => _generation;

    /// <summary>Why the last job did not start (filled by the busy probe at M1 tranche 3); null in tranche 1.</summary>
    public Func<string?>? BusyProbe { get; set; }

    public void Create()
    {
        _event = ExternalEvent.Create(this);
    }

    public void Enqueue(Job job)
    {
        _jobs.Enqueue(job);
        Raise();
    }

    public void EnqueueUi(Action<UIApplication> action)
    {
        _uiActions.Enqueue(action);
        Raise();
    }

    public string? BusyReason()
    {
        try
        {
            return BusyProbe?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Write("busy probe failed: " + ex.Message);
            return null;
        }
    }

    public void Execute(UIApplication app)
    {
        try
        {
            s_current = app;
            while (_uiActions.TryDequeue(out Action<UIApplication>? ui))
            {
                try
                {
                    ui(app);
                }
                catch (Exception ex)
                {
                    _log.Write("ui action failed: " + ex);
                }
            }
            while (_jobs.TryDequeue(out Job? job))
            {
                Run(job);
            }
        }
        catch (Exception ex)
        {
            _log.Write("dispatcher: unexpected: " + ex);
        }
        finally
        {
            s_current = null;
        }
    }

    public string GetName() => Brand.Product;

    private void Run(Job job)
    {
        // One line per job: proves on every request that Execute runs in the isolated context (tranche 1 acceptance, ADR-021).
        if (!JobTable.MarkStarted(job))
        {
            _log.Write("skip op=" + job.Op + " job=" + job.Id + " (cancelled before it started: " + (job.Error?.Code ?? "?") + ")");
            return;
        }
        _log.Write("execute op=" + job.Op + " job=" + job.Id + " alc=" + _alc
            + " thread=" + Environment.CurrentManagedThreadId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            var progress = new Progress<(int Percent, string Message)>(job.Report);
            JsonNode? result = job.Work(progress, job.Token);
            JobTable.MarkDone(job, result, null);
        }
        catch (OperationException ex)
        {
            JobTable.MarkDone(job, null, new ErrorInfo(ex.Code, null, ex.Message), ex.Refused, ex.Details);
        }
        catch (Exception ex)
        {
            _log.Write("job " + job.Id + " (" + job.Op + ") failed: " + ex);
            JobTable.MarkDone(job, null, new ErrorInfo(ErrorCodes.Internal, ex.GetType().Name, ex.Message, ex.ToString()));
        }
    }

    private void Raise()
    {
        ExternalEvent? ev = _event;
        if (ev is null)
        {
            throw new OperationException(ErrorCodes.ListenerDead, "external event not created");
        }
        ExternalEventRequest r = ev.Raise();
        if (r == ExternalEventRequest.Denied)
        {
            _log.Write("ExternalEvent.Raise → Denied: the handler is dead for this session");
            throw new OperationException(ErrorCodes.ListenerDead, "Revit refused the external event (reload required)");
        }
        if (r == ExternalEventRequest.TimedOut)
        {
            r = ev.Raise();
            _log.Write("ExternalEvent.Raise → TimedOut, retry → " + r);
        }
    }
}
