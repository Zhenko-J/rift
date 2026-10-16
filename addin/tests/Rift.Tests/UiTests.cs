// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Media;

using Xunit;

using Rift.Core.Control;
using Rift.Core.Logging;
using Rift.Core.Protocol;
using Rift.Ui;

namespace Rift.Tests;

/// <summary>UI work stream, tranche 1 (plan tests 10, 15 in part, 20 in part): theme values, the activity card, the boundaries of Rift.Ui.</summary>
public sealed class UiTests
{
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException("on STA: " + failure);
        }
    }

    // ── 10 ─────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Ground", "#CFCDC7")]
    [InlineData("Card", "#EAE9E6")]
    [InlineData("Inset", "#E0DFDB")]
    [InlineData("Ink", "#191918")]
    [InlineData("SubInk", "#3C3C39")]
    [InlineData("Quiet", "#64635E")]
    [InlineData("Hairline", "#C6C5BF")]
    [InlineData("Stroke", "#1F1F1E")]
    [InlineData("Accent", "#D93611")]
    [InlineData("OnAccent", "#FFFFFF")]
    [InlineData("Success", "#3E7A5E")]
    [InlineData("Warning", "#B98A22")]
    [InlineData("Failure", "#B2452E")]
    public void Every_palette_colour_is_the_value_of_gate_1(string name, string hex)
    {
        var color = (Color)typeof(Theme.Palette).GetField(name, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        Assert.Equal(hex, "#" + color.R.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)
            + color.G.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)
            + color.B.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(255, color.A);
        Assert.Equal(13, typeof(Theme.Palette).GetFields(BindingFlags.Public | BindingFlags.Static).Length);
    }

    // ── 15 (in part) ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_card_never_activates_never_tops_and_shows_one_accent_for_a_running_call()
    {
        OnSta(() =>
        {
            using var card = new ActivityCard(IntPtr.Zero, FileLog.Null);
            int style = card.EnsureHandleAndReadStyle();
            Assert.Equal(ActivityCard.WsExNoActivate, style & ActivityCard.WsExNoActivate);
            Assert.Equal(ActivityCard.WsExToolWindow, style & ActivityCard.WsExToolWindow);
            Assert.False(card.Window.ShowActivated);
            Assert.False(card.Window.Focusable);
            Assert.False(card.Window.Topmost);

            card.Fill(CardContent.Result("run_query", ResponseEnvelope.Accepted, null, null));
            Assert.Equal(Theme.Palette.Accent, ((SolidColorBrush)card.Pastille.Fill).Color);

            card.Fill(CardContent.Paused(new PauseInfo(ServerState.Paused, DateTimeOffset.UtcNow, "you")));
            Assert.Equal(Colors.Transparent, ((SolidColorBrush)card.Pastille.Fill).Color);
            Assert.Equal(Theme.Palette.Stroke, ((SolidColorBrush)card.Pastille.Stroke).Color);

            card.Fill(CardContent.Result("get_context", ResponseEnvelope.Refused, ErrorCodes.LeaseHeld, "held by another client"));
            Assert.Equal(Theme.Palette.Failure, ((SolidColorBrush)card.Pastille.Fill).Color);
            Assert.Contains("LEASE_HELD", card.Shown, StringComparison.Ordinal);
            Assert.Contains("held by another client", card.Shown, StringComparison.Ordinal);

            card.Enabled = false;
            card.Show(CardContent.Resumed());
            Assert.False(card.Window.IsVisible);
        });
    }

    [Fact]
    public void Card_asides_are_lower_case_without_a_final_period_and_a_pause_refusal_is_hollow()
    {
        CardContent[] all =
        {
            CardContent.Connected("Rift-MCP 0.0.1"),
            CardContent.Result("get_context", ResponseEnvelope.Ok, null, null),
            CardContent.Result("run_query", ResponseEnvelope.Accepted, null, null),
            CardContent.Result("run_query", ResponseEnvelope.Running, null, null),
            CardContent.Result("run_modify", ResponseEnvelope.ErrorStatus, ErrorCodes.ScriptError, "ValueError at line 3"),
            CardContent.Paused(new PauseInfo(ServerState.Paused, DateTimeOffset.UtcNow, "you")),
            CardContent.Resumed(),
        };
        foreach (CardContent c in all)
        {
            Assert.Equal(c.Aside, c.Aside.ToLowerInvariant());
            Assert.False(c.Aside.EndsWith('.'), c.Aside);
        }
        CardContent paused = CardContent.Result("get_context", ResponseEnvelope.Refused, ErrorCodes.Paused, PauseGate.Message);
        Assert.Equal(PastilleState.Paused, paused.State);
        Assert.Null(paused.Code);
    }

    // ── 20 (in part) ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rift_ui_never_references_the_revit_api_and_no_window_is_topmost()
    {
        Assert.DoesNotContain(typeof(Theme).Assembly.GetReferencedAssemblies(), a => a.Name is not null && a.Name.StartsWith("RevitAPI", StringComparison.Ordinal));
        string src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
        string csproj = File.ReadAllText(Path.Combine(src, "Rift.Ui", "Rift.Ui.csproj"));
        Assert.DoesNotContain("Revit.Api", csproj, StringComparison.Ordinal);
        foreach (string file in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            Assert.DoesNotMatch(@"Topmost\s*=\s*true", File.ReadAllText(file));
        }
    }
}
