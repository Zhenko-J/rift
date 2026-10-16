// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Windows;
using System.Windows.Controls;

using Rift.Core.Control;
using Rift.Ui.Kit;

namespace Rift.Ui.Windows;

/// <summary>What the session window asks about: the client, and the project that "active project" would mean.</summary>
/// <param name="ProjectTitle">Title of the active document when the window opened; null when none is open.</param>
/// <param name="ProjectClass">Its class (sandbox, reference, unsaved, unmarked).</param>
public sealed record SessionQuestion(string Client, string? ProjectTitle, string? ProjectClass, SessionChoice? Current);

/// <summary>
/// The session question (ADR-033), asked in Revit when an assistant session starts, so that neither the assistant nor text
/// found in the model can answer it: where the assistant may change the model (SANDBOX copies only, or the active project)
/// and whether a kept change waits for Keep/Discard (supervised) or not (full auto). Until it is answered every change is
/// refused with SESSION_PENDING; reads go on. Defaults are the cautious ones.
/// </summary>
public sealed class SessionWindow : RiftWindow
{
    private readonly IUiHost _host;
    private readonly SessionQuestion _question;
    private WorkScope _scope;
    private Supervision _supervision;
    private string? _note;

    public SessionWindow(IUiHost host, SessionQuestion question, IntPtr owner)
        : base("session", owner, 640, 600, TimeSpan.FromSeconds(30))
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _question = question ?? throw new ArgumentNullException(nameof(question));
        _scope = question.Current?.Scope ?? WorkScope.Sandbox;
        _supervision = question.Current?.Supervision ?? Supervision.Supervised;
        if (question.ProjectTitle is null)
        {
            _scope = WorkScope.Sandbox;
        }
    }

    protected override object Fingerprint() => (_scope, _supervision, _note);

    protected override UIElement Build()
    {
        var body = new StackPanel();
        body.Children.Add(Z.MicroLabel("new assistant session"));
        TextBlock who = Z.Value(_question.Client, 22);
        who.Margin = new Thickness(0, 8, 0, 4);
        body.Children.Add(who);
        TextBlock intro = Z.Aside("choose once how it works on this Revit; it will not ask again before each change");
        intro.Margin = new Thickness(0, 0, 0, 24);
        body.Children.Add(intro);

        body.Children.Add(Z.MicroLabel("where"));
        var where = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        where.Children.Add(Option("sandbox copies only", _scope == WorkScope.Sandbox, () => _scope = WorkScope.Sandbox));
        if (_question.ProjectTitle is not null)
        {
            where.Children.Add(Option("the active project", _scope == WorkScope.ActiveProject, () => _scope = WorkScope.ActiveProject));
        }
        body.Children.Add(where);
        body.Children.Add(Note(_scope == WorkScope.Sandbox
            ? "changes only in documents whose file name carries a SANDBOX marker"
            : "changes in " + _question.ProjectTitle + " (" + (_question.ProjectClass ?? "?") + "), and in any SANDBOX copy · saving stays yours"));

        body.Children.Add(Z.MicroLabel("how"));
        var how = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        how.Children.Add(Option("supervised", _supervision == Supervision.Supervised, () => _supervision = Supervision.Supervised));
        how.Children.Add(Option("full auto", _supervision == Supervision.FullAuto, () => _supervision = Supervision.FullAuto));
        body.Children.Add(how);
        body.Children.Add(Note(_supervision == Supervision.Supervised
            ? "Revit shows keep / discard with the change receipt before any change is kept; reads and dry runs never ask"
            : "changes are kept without a dialog; each one is still one undo entry 'Agent: …' and appears in history"));

        var verbs = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        verbs.Children.Add(Z.GhostButton("start", () =>
        {
            if (_host.ChooseSession(_scope, _supervision))
            {
                Close();
            }
            else
            {
                _note = "another assistant session started meanwhile: close this window, the new one asks again";
                Refresh(true);
            }
        }));
        body.Children.Add(verbs);
        if (_note is not null)
        {
            body.Children.Add(Note(_note));
        }
        return body;
    }

    private Button Option(string verb, bool selected, Action choose)
    {
        Button b = Z.GhostButton(verb, () =>
        {
            choose();
            Refresh(true);
        }, selected);
        b.Margin = new Thickness(0, 0, Theme.Size.Gutter, Theme.Size.Gutter);
        return b;
    }

    private static TextBlock Note(string text)
    {
        TextBlock t = Z.Aside(text, Theme.Size.BodySmall);
        t.Margin = new Thickness(0, 0, 0, 24);
        return t;
    }
}
