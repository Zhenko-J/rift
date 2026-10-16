// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.AccessControl;

using Rift.Core.IO;
using Rift.Core.Json;
using Rift.Core.Logging;
using Rift.Core.Security;

namespace Rift.Core.Discovery;

/// <summary>Atomic writer of discovery files (temp file + move), with purge of files whose pid is dead.</summary>
public sealed class DiscoveryFile
{
    private readonly string _dir;
    private readonly FileLog _log;

    public DiscoveryFile(string instancesDir, FileLog log)
    {
        _dir = instancesDir;
        _log = log;
    }

    public static string FileName(int pid) => "revit-" + pid.ToString(CultureInfo.InvariantCulture) + ".json";

    public static string KeyFileName(int pid) => "revit-" + pid.ToString(CultureInfo.InvariantCulture) + ".key";

    public string PathFor(int pid) => Path.Combine(_dir, FileName(pid));

    public string KeyPathFor(int pid) => Path.Combine(_dir, KeyFileName(pid));

    /// <summary>Writes the record atomically; returns the final path.</summary>
    public string Write(InstanceInfo info)
    {
        Directory.CreateDirectory(_dir);
        string final = PathFor(info.Pid);
        string tmp = final + "." + Environment.TickCount64.ToString(CultureInfo.InvariantCulture) + ".tmp";
        byte[] bytes = JsonCodec.Serialize(info);
        FileSecurity? acl = OwnerOnlyAcl.TryBuild(_log);
        if (acl is not null && OperatingSystem.IsWindows())
        {
            using (FileStream fs = FileSystemAclExtensions.Create(new FileInfo(tmp), FileMode.Create, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, acl))
            {
                fs.Write(bytes, 0, bytes.Length);
            }
        }
        else
        {
            File.WriteAllBytes(tmp, bytes);
        }
        AtomicFile.MoveReplacing(tmp, final);
        return final;
    }

    public void Delete(int pid)
    {
        try
        {
            File.Delete(PathFor(pid));
        }
        catch (IOException ex)
        {
            _log.Write("discovery delete failed: " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Write("discovery delete failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Removes every <c>revit-&lt;pid&gt;.*</c> file (record, key, leftover temp file) whose pid is not alive. Files that do not
    /// follow the naming scheme are left alone. Returns the number of dead pids cleaned.
    /// </summary>
    public int PurgeDeadPids(Func<int, bool> isAlive)
    {
        if (!Directory.Exists(_dir))
        {
            return 0;
        }
        var verdicts = new Dictionary<int, bool>();
        var cleaned = new HashSet<int>();
        foreach (string file in Directory.EnumerateFiles(_dir, "revit-*"))
        {
            string name = Path.GetFileName(file);
            int dot = name.IndexOf('.', StringComparison.Ordinal);
            if (dot <= "revit-".Length
                || !int.TryParse(name.AsSpan("revit-".Length, dot - "revit-".Length), NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
            {
                continue;
            }
            if (!verdicts.TryGetValue(pid, out bool alive))
            {
                alive = isAlive(pid);
                verdicts[pid] = alive;
            }
            if (alive)
            {
                continue;
            }
            try
            {
                File.Delete(file);
                cleaned.Add(pid);
            }
            catch (IOException ex)
            {
                _log.Write("purge failed for " + name + ": " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                _log.Write("purge failed for " + name + ": " + ex.Message);
            }
        }
        return cleaned.Count;
    }
}
