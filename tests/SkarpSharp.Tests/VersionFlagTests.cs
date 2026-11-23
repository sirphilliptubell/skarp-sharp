using SkarpSharp.Commands;

namespace SkarpSharp.Tests;

/// <summary>The <c>--version</c> flag prints one line, "skarp &lt;version&gt;", and exits 0; <c>--help</c> prints the usage on
/// standard output.</summary>
public class VersionFlagTests
{
    static async Task<(int Exit, string Out, string Err)> Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int exit = await SkarpCommand.RunAsync(args, new CommandIO { Out = o, Error = e, OpenStdout = () => Stream.Null });
        return (exit, o.ToString(), e.ToString());
    }

    [Test]
    [Arguments("--version")]
    [Arguments("-V")]
    public async Task Version_flag_prints_the_tool_version(string flag)
    {
        var r = await Run(flag);
        await Assert.That(r.Exit).IsEqualTo(0);
        await Assert.That(r.Out).IsEqualTo($"skarp {SkarpCommand.ToolVersion}{Environment.NewLine}");
        await Assert.That(r.Err).IsEqualTo("");
    }

    [Test]
    public async Task Version_is_listed_in_the_usage() =>
        await Assert.That(SkarpSharp.Configuration.CommandLine.Usage).Contains("skarp --version");

    [Test]
    [Arguments("--help")]
    [Arguments("-h")]
    public async Task Help_flag_prints_the_usage_on_standard_output_with_exit_0(string flag)
    {
        foreach (var args in new[] { new[] { flag }, new[] { "analyze", flag }, new[] { "hotspots", flag } })
        {
            var r = await Run(args);
            await Assert.That(r.Exit).IsEqualTo(0);
            await Assert.That(r.Out).Contains("Usage:");
            await Assert.That(r.Err).IsEqualTo("");
        }
    }
}
