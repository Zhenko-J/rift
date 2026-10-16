// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Globalization;
using System.Text;

using Rift.Core;
using Rift.Core.Lease;

namespace Rift.Revit.Ui;

/// <summary>
/// Text of the Status ribbon button (ADR-004/023): add-in version, pipe, lease holder and since when, secret generation,
/// last refusal, activity card state and log path. Pure formatting: no Revit API, no model data.
/// </summary>
internal static class StatusReport
{
    public static string Format(string version, string? pipeName, int served, LeaseHolder? holder, DateTimeOffset? leaseUntil,
        int generation, string? lastRefusal, string cardState, string? logPath, string? engine = null, string? server = null)
    {
        var sb = new StringBuilder();
        sb.Append(Brand.Product).Append(" add-in ").Append(version);
        sb.Append("\npipe: ").Append(pipeName is null ? "-" : "\\\\.\\pipe\\" + pipeName + " (served " + served.ToString(CultureInfo.InvariantCulture) + ")");
        sb.Append("\nserver: ").Append(server ?? "on");
        if (holder is null)
        {
            sb.Append("\nclient: none");
        }
        else
        {
            sb.Append("\nclient: ").Append(holder.Name).Append(' ').Append(holder.Version)
              .Append(" since ").Append(Local(holder.Since));
            if (leaseUntil is not null)
            {
                sb.Append(" (lease until ").Append(Local(leaseUntil.Value)).Append(')');
            }
        }
        sb.Append("\nsecret generation: ").Append(generation.ToString(CultureInfo.InvariantCulture));
        sb.Append("\nlast refusal: ").Append(lastRefusal ?? "none");
        sb.Append("\nscript engine: ").Append(engine ?? "-");
        sb.Append("\nactivity card: ").Append(cardState);
        sb.Append("\nlog: ").Append(logPath ?? "-");
        return sb.ToString();
    }

    private static string Local(DateTimeOffset t) => t.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}
