// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading;

using Rift.Core.Control;
using Rift.Core.Jobs;
using Rift.Core.Json;
using Rift.Core.Lease;
using Rift.Core.Logging;
using Rift.Core.Protocol.Ops;
using Rift.Core.Transport;

namespace Rift.Core.Protocol;

/// <summary>Where the pipe thread hands a job to the Revit API thread (ADR-008/025). Implemented by the Revit dispatcher and by test fakes.</summary>
public interface IJobRunner
{
    /// <summary>Queues the job. Throws <see cref="OperationException"/> (LISTENER_DEAD) when the event handler is unusable.</summary>
    void Enqueue(Job job);

    /// <summary>Why Revit did not start a job in time (modal dialog title…), or null when unknown.</summary>
    string? BusyReason();
}

/// <summary>
/// Decodes the envelope, picks the operation handler, runs it on the pipe thread or as a job on the API thread,
/// and builds the response envelope. Never throws towards the listener.
/// </summary>
public sealed class RequestRouter : IRequestHandler
{
    private readonly Dictionary<string, IOperationHandler> _handlers = new(StringComparer.Ordinal);
    private readonly JobTable _jobs;
    private readonly IJobRunner _apiThread;
    private readonly Func<TargetInfo> _target;
    private readonly FileLog _log;
    private readonly TimeSpan _startTimeout;
    private readonly TimeSpan _defaultTimeout;
    private readonly LeaseManager? _lease;
    private readonly PauseGate? _pause;

    /// <param name="lease">When given, every op other than <c>lease</c> is admitted by the lease (ADR-004): renewed for its holder,
    /// refused with <c>LEASE_HELD</c> for any other client while the lease is live.</param>
    /// <param name="pause">When given and paused (ADR-029 §2), every authenticated request is refused with <c>PAUSED</c> before
    /// the handler lookup and the lease: no handler runs, no lease is taken, no job is created.</param>
    public RequestRouter(IEnumerable<IOperationHandler> handlers, JobTable jobs, IJobRunner apiThread, Func<TargetInfo> target, FileLog log,
        TimeSpan? startTimeout = null, TimeSpan? defaultTimeout = null, LeaseManager? lease = null, PauseGate? pause = null)
    {
        foreach (IOperationHandler h in handlers)
        {
            _handlers[h.Op] = h;
        }
        _jobs = jobs;
        _apiThread = apiThread;
        _target = target;
        _log = log;
        _startTimeout = startTimeout ?? TimeSpan.FromSeconds(30);
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(60);
        _lease = lease;
        _pause = pause;
    }

    /// <summary>
    /// Raised after every handled request with (label, status, error code or null, error message or null). The label is the
    /// MCP tool name sent by the server in <c>client.tool</c>, or the protocol op when absent. Used by the activity card.
    /// </summary>
    public event Action<string, string, string?, string?>? Completed;

    /// <summary>The MCP tool name carried in <c>client.tool</c>, or the op when the client did not send one.</summary>
    public static string LabelOf(RequestEnvelope request)
    {
        if (request.Client is System.Text.Json.Nodes.JsonObject client
            && client["tool"] is JsonValue value
            && value.TryGetValue(out string? tool)
            && !string.IsNullOrWhiteSpace(tool))
        {
            return tool.Length <= 64 ? tool : tool[..64];
        }
        return request.Op;
    }

    private static string? ClientString(RequestEnvelope request, string name) =>
        request.Client is JsonObject client && client[name] is JsonValue value && value.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s)
            ? (s.Length <= 128 ? s : s[..128])
            : null;

    public static RequestEnvelope? TryParse(ReadOnlySpan<byte> body, out string? errorCode)
    {
        errorCode = null;
        RequestEnvelope? req;
        try
        {
            req = JsonCodec.Deserialize<RequestEnvelope>(body);
        }
        catch (System.Text.Json.JsonException)
        {
            errorCode = ErrorCodes.BadRequest;
            return null;
        }
        if (req is null || string.IsNullOrEmpty(req.Op) || string.IsNullOrEmpty(req.V))
        {
            errorCode = ErrorCodes.BadRequest;
            return null;
        }
        if (!SameMajor(req.V, ProtocolVersion.Current))
        {
            errorCode = ErrorCodes.ProtocolMismatch;
            return null;
        }
        return req;
    }

    public ResponseEnvelope Handle(ReadOnlySpan<byte> body, CancellationToken cancellation)
    {
        var sw = Stopwatch.StartNew();
        RequestEnvelope? request = TryParse(body, out string? code);
        if (request is null)
        {
            return ResponseEnvelope.Fail(null, code!, null, SafeTarget(), sw.ElapsedMilliseconds);
        }
        ResponseEnvelope response = Handle(request, sw, cancellation);
        Completed?.Invoke(LabelOf(request), response.Status, response.Error?.Code, response.Error?.Message);
        return response;
    }

    private ResponseEnvelope Handle(RequestEnvelope request, Stopwatch sw, CancellationToken cancellation)
    {
        if (_pause is not null && _pause.State is { State: ServerState.Paused } paused)
        {
            return Paused(request.Id, paused, sw.ElapsedMilliseconds);
        }
        if (!_handlers.TryGetValue(request.Op, out IOperationHandler? handler))
        {
            return ResponseEnvelope.Fail(request.Id, ErrorCodes.BadRequest, "unknown op: " + request.Op, SafeTarget(), sw.ElapsedMilliseconds);
        }
        if (_lease is not null && !string.Equals(request.Op, "lease", StringComparison.Ordinal))
        {
            LeaseResult admitted = _lease.Admit(ClientString(request, "session_id"), ClientString(request, "name"), ClientString(request, "version"));
            if (!admitted.Granted)
            {
                JsonObject? details = admitted.Holder is null ? null : new JsonObject { ["holder"] = LeaseOp.ToJson(admitted.Holder) };
                return ResponseEnvelope.Refuse(request.Id, ErrorCodes.LeaseHeld, "held by " + (admitted.Holder?.Name ?? "another client"), details,
                    SafeTarget(), sw.ElapsedMilliseconds);
            }
        }
        try
        {
            if (!handler.NeedsApiThread)
            {
                JsonNode? result = handler.Execute(request, new Progress<(int, string)>(), cancellation);
                return new ResponseEnvelope(ProtocolVersion.Current, request.Id, ResponseEnvelope.Ok, SafeTarget(), result, null, sw.ElapsedMilliseconds);
            }
            if (_pause is not null && _pause.State is { State: ServerState.Paused } pausedSince)
            {
                // Paused between the gate and here: no job is created.
                return Paused(request.Id, pausedSince, sw.ElapsedMilliseconds);
            }
            Job job = _jobs.Create(request.Op, (progress, ct) => handler.Execute(request, progress, ct));
            _apiThread.Enqueue(job);
            if (!job.WaitStarted(_startTimeout))
            {
                string reason = _apiThread.BusyReason() ?? "Revit did not start the job in time";
                // A job the client is told failed must never run later behind its back: cancel it unless it just started.
                if (JobTable.TryCancelBeforeStart(job, new ErrorInfo(ErrorCodes.RevitBusy, null, reason)))
                {
                    return ResponseEnvelope.Fail(request.Id, ErrorCodes.RevitBusy, reason, SafeTarget(), sw.ElapsedMilliseconds,
                        new JsonObject { ["busy_reason"] = reason, ["job_id"] = job.Id, ["cancelled"] = true });
                }
            }
            TimeSpan timeout = request.TimeoutS > 0 ? TimeSpan.FromSeconds(request.TimeoutS) : _defaultTimeout;
            if (!job.WaitFinished(timeout))
            {
                var accepted = new JsonObject { ["job_id"] = job.Id, ["progress"] = job.Progress.Percent, ["message"] = job.Progress.Message };
                return new ResponseEnvelope(ProtocolVersion.Current, request.Id, ResponseEnvelope.Accepted, SafeTarget(), accepted, null, sw.ElapsedMilliseconds);
            }
            return FromJob(request.Id, job, sw.ElapsedMilliseconds);
        }
        catch (OperationException ex)
        {
            return ex.Refused
                ? ResponseEnvelope.Refuse(request.Id, ex.Code, ex.Message, ex.Details, SafeTarget(), sw.ElapsedMilliseconds)
                : ResponseEnvelope.Fail(request.Id, ex.Code, ex.Message, SafeTarget(), sw.ElapsedMilliseconds, ex.Details);
        }
        catch (Exception ex)
        {
            _log.Write("router: " + request.Op + " failed: " + ex);
            return ResponseEnvelope.Fail(request.Id, ErrorCodes.Internal, ex.GetType().Name + ": " + ex.Message, SafeTarget(), sw.ElapsedMilliseconds);
        }
    }

    /// <summary>Builds the response of a finished (or still running) job; used by the <c>job</c> operation too.</summary>
    public ResponseEnvelope FromJob(string? requestId, Job job, long elapsedMs)
    {
        if (job.State != JobState.Done)
        {
            var running = new JsonObject { ["job_id"] = job.Id, ["progress"] = job.Progress.Percent, ["message"] = job.Progress.Message };
            return new ResponseEnvelope(ProtocolVersion.Current, requestId, ResponseEnvelope.Running, SafeTarget(), running, null, elapsedMs);
        }
        if (job.Error is not null)
        {
            return job.Refused
                ? ResponseEnvelope.Refuse(requestId, job.Error.Code, job.Error.Message, job.Details, SafeTarget(), elapsedMs)
                : new ResponseEnvelope(ProtocolVersion.Current, requestId, ResponseEnvelope.ErrorStatus, SafeTarget(), job.Details, job.Error, elapsedMs);
        }
        return new ResponseEnvelope(ProtocolVersion.Current, requestId, ResponseEnvelope.Ok, SafeTarget(), job.Result, null, elapsedMs);
    }

    private ResponseEnvelope Paused(string? requestId, PauseInfo paused, long elapsedMs)
    {
        var details = new JsonObject
        {
            ["paused_since_utc"] = paused.SinceUtc?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        };
        return ResponseEnvelope.Refuse(requestId, ErrorCodes.Paused, PauseGate.Message, details, SafeTarget(), elapsedMs);
    }

    private TargetInfo? SafeTarget()
    {
        try
        {
            return _target();
        }
        catch (Exception ex)
        {
            _log.Write("target info failed: " + ex.Message);
            return null;
        }
    }

    private static bool SameMajor(string a, string b)
    {
        int ia = a.IndexOf('.', StringComparison.Ordinal);
        int ib = b.IndexOf('.', StringComparison.Ordinal);
        string ma = ia < 0 ? a : a[..ia];
        string mb = ib < 0 ? b : b[..ib];
        return string.Equals(ma, mb, StringComparison.Ordinal);
    }
}
