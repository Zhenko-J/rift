// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

using Rift.Core.Discovery;
using Rift.Core.Logging;

namespace Rift.Tests;

/// <summary>Plan M1, test 5: atomic discovery writes with an owner-only ACL, purge of dead pids only.</summary>
public sealed class DiscoveryFileTests : IDisposable
{
    private static readonly string[] s_capabilities = { "context", "lease", "job" };
    private static readonly string[] s_survivors = { "notes.txt", "revit-100.json", "revit-100.key", "revit-abc.json" };
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rift-tests", Guid.NewGuid().ToString("N"));

    private static InstanceInfo Info(int pid, int generation, string pipe = "rift-revit-x") =>
        new(pid, new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero), "pipe", pipe, "2026", "26.3", "ironpython-3.4", "0.1.0",
            generation, "2.0", s_capabilities, DateTimeOffset.UtcNow);

    [Fact]
    public void Write_produces_the_adr003_fields_in_snake_case()
    {
        var discovery = new DiscoveryFile(_dir, FileLog.Null);
        string path = discovery.Write(Info(1234, 3));
        Assert.Equal(Path.Combine(_dir, "revit-1234.json"), path);
        JsonNode node = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(1234, node["pid"]!.GetValue<int>());
        Assert.Equal(3, node["generation"]!.GetValue<int>());
        Assert.Equal("rift-revit-x", node["pipe"]!.GetValue<string>());
        Assert.NotNull(node["process_start_utc"]);
        Assert.NotNull(node["revit_version"]);
        Assert.NotNull(node["addin_version"]);
        Assert.NotNull(node["written_utc"]);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task Concurrent_readers_never_observe_a_partial_file()
    {
        var discovery = new DiscoveryFile(_dir, FileLog.Null);
        string path = discovery.Write(Info(77, 1, new string('p', 2000)));
        using var stop = new CancellationTokenSource();
        using var firstRead = new ManualResetEventSlim(false);
        int parsed = 0;
        Task reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                string text;
                try
                {
                    text = File.ReadAllText(path);
                }
                catch (IOException)
                {
                    continue;   // replaced at this instant: a reader retries, it never sees half a file
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                JsonNode node = JsonNode.Parse(text)!;   // throws on a torn write
                Assert.Equal(77, node["pid"]!.GetValue<int>());
                Interlocked.Increment(ref parsed);
                firstRead.Set();
                Thread.Sleep(1);   // a client reads now and then, not in a tight loop
            }
        });
        // On a fast machine the writer could finish before the reader's first read: start writing once it has read.
        Assert.True(firstRead.Wait(TimeSpan.FromSeconds(10)), "the reader never read the discovery file");
        for (int g = 2; g < 200; g++)
        {
            discovery.Write(Info(77, g, new string('p', 2000 + g)));
        }
        stop.Cancel();
        await reader;
        Assert.True(parsed > 0);
        Assert.Equal(199, JsonNode.Parse(File.ReadAllText(path))!["generation"]!.GetValue<int>());
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Purge_removes_dead_pids_only_with_their_key_and_temp_files()
    {
        var discovery = new DiscoveryFile(_dir, FileLog.Null);
        discovery.Write(Info(100, 1));
        discovery.Write(Info(200, 1));
        File.WriteAllBytes(discovery.KeyPathFor(100), new byte[32]);
        File.WriteAllBytes(discovery.KeyPathFor(200), new byte[32]);
        File.WriteAllBytes(discovery.KeyPathFor(300), new byte[32]);              // orphan key of a dead pid
        File.WriteAllText(Path.Combine(_dir, "revit-200.json.123.tmp"), "{");     // leftover of a crash mid-write
        File.WriteAllText(Path.Combine(_dir, "revit-abc.json"), "{}");            // not ours: left alone
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "x");

        int removed = discovery.PurgeDeadPids(pid => pid == 100);

        Assert.Equal(2, removed);
        string[] left = Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;
        Assert.Equal(s_survivors, left);
        Assert.Equal(0, new DiscoveryFile(Path.Combine(_dir, "missing"), FileLog.Null).PurgeDeadPids(_ => false));
    }

    [Fact]
    public void Delete_removes_the_record()
    {
        var discovery = new DiscoveryFile(_dir, FileLog.Null);
        discovery.Write(Info(5, 1));
        discovery.Delete(5);
        Assert.False(File.Exists(discovery.PathFor(5)));
        discovery.Delete(5);
    }

    [Fact]
    public void Record_is_created_with_an_owner_only_acl()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var discovery = new DiscoveryFile(_dir, FileLog.Null);
        string path = discovery.Write(Info(9, 1));
        FileSecurity acl = new FileInfo(path).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected, "inheritance must be disabled");
        SecurityIdentifier me = WindowsIdentity.GetCurrent().User!;
        AuthorizationRuleCollection rules = acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        Assert.NotEmpty(rules);
        Assert.All(rules.Cast<FileSystemAccessRule>(), r =>
        {
            Assert.Equal(me, r.IdentityReference);
            Assert.Equal(AccessControlType.Allow, r.AccessControlType);
        });
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
