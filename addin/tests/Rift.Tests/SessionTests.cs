// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;

using Xunit;

using Rift.Core.Config;
using Rift.Core.Control;
using Rift.Core.Protocol;
using Rift.Scripting.Runs;
using Rift.Ui;

namespace Rift.Tests;

/// <summary>ADR-033: the session question asked in Revit, and what it does to the write policy.</summary>
public sealed class SessionTests
{
    [Fact]
    public void A_new_client_clears_the_answer_and_the_same_client_keeps_it()
    {
        var gate = new SessionGate();
        var events = new List<(string, SessionChoice?)>();
        gate.Changed += (c, choice) => events.Add((c, choice));

        Assert.True(gate.Begin("a"));
        Assert.Null(gate.Choice);
        Assert.True(gate.Choose("a", WorkScope.ActiveProject, Supervision.FullAuto, @"C:\w\PLAN.rvt", "PLAN"));
        Assert.Equal("active project · full auto", gate.Choice!.Text());
        Assert.False(gate.Begin("a"));
        Assert.NotNull(gate.Choice);

        Assert.True(gate.Begin("b"));
        Assert.Null(gate.Choice);
        Assert.False(gate.Choose("a", WorkScope.Sandbox, Supervision.Supervised, null, null));
        Assert.Null(gate.Choice);
        Assert.Equal(new[] { "a", "a", "b" }, events.ConvertAll(e => e.Item1));
    }

    [Fact]
    public void The_active_project_is_matched_by_path_or_by_title_when_unsaved()
    {
        var saved = new SessionChoice("a", WorkScope.ActiveProject, Supervision.FullAuto, @"C:\w\PLAN.rvt", "PLAN", DateTimeOffset.UtcNow);
        Assert.True(saved.IsProject(@"c:\W\plan.RVT", "PLAN"));
        Assert.False(saved.IsProject(@"C:\w\OTHER.rvt", "PLAN"));
        var unsaved = saved with { ProjectPath = null, ProjectTitle = "Project1" };
        Assert.True(unsaved.IsProject(null, "Project1"));
        Assert.False(unsaved.IsProject(@"C:\w\Project1.rvt", "Project1"));
        Assert.False((saved with { Scope = WorkScope.Sandbox }).IsProject(@"C:\w\PLAN.rvt", "PLAN"));
        // A sandbox choice never records a project.
        var gate = new SessionGate();
        gate.Begin("a");
        gate.Choose("a", WorkScope.Sandbox, Supervision.FullAuto, @"C:\w\PLAN.rvt", "PLAN");
        Assert.Null(gate.Choice!.ProjectPath);
    }

    [Theory]
    [InlineData("sandbox", false, false, false, "auto", null, null)]
    [InlineData("sandbox", false, false, true, "ask", null, "supervised_session")]
    [InlineData("unmarked", false, false, false, null, ErrorCodes.SandboxRequired, "unmarked_document")]
    [InlineData("unmarked", true, true, false, "auto", null, null)]
    [InlineData("reference", true, true, false, "auto", null, null)]
    [InlineData("unsaved", true, true, true, "ask", null, "supervised_session")]
    [InlineData("sandbox", false, false, false, null, ErrorCodes.SessionPending, "session_pending")]
    public void The_session_decides_with_the_document_class(string cls, bool chosen, bool inProject, bool supervised,
        string? applied, string? refusal, string? reason)
    {
        // Row 7 is the unanswered session: chosen is false only there.
        bool answered = chosen || refusal != ErrorCodes.SessionPending;
        PolicyDecision d = WritePolicy.Decide(cls, WritePolicy.Auto, allowReference: false, new SessionPolicy(answered, inProject, supervised, "x"));
        Assert.Equal(applied, d.Applied);
        Assert.Equal(refusal, d.RefusalCode);
        Assert.Equal(reason, d.Reason);
    }

    [Fact]
    public void Without_a_session_the_policy_is_the_one_of_adr_009()
    {
        Assert.Equal(ErrorCodes.SandboxRequired, WritePolicy.Decide("unmarked", WritePolicy.Auto, false).RefusalCode);
        Assert.Equal(WritePolicy.Auto, WritePolicy.Decide("sandbox", WritePolicy.Auto, false, null).Applied);
        Assert.Contains("session window", WritePolicy.RefusalMessage(new PolicyDecision(null, ErrorCodes.SessionPending, "session_pending")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_session_question_is_on_by_default_and_can_be_turned_off()
    {
        Assert.True(RiftConfig.Default.SessionPrompt);
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rift-session-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            System.IO.File.WriteAllText(path, "{\"session_prompt\": false}");
            Assert.False(RiftConfig.Load(path).SessionPrompt);
            RiftConfig.Default.Save(path);
            Assert.Contains("\"session_prompt\": true", System.IO.File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void The_card_says_when_the_answer_is_waited_for()
    {
        Assert.Equal(PastilleState.Paused, CardContent.Session(null).State);
        var choice = new SessionChoice("a", WorkScope.Sandbox, Supervision.Supervised, null, null, DateTimeOffset.UtcNow);
        Assert.Equal("session: sandbox copies · supervised", CardContent.Session(choice).Aside);
    }
}
