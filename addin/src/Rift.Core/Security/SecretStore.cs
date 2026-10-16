// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

using Rift.Core.IO;
using Rift.Core.Logging;

namespace Rift.Core.Security;

/// <summary>
/// The per-instance secret (ADR-002/003): 32 random bytes written to <c>revit-&lt;pid&gt;.key</c> with an explicit
/// ACL when possible (best effort, journaled). Rotates on demand and after repeated authentication failures.
/// </summary>
public sealed class SecretStore
{
    private readonly FileLog _log;
    private readonly object _gate = new();
    private byte[] _secret;

    public SecretStore(string keyPath, FileLog log)
    {
        KeyPath = keyPath;
        _log = log;
        _secret = HmacSigner.NewSecret();
        Generation = 1;
        Write();
    }

    public string KeyPath { get; }

    public int Generation { get; private set; }

    /// <summary>Raised after a rotation, once the new key file is written, with the new generation (the host rewrites the discovery file).</summary>
    public event Action<int>? Rotated;

    /// <summary>A copy of the current secret. Callers must not keep it beyond one request.</summary>
    public byte[] Current
    {
        get
        {
            lock (_gate)
            {
                return (byte[])_secret.Clone();
            }
        }
    }

    public void Rotate()
    {
        int generation;
        lock (_gate)
        {
            _secret = HmacSigner.NewSecret();
            generation = ++Generation;
        }
        Write();
        _log.Write("secret rotated, generation=" + generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Rotated?.Invoke(generation);
    }

    public void Delete()
    {
        try
        {
            File.Delete(KeyPath);
        }
        catch (IOException ex)
        {
            _log.Write("key delete failed: " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Write("key delete failed: " + ex.Message);
        }
    }

    private void Write()
    {
        byte[] secret;
        lock (_gate)
        {
            secret = (byte[])_secret.Clone();
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
            string tmp = KeyPath + ".tmp";
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            FileSecurity? acl = OwnerOnlyAcl.TryBuild(_log);
            if (acl is not null && OperatingSystem.IsWindows())
            {
                using FileStream fs = FileSystemAclExtensions.Create(new FileInfo(tmp), FileMode.Create, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, acl);
                fs.Write(secret, 0, secret.Length);
            }
            else
            {
                using FileStream fs = new(tmp, options);
                fs.Write(secret, 0, secret.Length);
            }
            AtomicFile.MoveReplacing(tmp, KeyPath);
        }
        catch (IOException ex)
        {
            _log.Write("key write failed: " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Write("key write failed: " + ex.Message);
        }
        finally
        {
            Array.Clear(secret);
        }
    }
}

/// <summary>Owner-only DACL (current user full control, inheritance disabled). Null when it cannot be built; the caller falls back to the inherited ACL.</summary>
public static class OwnerOnlyAcl
{
    public static FileSecurity? TryBuild(FileLog log)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        try
        {
            SecurityIdentifier? me = WindowsIdentity.GetCurrent().User;
            if (me is null)
            {
                return null;
            }
            var fs = new FileSecurity();
            fs.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            fs.SetOwner(me);
            fs.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Allow));
            return fs;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log.Write("owner-only ACL unavailable: " + ex.Message);
            return null;
        }
    }
}
