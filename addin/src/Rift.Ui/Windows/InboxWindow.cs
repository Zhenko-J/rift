// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

using Rift.Core.Baked;
using Rift.Ui.Kit;

namespace Rift.Ui.Windows;

/// <summary>
/// The inbox of baked commands (ADR-034): numeral = proposals waiting, each shown with its full script so that the user
/// reads what will be frozen; accept freezes it under its name with its SHA-256, reject deletes it. Below, the accepted
/// commands. Only this window accepts a proposal: the assistant cannot.
/// </summary>
public sealed class InboxWindow : RiftWindow
{
    private readonly IUiHost _host;
    private string? _note;

    public InboxWindow(IUiHost host, IntPtr owner)
        : base("inbox", owner, 820, 760, TimeSpan.FromSeconds(2))
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    protected override object Fingerprint() =>
        string.Join("|", _host.Proposals().Select(p => p.Id)) + "#" + string.Join("|", _host.BakedCommands().Select(c => c.Name + c.Sha256)) + _note;

    protected override UIElement Build()
    {
        var proposals = _host.Proposals();
        StackPanel body = Heading("inbox", proposals.Count.ToString(CultureInfo.InvariantCulture),
            proposals.Count == 0
                ? "no command waiting · the assistant proposes, only you accept"
                : "read each script before you accept it · an accepted command runs under the same guards as run_query and run_modify");
        if (_note is not null)
        {
            TextBlock n = Z.Aside(_note, Theme.Size.BodySmall);
            n.Margin = new Thickness(0, 0, 0, 16);
            body.Children.Add(n);
        }
        foreach (BakedProposal p in proposals)
        {
            var stack = new StackPanel();
            stack.Children.Add(Z.MicroLabel(p.Mode == "modify" ? "proposed · changes the model" : "proposed · reads only"));
            TextBlock name = Z.Value(p.Name, 20);
            name.Margin = new Thickness(0, 6, 0, 2);
            stack.Children.Add(name);
            if (p.Description.Length > 0)
            {
                stack.Children.Add(Z.Aside(p.Description, Theme.Size.BodySmall));
            }
            stack.Children.Add(Z.Aside("sha256 " + p.Sha256[..Math.Min(16, p.Sha256.Length)] + "… · " + Lines(p.Script) + " lines · "
                + p.ProposedUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture), Theme.Size.BodySmall));
            var script = new TextBox
            {
                Text = p.Script,
                IsReadOnly = true,
                FontFamily = Theme.Mono,
                FontSize = Theme.Size.BodySmall,
                Foreground = Theme.Brush(Theme.Palette.Ink),
                Background = Theme.Brush(Theme.Palette.Card),
                BorderThickness = new Thickness(0),
                MaxHeight = 280,
                Margin = new Thickness(0, 10, 0, 10),
                Padding = new Thickness(10),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                TextWrapping = TextWrapping.NoWrap,
            };
            stack.Children.Add(script);
            var verbs = new WrapPanel();
            string id = p.Id;
            string label = p.Name;
            verbs.Children.Add(Z.GhostButton("accept", () => Do(() => _host.AcceptBaked(id), label + " accepted")));
            verbs.Children.Add(Z.GhostButton("reject", () => Do(() => _host.RejectBaked(id), label + " rejected")));
            stack.Children.Add(verbs);
            body.Children.Add(new Border
            {
                Background = Theme.Brush(Theme.Palette.Inset),
                CornerRadius = new CornerRadius(Theme.Radius.Panel),
                Padding = new Thickness(Theme.Pad.Compact),
                Margin = new Thickness(0, 0, 0, Theme.Size.Gutter),
                Child = stack,
            });
        }

        var accepted = _host.BakedCommands();
        TextBlock head = Z.Aside(accepted.Count == 0 ? "no accepted command yet" : accepted.Count.ToString(CultureInfo.InvariantCulture) + " accepted", Theme.Size.BodySmall);
        head.Margin = new Thickness(0, 16, 0, 8);
        body.Children.Add(Z.MicroLabel("accepted"));
        body.Children.Add(head);
        foreach (BakedCommand c in accepted)
        {
            body.Children.Add(Z.Value(c.Name + "  ·  " + c.Mode + "  ·  " + c.Sha256[..Math.Min(12, c.Sha256.Length)] + "…  ·  "
                + c.AcceptedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), Theme.Size.BodySmall));
        }
        return body;
    }

    private void Do(Action action, string done)
    {
        try
        {
            action();
            _note = done;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException or Rift.Core.Protocol.OperationException)
        {
            _note = ex.Message;
        }
        Refresh(true);
    }

    private static int Lines(string s) => s.Count(c => c == '\n') + 1;
}
