// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;

namespace Rift.Core.Control;

/// <summary>Where the assistant may change the model during a session.</summary>
public enum WorkScope
{
    /// <summary>SANDBOX copies only (ADR-009, the default policy).</summary>
    Sandbox,

    /// <summary>The project that was active when the user chose, plus any SANDBOX copy.</summary>
    ActiveProject,
}

/// <summary>Whether a kept change waits for the user.</summary>
public enum Supervision
{
    /// <summary>Changes are kept without a dialog.</summary>
    FullAuto,

    /// <summary>Every kept change shows Keep/Discard in Revit first (dry runs and reads never ask).</summary>
    Supervised,
}

/// <summary>The user's answer for one assistant session, given in Revit (never by the assistant).</summary>
/// <param name="ClientId">The <c>session_id</c> of the client that holds the lease.</param>
/// <param name="ProjectPath">File path of the project chosen with <see cref="WorkScope.ActiveProject"/> (null when unsaved).</param>
/// <param name="ProjectTitle">Title of that project, also used when it has no path.</param>
public sealed record SessionChoice(string ClientId, WorkScope Scope, Supervision Supervision, string? ProjectPath, string? ProjectTitle,
    DateTimeOffset ChosenUtc)
{
    /// <summary>"sandbox copies · supervised", "active project · full auto".</summary>
    public string Text() =>
        (Scope == WorkScope.Sandbox ? "sandbox copies" : "active project") + " · " + (Supervision == Supervision.FullAuto ? "full auto" : "supervised");

    /// <summary>True when a document with this path and title is the chosen project.</summary>
    public bool IsProject(string? path, string? title)
    {
        if (Scope != WorkScope.ActiveProject)
        {
            return false;
        }
        if (!string.IsNullOrEmpty(ProjectPath))
        {
            return string.Equals(ProjectPath, path, StringComparison.OrdinalIgnoreCase);
        }
        return string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(ProjectTitle) && string.Equals(ProjectTitle, title, StringComparison.Ordinal);
    }
}

/// <summary>
/// The session question (asked in Revit at the start of each assistant session): a new client session clears the
/// previous answer, the same client keeps it when its lease is granted again. Thread-safe; <see cref="Changed"/> is
/// raised outside the lock.
/// </summary>
public sealed class SessionGate
{
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock;
    private string? _client;
    private SessionChoice? _choice;

    public SessionGate(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Raised when a session starts (choice null: the question must be asked) or is answered.</summary>
    public event Action<string, SessionChoice?>? Changed;

    /// <summary>The client of the current session, or null before the first lease.</summary>
    public string? Client
    {
        get
        {
            lock (_gate)
            {
                return _client;
            }
        }
    }

    /// <summary>The answer of the current session, or null while the question is pending.</summary>
    public SessionChoice? Choice
    {
        get
        {
            lock (_gate)
            {
                return _choice;
            }
        }
    }

    /// <summary>A client obtained the lease. Returns true when this is a new session (the question must be asked).</summary>
    public bool Begin(string clientId)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        lock (_gate)
        {
            if (string.Equals(_client, clientId, StringComparison.Ordinal))
            {
                return false;
            }
            _client = clientId;
            _choice = null;
        }
        Changed?.Invoke(clientId, null);
        return true;
    }

    /// <summary>The user answered in Revit. Ignored (false) when another session started meanwhile.</summary>
    public bool Choose(string clientId, WorkScope scope, Supervision supervision, string? projectPath, string? projectTitle)
    {
        SessionChoice choice;
        lock (_gate)
        {
            if (!string.Equals(_client, clientId, StringComparison.Ordinal))
            {
                return false;
            }
            choice = new SessionChoice(clientId, scope, supervision,
                scope == WorkScope.ActiveProject ? projectPath : null, scope == WorkScope.ActiveProject ? projectTitle : null, _clock());
            _choice = choice;
        }
        Changed?.Invoke(clientId, choice);
        return true;
    }
}
