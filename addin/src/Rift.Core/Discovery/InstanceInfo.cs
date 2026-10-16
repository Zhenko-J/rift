// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Rift.Core.Discovery;

/// <summary>Shareable metadata of one Revit instance, written to <c>instances\revit-&lt;pid&gt;.json</c> (ADR-003).</summary>
public sealed record InstanceInfo(
    int Pid,
    [property: JsonPropertyName("process_start_utc")] DateTimeOffset ProcessStartUtc,
    string Transport,
    string Pipe,
    [property: JsonPropertyName("revit_version")] string RevitVersion,
    string Build,
    string Engine,
    [property: JsonPropertyName("addin_version")] string AddinVersion,
    int Generation,
    string Protocol,
    IReadOnlyList<string> Capabilities,
    [property: JsonPropertyName("written_utc")] DateTimeOffset WrittenUtc);
