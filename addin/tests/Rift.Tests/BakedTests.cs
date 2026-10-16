// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

using Xunit;

using Rift.Core.Baked;
using Rift.Core.Protocol;

namespace Rift.Tests;

/// <summary>ADR-034: baked commands are proposed by the assistant, accepted by the user, run only with their recorded hash.</summary>
public sealed class BakedTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rift-baked-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_proposal_waits_until_the_user_accepts_it()
    {
        var store = new BakedStore(_dir);
        BakedProposal? seen = null;
        store.Proposed += p => seen = p;
        BakedProposal p = store.Propose("count_walls", "how many walls", "query", "result = 1");
        Assert.Same(p, seen);
        Assert.Single(store.Proposals());
        OperationException notYet = Assert.Throws<OperationException>(() => store.Find("count_walls"));
        Assert.Equal(ErrorCodes.BadRequest, notYet.Code);

        BakedCommand c = store.Accept(p.Id);
        Assert.Equal(BakedStore.Hash("result = 1"), c.Sha256);
        Assert.Empty(store.Proposals());
        Assert.Equal("count_walls", store.Find("count_walls").Name);
    }

    [Fact]
    public void A_command_whose_script_changed_on_disk_never_runs()
    {
        var store = new BakedStore(_dir);
        store.Accept(store.Propose("count_walls", "", "query", "result = 1").Id);
        string file = Path.Combine(_dir, "accepted", "count_walls.json");
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json["script"] = "result = 2";
        File.WriteAllText(file, json.ToJsonString());
        OperationException ex = Assert.Throws<OperationException>(() => store.Find("count_walls"));
        Assert.Equal(ErrorCodes.HashMismatch, ex.Code);
    }

    [Theory]
    [InlineData("Count", "query", "x = 1")]
    [InlineData("ab", "query", "x = 1")]
    [InlineData("../evil", "query", "x = 1")]
    [InlineData("count_walls", "delete", "x = 1")]
    [InlineData("count_walls", "query", " ")]
    public void Bad_proposals_are_refused(string name, string mode, string script)
    {
        var store = new BakedStore(_dir);
        Assert.Equal(ErrorCodes.BadRequest, Assert.Throws<OperationException>(() => store.Propose(name, "", mode, script)).Code);
        Assert.Empty(store.Proposals());
    }

    [Fact]
    public void Rejecting_deletes_the_proposal_and_ignores_odd_ids()
    {
        var store = new BakedStore(_dir);
        BakedProposal p = store.Propose("count_walls", "", "query", "result = 1");
        store.Reject("../../x");
        Assert.Single(store.Proposals());
        store.Reject(p.Id);
        Assert.Empty(store.Proposals());
        Assert.Empty(store.Commands());
        Assert.False(Directory.EnumerateFiles(_dir, "*.tmp", SearchOption.AllDirectories).Any());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
