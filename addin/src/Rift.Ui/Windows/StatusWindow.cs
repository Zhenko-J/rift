// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

using Rift.Ui.Kit;

namespace Rift.Ui.Windows;

/// <summary>
/// The Status window (UI gate 3, tranche 3): numeral CALLS SERVED, tiles for the server, the client, the pipe, the
/// engine, the last refusal and the log, and three verbs — pause or resume, the activity card switch, copy the log path.
/// </summary>
public sealed class StatusWindow : RiftWindow
{
    private readonly IUiHost _host;
    private string? _copied;

    public StatusWindow(IUiHost host, IntPtr owner)
        : base("status", owner, 640, 620, TimeSpan.FromSeconds(1))
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    protected override object Fingerprint() => (_host.Status(), _copied);

    protected override UIElement Build()
    {
        StatusSnapshot s = _host.Status();
        string server = s.Paused
            ? "paused by you at " + Local(s.PausedSinceUtc) + " · every call gets PAUSED until you resume"
            : "on · the assistant can reach this model";
        StackPanel body = Heading("calls served", s.Served.ToString(CultureInfo.InvariantCulture), server);

        var tiles = new WrapPanel();
        tiles.Children.Add(Tile(Z.InsetTile("client", s.Client ?? "none",
            s.Client is null ? "no assistant holds the lease" : "since " + Local(s.ClientSinceUtc) + " · lease until " + Local(s.LeaseUntilUtc))));
        tiles.Children.Add(Tile(Z.InsetTile("server", s.Paused ? "paused" : "on", "add-in " + s.Version)));
        tiles.Children.Add(Tile(Z.InsetTile("pipe", s.PipeName ?? "-", "secret generation " + s.SecretGeneration.ToString(CultureInfo.InvariantCulture))));
        tiles.Children.Add(Tile(Z.InsetTile("script engine", s.Engine)));
        tiles.Children.Add(Tile(Z.InsetTile("last refusal", s.LastRefusal ?? "none")));
        tiles.Children.Add(Tile(Z.InsetTile("other agents", s.Coexistence ?? "none detected")));
        tiles.Children.Add(Tile(Z.InsetTile("session", s.Session ?? "no assistant session",
            s.Session == "not answered" ? "changes are refused until you answer" : null)));
        body.Children.Add(tiles);

        var log = Z.InsetTile("log", s.LogPath ?? "-", _copied);
        log.HorizontalAlignment = HorizontalAlignment.Stretch;
        body.Children.Add(log);

        var verbs = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        verbs.Children.Add(Z.GhostButton(s.Paused ? "resume" : "pause", () =>
        {
            _host.TogglePause();
            Refresh(true);
        }));
        verbs.Children.Add(Z.GhostButton(s.CardOn ? "activity card off" : "activity card on", () =>
        {
            _host.SetActivityCard(!s.CardOn);
            Refresh(true);
        }));
        if (s.Session is not null)
        {
            verbs.Children.Add(Z.GhostButton("change session", _host.OpenSession));
        }
        if (s.LogPath is not null)
        {
            string path = s.LogPath;
            verbs.Children.Add(Z.GhostButton("copy log path", () =>
            {
                _copied = Copy(path) ? "path copied" : "clipboard busy, copy it from here";
                Refresh(true);
            }));
        }
        body.Children.Add(verbs);
        return body;
    }

    private static Border Tile(Border tile)
    {
        tile.Width = 268;
        return tile;
    }

    private static string Local(DateTimeOffset? utc) =>
        utc?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "?";
}
