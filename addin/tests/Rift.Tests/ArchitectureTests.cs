// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Linq;
using System.Reflection;

using Xunit;

namespace Rift.Tests;

/// <summary>ADR-021: Core and Scripting never reference the Revit API.</summary>
public sealed class ArchitectureTests
{
    [Theory]
    [InlineData(typeof(Rift.Core.Protocol.ProtocolVersion))]
    [InlineData(typeof(Rift.Scripting.IScriptEngine))]
    [InlineData(typeof(Rift.Scripting.Runs.RunQueryOp))]
    public void Revit_agnostic_assemblies_do_not_reference_the_revit_api(Type anchor)
    {
        AssemblyName[] refs = anchor.Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(refs, r => r.Name!.StartsWith("RevitAPI", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(refs, r => r.Name!.Contains("pyRevit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Core_does_not_reference_networking_or_process_apis()
    {
        AssemblyName[] refs = typeof(Rift.Core.Protocol.ProtocolVersion).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(refs, r => r.Name!.StartsWith("System.Net", StringComparison.Ordinal));
        Assert.DoesNotContain(refs, r => r.Name == "System.Diagnostics.Process");
        Assert.All(refs, r => Assert.NotEqual("System.Net.Sockets", r.Name));
    }

    [Fact]
    public void Core_does_not_reference_the_script_engine()
    {
        AssemblyName[] refs = typeof(Rift.Core.Protocol.ProtocolVersion).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(refs, r => r.Name == "Rift.Scripting" || r.Name!.StartsWith("IronPython", StringComparison.Ordinal));
    }
}
