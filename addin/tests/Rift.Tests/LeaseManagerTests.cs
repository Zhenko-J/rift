// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

using Xunit;

using Rift.Core.Jobs;
using Rift.Core.Lease;
using Rift.Core.Logging;
using Rift.Core.Protocol;
using Rift.Core.Protocol.Ops;

namespace Rift.Tests;

/// <summary>Plan M1, test 7 (lease) and the lease admission of the router (ADR-004).</summary>
public sealed class LeaseManagerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private DateTimeOffset _now = T0;

    private LeaseManager NewLease() => new(TimeSpan.FromSeconds(60), () => _now);

    private static LeaseHolder Candidate(string id) => new(id, "client-" + id, "1.0", DateTimeOffset.MinValue);

    [Fact]
    public void Second_candidate_is_held_off_with_the_first_holder()
    {
        LeaseManager lease = NewLease();
        var granted = new List<LeaseHolder>();
        lease.Granted += granted.Add;

        LeaseResult a = lease.Acquire(Candidate("a"));
        Assert.True(a.Granted);
        Assert.Equal(T0.AddSeconds(60), a.Until);
        Assert.Equal(T0, a.Holder!.Since);

        LeaseResult b = lease.Acquire(Candidate("b"));
        Assert.False(b.Granted);
        Assert.Equal("a", b.Holder!.ClientId);
        Assert.Equal("client-a", b.Holder.Name);

        // Re-acquiring by the holder renews without a new Granted event and keeps "since".
        _now = T0.AddSeconds(30);
        LeaseResult again = lease.Acquire(Candidate("a"));
        Assert.True(again.Granted);
        Assert.Equal(T0.AddSeconds(90), again.Until);
        Assert.Equal(T0, again.Holder!.Since);
        Assert.Single(granted);
    }

    [Fact]
    public void Renewal_extends_and_expiry_lets_a_new_candidate_in()
    {
        LeaseManager lease = NewLease();
        var granted = new List<string>();
        lease.Granted += h => granted.Add(h.ClientId);
        lease.Acquire(Candidate("a"));

        _now = T0.AddSeconds(50);
        Assert.True(lease.Renew("a"));
        Assert.False(lease.Renew("b"));
        _now = T0.AddSeconds(100);           // past the first 60 s, within the renewed lease
        Assert.Equal("a", lease.Holder!.ClientId);
        Assert.False(lease.Acquire(Candidate("b")).Granted);

        _now = T0.AddSeconds(111);           // renewed lease ended at 110 s
        Assert.Null(lease.Holder);
        Assert.Null(lease.Until);
        Assert.False(lease.Renew("a"));
        LeaseResult b = lease.Acquire(Candidate("b"));
        Assert.True(b.Granted);
        Assert.Equal("b", lease.Holder!.ClientId);
        Assert.Equal(["a", "b"], granted);
    }

    [Fact]
    public void Admit_renews_the_holder_refuses_others_and_takes_a_free_lease()
    {
        LeaseManager lease = NewLease();
        var granted = new List<string>();
        lease.Granted += h => granted.Add(h.Name);

        // Free lease, no client id: served, nothing taken.
        Assert.True(lease.Admit(null, null, null).Granted);
        Assert.Null(lease.Holder);

        // Free lease with a client id: taken, announced once.
        Assert.True(lease.Admit("a", "rift-mcp", "0.1").Granted);
        Assert.Equal("rift-mcp", lease.Holder!.Name);

        _now = T0.AddSeconds(55);
        Assert.True(lease.Admit("a", null, null).Granted);
        _now = T0.AddSeconds(110);           // alive only because Admit renewed it
        Assert.Equal("a", lease.Holder!.ClientId);

        LeaseResult other = lease.Admit("b", "other", "9");
        Assert.False(other.Granted);
        Assert.Equal("a", other.Holder!.ClientId);
        Assert.False(lease.Admit(null, null, null).Granted);
        Assert.Equal(["rift-mcp"], granted);
    }

    [Fact]
    public void Revoke_frees_the_lease()
    {
        LeaseManager lease = NewLease();
        lease.Acquire(Candidate("a"));
        lease.Revoke();
        Assert.Null(lease.Holder);
        Assert.True(lease.Acquire(Candidate("b")).Granted);
    }

    private sealed class Ping : IOperationHandler
    {
        public int Runs;

        public string Op => "ping";

        public bool NeedsApiThread => false;

        public JsonNode? Execute(RequestEnvelope request, IProgress<(int Percent, string Message)> progress, CancellationToken cancellation)
        {
            Runs++;
            return new JsonObject();
        }
    }

    private sealed class NoRunner : IJobRunner
    {
        public void Enqueue(Job job) => throw new InvalidOperationException("no API thread in this test");

        public string? BusyReason() => null;
    }

    private static ResponseEnvelope Send(RequestRouter router, string op, string? sessionId, string args = "{}")
    {
        string client = sessionId is null ? "null" : "{\"session_id\":\"" + sessionId + "\",\"name\":\"n-" + sessionId + "\",\"version\":\"1\"}";
        string json = "{\"v\":\"2.0\",\"id\":\"x\",\"op\":\"" + op + "\",\"client\":" + client + ",\"args\":" + args + "}";
        return router.Handle(Encoding.UTF8.GetBytes(json), CancellationToken.None);
    }

    [Fact]
    public void Router_refuses_a_second_client_with_lease_held_and_executes_nothing()
    {
        LeaseManager lease = NewLease();
        var ping = new Ping();
        var router = new RequestRouter(new IOperationHandler[] { new LeaseOp(lease), ping }, new JobTable(TimeSpan.FromMinutes(1)), new NoRunner(),
            () => new TargetInfo(1, 1, null), FileLog.Null, lease: lease);
        var completed = new List<(string Label, string Status, string? Code)>();
        router.Completed += (label, status, code, _) => completed.Add((label, status, code));

        ResponseEnvelope first = Send(router, "lease", "a", "{\"client_id\":\"a\",\"client\":{\"name\":\"first\",\"version\":\"1\"}}");
        Assert.Equal("ok", first.Status);
        Assert.Equal("ok", Send(router, "ping", "a").Status);

        ResponseEnvelope secondLease = Send(router, "lease", "b", "{\"client_id\":\"b\",\"client\":{\"name\":\"second\",\"version\":\"1\"}}");
        Assert.Equal("refused", secondLease.Status);
        Assert.Equal(ErrorCodes.LeaseHeld, secondLease.Error!.Code);
        Assert.Equal("first", secondLease.Result!["holder"]!["name"]!.GetValue<string>());

        ResponseEnvelope secondPing = Send(router, "ping", "b");
        Assert.Equal("refused", secondPing.Status);
        Assert.Equal(ErrorCodes.LeaseHeld, secondPing.Error!.Code);
        Assert.Equal("a", secondPing.Result!["holder"]!["client_id"]!.GetValue<string>());
        Assert.Equal(ErrorCodes.LeaseHeld, Send(router, "ping", null).Error!.Code);
        Assert.Equal(1, ping.Runs);

        // The card sees every refusal with its code (ADR-023).
        Assert.Equal(("lease", "refused", ErrorCodes.LeaseHeld), completed[2]);
        Assert.Equal(("ping", "refused", ErrorCodes.LeaseHeld), completed[3]);
    }

    [Fact]
    public void Router_renews_the_lease_on_every_request_of_its_holder()
    {
        LeaseManager lease = NewLease();
        var router = new RequestRouter(new IOperationHandler[] { new LeaseOp(lease), new Ping() }, new JobTable(TimeSpan.FromMinutes(1)), new NoRunner(),
            () => new TargetInfo(1, 1, null), FileLog.Null, lease: lease);
        Send(router, "lease", "a", "{\"client_id\":\"a\"}");
        for (int i = 1; i <= 5; i++)
        {
            _now = T0.AddSeconds(50 * i);
            Assert.Equal("ok", Send(router, "ping", "a").Status);
        }
        Assert.Equal(T0.AddSeconds(310), lease.Until);
        Assert.Equal("refused", Send(router, "ping", "b").Status);

        _now = T0.AddSeconds(400);           // holder idle past its lease: another client may take it
        Assert.Equal("ok", Send(router, "ping", "b").Status);
        Assert.Equal("n-b", lease.Holder!.Name);
    }
}
