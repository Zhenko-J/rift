// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;

using Xunit;

using Rift.Core.Protocol;
using Rift.Scripting.Runs;

namespace Rift.Tests;

/// <summary>M2 plan test 1: the document policy of ADR-009 as a pure function.</summary>
public sealed class WritePolicyTests
{
    [Theory]
    [InlineData("sandbox", "auto", false, "auto", null)]
    [InlineData("sandbox", "ask", false, "ask", null)]
    [InlineData("sandbox", "auto", true, "auto", null)]
    [InlineData("unsaved", "auto", false, "ask", "unsaved_document")]
    [InlineData("unsaved", "ask", false, "ask", null)]
    [InlineData("reference", "auto", true, "ask", "reference_document")]
    [InlineData("reference", "ask", true, "ask", null)]
    public void A_run_gets_the_requested_policy_or_a_stricter_one(string documentClass, string requested, bool allowReference, string applied, string? reason)
    {
        PolicyDecision d = WritePolicy.Decide(documentClass, requested, allowReference);
        Assert.False(d.Refused);
        Assert.Equal(applied, d.Applied);
        Assert.Equal(reason, d.Reason);
    }

    [Theory]
    [InlineData("reference", "auto", false, "reference_document")]
    [InlineData("reference", "ask", false, "reference_document")]
    [InlineData("unmarked", "auto", false, "unmarked_document")]
    [InlineData("unmarked", "ask", true, "unmarked_document")]
    [InlineData(null, "auto", false, "unmarked_document")]
    public void Outside_a_sandbox_the_run_is_refused_with_sandbox_required(string? documentClass, string requested, bool allowReference, string reason)
    {
        PolicyDecision d = WritePolicy.Decide(documentClass, requested, allowReference);
        Assert.True(d.Refused);
        Assert.Equal(ErrorCodes.SandboxRequired, d.RefusalCode);
        Assert.Null(d.Applied);
        Assert.Equal(reason, d.Reason);
        Assert.Contains(reason == "reference_document" ? "allow_reference" : "SANDBOX", WritePolicy.RefusalMessage(d), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_policy_is_a_programming_error()
    {
        Assert.Throws<ArgumentException>(() => WritePolicy.Decide("sandbox", "always", false));
    }
}
