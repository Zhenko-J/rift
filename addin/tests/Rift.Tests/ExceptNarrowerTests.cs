// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;

using Xunit;

using Rift.Scripting;
using Rift.Scripting.Python;

namespace Rift.Tests;

/// <summary>Plan M1, test 15b: handlers that could catch the timeout are rewritten through the AST, lines preserved.</summary>
public sealed class ExceptNarrowerTests : IDisposable
{
    private readonly IronPythonEngine _engine = new(Array.Empty<System.Reflection.Assembly>(), null);

    [Theory]
    [InlineData("try:\n    pass\nexcept:\n    pass\n", "except Exception:")]
    [InlineData("try:\n    pass\nexcept KeyboardInterrupt:\n    pass\n", "except Exception:")]
    [InlineData("try:\n    pass\nexcept BaseException as e:\n    pass\n", "except Exception as e:")]
    [InlineData("try:\n    pass\nexcept (ValueError, BaseException):\n    pass\n", "except Exception:")]
    [InlineData("try:\n    pass\nexcept (KeyboardInterrupt, TypeError) as e:\n    pass\n", "except Exception as e:")]
    [InlineData("try:\n    pass\nexcept builtins.BaseException:\n    pass\n", "except Exception:")]
    public void Handlers_that_catch_the_interrupt_are_rewritten(string source, string expected)
    {
        string narrowed = ExceptNarrower.Narrow(source);
        Assert.Contains(expected, narrowed, StringComparison.Ordinal);
        Assert.DoesNotContain("BaseException", narrowed, StringComparison.Ordinal);
        Assert.DoesNotContain("KeyboardInterrupt", narrowed, StringComparison.Ordinal);
        Assert.Equal(source.Split('\n').Length, narrowed.Split('\n').Length);
    }

    [Theory]
    [InlineData("try:\n    pass\nexcept (ValueError, TypeError):\n    pass\n")]
    [InlineData("try:\n    pass\nexcept Exception as e:\n    pass\n")]
    [InlineData("s = 'except:'\n# except BaseException:\nt = \"\"\"\nexcept KeyboardInterrupt:\n\"\"\"\n")]
    [InlineData("def f(:\n    except:\n")]
    public void Other_handlers_strings_comments_and_invalid_code_are_untouched(string source)
    {
        Assert.Equal(source, ExceptNarrower.Narrow(source));
    }

    [Fact]
    public void Multi_line_tuple_keeps_its_line_count_and_nested_handlers_are_found()
    {
        string source = "def f():\n    for i in range(3):\n        try:\n            pass\n        except (ValueError,\n                BaseException):\n            pass\n    return 1\nx = f()\n";
        string narrowed = ExceptNarrower.Narrow(source);
        Assert.Equal(source.Split('\n').Length, narrowed.Split('\n').Length);
        Assert.Contains("except (Exception\n):", narrowed, StringComparison.Ordinal);
        CompileResult c = _engine.Compile(source, "<test>");
        Assert.True(c.Ok, c.Message);
    }

    [Theory]
    [InlineData("i = 0\nwhile i < 500000000:\n    try:\n        i += 1\n    except KeyboardInterrupt:\n        pass\n")]
    [InlineData("i = 0\nwhile i < 500000000:\n    try:\n        i += 1\n    except (ValueError, BaseException):\n        pass\n")]
    [InlineData("i = 0\nwhile i < 500000000:\n    try:\n        i += 1\n    except BaseException as e:\n        pass\n")]
    [InlineData("i = 0\nwhile i < 500000000:\n    try:\n        i += 1\n    except:\n        pass\n")]
    [InlineData("exec('i = 0\\nwhile i < 500000000:\\n    try:\\n        i += 1\\n    except:\\n        pass\\n')\n")]
    public void The_deadline_cuts_a_loop_under_each_form(string source)
    {
        CompileResult c = _engine.Compile(source, "<test>");
        Assert.True(c.Ok, c.Message);
        RunResult r = _engine.Run(c.Script!, new ScriptContext(new object(), new object(), null, null, new JsonObject(), (_, _) => { }, TextWriter.Null, TextWriter.Null),
            TimeSpan.FromSeconds(0.5), CancellationToken.None);
        Assert.True(r.TimedOut, r.Error?.Message);
        Assert.True(r.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Syntax_errors_keep_their_line()
    {
        CompileResult c = _engine.Compile("x = 1\ny = (\nz = 3\n", "<test>");
        Assert.False(c.Ok);
        Assert.InRange(c.Line, 2, 4);
    }

    public void Dispose() => _engine.Dispose();
}
