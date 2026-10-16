// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

using Xunit;

using Rift.Core.Config;
using Rift.Core.Journal;
using Rift.Core.Json;
using Rift.Core.Logging;
using Rift.Core.Policy;
using Rift.Core.Protocol;
using Rift.Scripting.Runs;

namespace Rift.Tests;

/// <summary>Plan M1, tests 12 (config), 13 (redaction), 14 (protocol codes documented), plus classifier, sanitiser, journal.</summary>
public sealed class PolicyAndJournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rift-tests-" + Guid.NewGuid().ToString("N"));

    public PolicyAndJournalTests()
    {
        Directory.CreateDirectory(_dir);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    public void Config_load_on_a_missing_or_invalid_file_gives_the_defaults(string? content)
    {
        string path = Path.Combine(_dir, "config.json");
        if (content is not null)
        {
            File.WriteAllText(path, content);
        }
        RiftConfig c = RiftConfig.Load(path);
        Assert.Same(RiftConfig.Default, c);
        Assert.True(c.ActivityCard);
        Assert.Equal(30, c.StartTimeoutS);
        Assert.Contains("SANDBOX", c.SandboxMarkers);
    }

    [Fact]
    public void Config_round_trips_and_bad_fields_keep_their_default()
    {
        string path = Path.Combine(_dir, "config.json");
        var c = new RiftConfig(false, new[] { "BAC_A_SABLE" }, new[] { "Reference" }, new[] { "agent" }, 45);
        c.Save(path);
        RiftConfig back = RiftConfig.Load(path);
        Assert.False(back.ActivityCard);
        Assert.Equal(c.SandboxMarkers, back.SandboxMarkers);
        Assert.Equal(c.ReferenceNames, back.ReferenceNames);
        Assert.Equal(45, back.StartTimeoutS);

        File.WriteAllText(path, "{\"activity_card\": \"no\", \"start_timeout_s\": 9999, \"sandbox_markers\": 3}");
        RiftConfig bad = RiftConfig.Load(path);
        Assert.True(bad.ActivityCard);
        Assert.Equal(RiftConfig.MaxStartTimeoutS, bad.StartTimeoutS);
        Assert.Equal(RiftConfig.DefaultSandboxMarkers, bad.SandboxMarkers);
        Assert.False(bad.Developer);

        File.WriteAllText(path, "{\"developer\": true}");
        Assert.True(RiftConfig.Load(path).Developer);
        (RiftConfig.Default with { Developer = true }).Save(path);
        Assert.True(RiftConfig.Load(path).Developer);

        // A key written twice by hand must not stop the add-in: defaults, no exception.
        File.WriteAllText(path, "{\"developer\": true, \"developer\": false}");
        Assert.Equal(RiftConfig.Default, RiftConfig.Load(path) with { SandboxMarkers = RiftConfig.Default.SandboxMarkers });
    }

    [Theory]
    [InlineData(null, DocumentClass.Unsaved)]
    [InlineData(@"C:\work\Project_SANDBOX_01.rvt", DocumentClass.Sandbox)]
    [InlineData(@"C:\SANDBOX\project.rvt", DocumentClass.Unmarked)]           // the folder does not count, only the file name
    [InlineData(@"\\server\share\copie de travail.rvt", DocumentClass.Sandbox)]
    [InlineData(@"C:\work\Reference.rte", DocumentClass.Reference)]
    [InlineData(@"C:\work\Client.rvt", DocumentClass.Unmarked)]
    public void Documents_are_classified_by_file_name(string? path, DocumentClass expected)
    {
        var config = RiftConfig.Default with { ReferenceNames = new[] { "Reference" } };
        Assert.Equal(expected, DocumentClassifier.Classify(path, config));
    }

    [Theory]
    [InlineData(@"C:\work\Copyright_Studio.rte", DocumentClass.Unmarked)]      // F-R6: COPY inside a word is not a marker
    [InlineData(@"C:\work\Sandboxed.rvt", DocumentClass.Unmarked)]
    [InlineData(@"C:\work\Plan - Copy.rvt", DocumentClass.Sandbox)]
    [InlineData(@"C:\work\Plan.copy.rvt", DocumentClass.Sandbox)]
    [InlineData(@"C:\work\Plan(SANDBOX).rvt", DocumentClass.Sandbox)]
    [InlineData(@"C:\work\Base_Template_SANDBOX.rte", DocumentClass.Sandbox)]
    [InlineData(@"C:\work\SANDBOX2.rvt", DocumentClass.Unmarked)]                // a digit glued to the marker is part of the word
    [InlineData(@"C:\work\Plan_INJ_01.rvt", DocumentClass.Sandbox)]              // a marker that carries its own delimiters
    public void Sandbox_markers_match_delimited_words_only(string path, DocumentClass expected)
    {
        Assert.Equal(expected, DocumentClassifier.Classify(path, RiftConfig.Default));
    }

    [Fact]
    public void Short_or_punctuation_only_markers_are_ignored_at_load_and_at_classification()
    {
        string path = Path.Combine(_dir, "markers.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{\"sandbox_markers\": [\".\", \"_-_-\", \"AB\", \"TEST\"]}");
        RiftConfig config = RiftConfig.Load(path);
        Assert.Equal(new[] { "TEST" }, config.SandboxMarkers);
        Assert.Equal(DocumentClass.Unmarked, DocumentClassifier.Classify(@"C:\work\Client.rvt", config with { SandboxMarkers = new[] { "." } }));
        Assert.False(DocumentClassifier.IsValidMarker("_-_-"));
        Assert.True(DocumentClassifier.IsValidMarker("_INJ_"));
    }

    [Fact]
    public void Redactor_hides_user_host_and_document_folder_and_leaves_the_rest()
    {
        const string text = @"D:\Home\Jane Doe\Projects\Alpha\model.rvt opened by jane doe on WS-042; D:\\Home\\Jane Doe\\Projects\\Alpha\\x.json; D:/Home/Jane Doe/Projects/Alpha/y; other C:\Temp\z";
        string r = Redactor.Redact(text, "Jane Doe", "WS-042", @"D:\Home\Jane Doe\Projects\Alpha\");
        Assert.Equal(@".\model.rvt opened by <USER> on <HOST>; .\\x.json; ./y; other C:\Temp\z", r);
        Assert.Equal("abc", Redactor.Redact("abc", "a", "", null));   // one-letter names are not redacted
    }

    [Fact]
    public void Journal_writes_redacted_files_attachments_and_purges_old_runs()
    {
        DateTimeOffset now = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);
        var journal = new RunJournal(_dir, new RedactionContext("jdoe", "WS-1"), TimeSpan.FromDays(14), FileLog.Null, () => now);
        string folder = journal.Begin("abc", "run", "print('jdoe')\n", new JsonObject { ["p"] = @"D:\docs\a.rvt" }, @"D:\docs");
        string? reference = journal.WriteAttachment("abc", "result.json", "{\"host\":\"WS-1\"}");
        journal.End("abc", new JsonObject { ["outcome"] = "ok" }, "ok", 12);

        Assert.Equal("runs/" + folder + "/result.json", reference);
        Assert.Equal("print('<USER>')\n", File.ReadAllText(Path.Combine(_dir, folder, "script.py")));
        Assert.Contains(@".\\a.rvt", File.ReadAllText(Path.Combine(_dir, folder, "request.json")), StringComparison.Ordinal);
        Assert.Equal("{\"host\":\"<HOST>\"}", File.ReadAllText(Path.Combine(_dir, folder, "result.json")));
        Assert.Single(File.ReadAllLines(Path.Combine(_dir, RunJournal.IndexFileName)));

        now += TimeSpan.FromDays(15);
        Assert.Equal(1, journal.Purge());
        Assert.False(Directory.Exists(Path.Combine(_dir, folder)));
    }

    [Fact]
    public void Sanitizer_strips_controls_and_bidi_keeps_text_and_truncates()
    {
        Assert.Equal("a\tb\nc\r\nd", Sanitizer.Sanitize("a\tb\nc\r\nd\u0000\u202E\u2066\u200B\u0085"));
        Assert.Equal("élévation 😀", Sanitizer.Sanitize("élévation 😀"));
        string cut = Sanitizer.Sanitize(new string('x', 10), 4);
        Assert.Equal("xxxx" + Sanitizer.TruncationMark, cut);
    }

    [Fact]
    public void Sanitizer_strips_invisible_carriers_of_instructions_F_R4()
    {
        // TAG block (ASCII smuggling): letters spelt in U+E0072 U+E0075 U+E006E, invisible in Revit.
        Assert.Equal("Comment", Sanitizer.Sanitize("Comment\U000E0072\U000E0075\U000E006E\U000E007F"));
        // Every format character: ZWNJ, ZWJ, word joiner, invisible operators, soft hyphen, Arabic number sign.
        Assert.Equal("abcdef", Sanitizer.Sanitize("a\u200Cb\u200Dc\u2060d\u2064e\u00ADf\u0600"));
        // Private use (BMP and plane 15), variation selectors, noncharacters.
        Assert.Equal("xy", Sanitizer.Sanitize("x\uE000\U000F0000\uFE0F\U000E0100\uFFFF\uFDD0y"));
        // Lone surrogates, high or low; a valid pair is kept.
        Assert.Equal("ab\U0001F600", Sanitizer.Sanitize("a\uD83Db\uDC00\uD83D\uDE00"));
        // Line and paragraph separators become a line feed.
        Assert.Equal("one\ntwo\nthree", Sanitizer.Sanitize("one\u2028two\u2029three"));
        Assert.True(Sanitizer.IsStripped('\uD800'));
        Assert.False(Sanitizer.IsStripped('\u00E9'));
    }

    [Theory]
    [InlineData("   at Autodesk.Revit.DB.Document.Save()\n   at Microsoft.Scripting...", "save")]
    [InlineData("   at Autodesk.Revit.DB.Document.SaveAs(String filepath)", "save_as")]
    [InlineData("   at Autodesk.Revit.DB.Document.Close(Boolean saveModified)", "close")]
    [InlineData("   at Autodesk.Revit.DB.Document.SynchronizeWithCentral(TransactWithCentralOptions a, SynchronizeWithCentralOptions b)", "sync")]
    [InlineData("   at Autodesk.Revit.DB.Document.Export(String folder, String name, DWGExportOptions options)", "export")]
    [InlineData("   at Autodesk.Revit.DB.Document.GetElement(ElementId id)", null)]
    public void Blocked_document_calls_are_named_from_the_stack_trace(string trace, string? expected)
    {
        Assert.Equal(expected, BlockedCalls.ClassifyText(trace));
    }

    [Fact]
    public void Protocol_codes_of_tranche_3_exist_and_are_documented()
    {
        string doc = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "docs", "PROTOCOL.md"));
        foreach (string code in new[] { ErrorCodes.TimeoutUnavailable, ErrorCodes.WriteInQuery, ErrorCodes.JobNotFound, ErrorCodes.RevitBusy, ErrorCodes.ScriptError, ErrorCodes.Timeout })
        {
            Assert.Contains(code, doc, StringComparison.Ordinal);
        }
        Assert.Contains("result_ref", doc, StringComparison.Ordinal);
        Assert.Contains("stdout_truncated", doc, StringComparison.Ordinal);
        Assert.True(typeof(ErrorCodes).GetFields().Select(f => (string)f.GetValue(null)!).All(c => doc.Contains(c, StringComparison.Ordinal)));
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
