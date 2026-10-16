// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Rift.Core.Transport;

/// <summary>Pipe DACL of ADR-001: current user full control, network logons denied (proven by P-D1).</summary>
[SupportedOSPlatform("windows")]
public static class PipeAcl
{
    public static PipeSecurity Build()
    {
        var security = new PipeSecurity();
        SecurityIdentifier me = WindowsIdentity.GetCurrent().User!;
        security.SetOwner(me);
        security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }
}
