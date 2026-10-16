// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;

using Xunit;

using Rift.Core.Config;
using Rift.Core.Policy;

namespace Rift.Tests;

/// <summary>ADR-019: other assistant add-ins and pyRevit detected by assembly name, with the default patterns of config.json.</summary>
public sealed class CoexistenceTests
{
    [Fact]
    public void Known_assistant_add_ins_are_reported_and_ours_are_not()
    {
        string?[] loaded =
        {
            "RevitAPI", "RevitAPIUI", "Rift.Core", "Rift.Revit", "RvtMcp.Plugin", "pyRevitLoader",
            "pyRevit_2026_c75d2bfd839d10cf_mcp-server-for-revit-python", "Newtonsoft.Json", null, "",
        };
        CoexistenceReport report = Coexistence.Detect(loaded, RiftConfig.Default.AgentPatterns);
        Assert.True(report.PyRevitLoaded);
        Assert.True(report.OtherAgentActive);
        Assert.Equal(new[] { "pyRevit_2026_c75d2bfd839d10cf_mcp-server-for-revit-python", "RvtMcp.Plugin" }, report.OtherAgents);
    }

    [Fact]
    public void Revit_alone_reports_nothing()
    {
        CoexistenceReport report = Coexistence.Detect(new[] { "RevitAPI", "AdWindows", "Rift.Ui", "Autodesk.Agent.Telemetry" }, RiftConfig.Default.AgentPatterns);
        Assert.False(report.PyRevitLoaded);
        Assert.False(report.OtherAgentActive);
    }

    [Theory]
    [InlineData("RvtMcp.Plugin", "RvtMcp*", true)]
    [InlineData("rvtmcp.plugin", "RvtMcp*", true)]
    [InlineData("MyRvtMcp", "RvtMcp*", false)]
    [InlineData("revit_mcp_plugin", "revit_mcp*", true)]
    [InlineData("a.b", "a?b", true)]
    [InlineData("a+b", "a.b", false)]
    public void Patterns_are_globs(string name, string pattern, bool expected)
    {
        Assert.Equal(expected, Coexistence.Matches(name, pattern));
    }

    [Fact]
    public void Coexistence_is_off_by_default_and_read_from_config()
    {
        Assert.False(RiftConfig.Default.CoexistWithOtherAgents);
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rift-config-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            System.IO.File.WriteAllText(path, "{\"coexist_with_other_agents\": true}");
            RiftConfig loaded = RiftConfig.Load(path);
            Assert.True(loaded.CoexistWithOtherAgents);
            Assert.Equal(RiftConfig.Default.AgentPatterns, loaded.AgentPatterns);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}
