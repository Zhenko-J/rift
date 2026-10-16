// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System.Globalization;

using Rift.Core.Control;
using Rift.Core.Protocol;

namespace Rift.Ui;

/// <summary>The pastille of the card (UI gate 1): full ink = done, accent = running, full red = failed or refused, hollow = paused.</summary>
public enum PastilleState
{
    Done,
    Running,
    Failed,
    Paused,
}

/// <summary>
/// What the activity card shows (ADR-023, UI gate 1): a pastille, a micro-label (the tool), a lower-case aside without a
/// final period, and on failure only the code and the message the assistant received. Never script text, never model data.
/// </summary>
public sealed record CardContent(PastilleState State, string Label, string Aside, string? Code, string? Message)
{
    /// <summary>A client obtained the lease.</summary>
    public static CardContent Connected(string client) =>
        new(PastilleState.Done, "assistant", "assistant connected — " + Lower(client), null, null);

    /// <summary>A request ended (or became a job) with <paramref name="status"/>; refusals by the pause show the hollow pastille.</summary>
    public static CardContent Result(string label, string status, string? code, string? message)
    {
        if (code == ErrorCodes.Paused)
        {
            return new CardContent(PastilleState.Paused, label, label + " refused — paused by you", null, null);
        }
        return status switch
        {
            ResponseEnvelope.Ok => new CardContent(PastilleState.Done, label, label + " succeeded", null, null),
            ResponseEnvelope.Accepted => new CardContent(PastilleState.Running, label, label + " still running", null, null),
            ResponseEnvelope.Running => new CardContent(PastilleState.Running, label, label + " running", null, null),
            ResponseEnvelope.Refused => new CardContent(PastilleState.Failed, label, label + " refused", code, message),
            _ => new CardContent(PastilleState.Failed, label, label + " failed", code, message),
        };
    }

    /// <summary>The user paused the server from the ribbon.</summary>
    public static CardContent Paused(PauseInfo pause)
    {
        string at = pause.SinceUtc?.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) ?? "now";
        return new CardContent(PastilleState.Paused, "server", "paused by you at " + at + " · every call is refused until you resume", null, null);
    }

    /// <summary>A session started (choice null: the question waits in Revit) or the user answered it.</summary>
    public static CardContent Session(SessionChoice? choice) => choice is null
        ? new CardContent(PastilleState.Paused, "session", "answer the session window · changes wait until you do", null, null)
        : new CardContent(PastilleState.Done, "session", "session: " + choice.Text(), null, null);

    /// <summary>The assistant proposed a baked command: it waits in the inbox (ADR-034).</summary>
    public static CardContent Inbox(string name) =>
        new(PastilleState.Running, "inbox", name + " proposed · review it in the RIFT inbox before it can run", null, null);

    /// <summary>The user resumed the server from the ribbon.</summary>
    public static CardContent Resumed() =>
        new(PastilleState.Done, "server", "resumed · the next call goes through", null, null);

    private static string Lower(string s) => s.ToLower(CultureInfo.InvariantCulture);
}
