using System.Runtime.CompilerServices;
using DiffEngine;
using EmptyFiles;
using VerifyTests;

namespace SkarpSharp.Tests;

/// <summary>
/// The file-content checks (CLI golden outputs, the SARIF golden output and the contract shape
/// snapshots) run through Verify.TUnit. The verified files stay in their folders with Verify's naming:
/// Golden/expected/{TestClass}.{TestMethod}.verified.{ext} (Verify's default name) and, because a shape snapshot belongs
/// to a schema version rather than to a test, Contracts/shapes/{contract}.v{version}.verified.txt. On a mismatch Verify
/// writes the .received. file next to it (git-ignored). What varies between runs is scrubbed with Verify scrubbers at
/// each call site; the comparison itself is exact.
/// <para>After an intended change: run the tests once with SKARP_UPDATE_GOLDEN=1 (golden outputs) or SKARP_UPDATE_SHAPES=1
/// (shape snapshots) and review the diff, as before; or accept the received file. Verify writes text files with a UTF-8
/// byte order mark; the update flags remove it again, so the committed files stay as they were.</para>
/// </summary>
static class Snapshots
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // The SARIF golden output is text (JSON); Verify treats unknown extensions as binary.
        FileExtensions.AddTextExtension("sarif");
        // Never start a diff tool on a build server (DiffEngine also detects most of them itself) or when CI is set.
        if (BuildServerDetector.Detected || Environment.GetEnvironmentVariable("CI") is { Length: > 0 })
            DiffRunner.Disabled = true;
    }

    /// <summary>The test project folder, from this file's compile-time path (Verify resolves snapshot folders the same way).</summary>
    static string ProjectDirectory([CallerFilePath] string file = "") => Path.GetDirectoryName(file)!;

    /// <summary>
    /// Compares <paramref name="text"/>, after the scrubbers <paramref name="configure"/> adds, with the verified file in
    /// <paramref name="directory"/> (relative to the test project folder), named {fileName}.verified.{extension}, or by
    /// Verify's default {TestClass}.{TestMethod} when <paramref name="fileName"/> is null. With the environment variable
    /// <paramref name="updateVariable"/> set to 1 the verified file is (re)written instead.
    /// </summary>
    public static async Task Verify(string text, string directory, string? fileName, string extension, string updateVariable,
        Action<VerifySettings>? configure = null)
    {
        var folder = Path.Combine(ProjectDirectory(), directory);
        var settings = new VerifySettings();
        settings.UseDirectory(folder);
        if (fileName is not null) settings.UseFileName(fileName);
        // The tool version changes with every tag and commit (MinVer): snapshots never hold it.
        if (SkarpSharp.Commands.SkarpCommand.ToolVersion is { Length: > 0 } toolVersion)
            settings.AddScrubber(sb => sb.Replace(toolVersion, "{toolVersion}"));
        configure?.Invoke(settings);
        bool update = Environment.GetEnvironmentVariable(updateVariable) == "1";
        if (update) settings.AutoVerify();
        await VerifyTUnit.Verifier.Verify(text, extension, settings);
        if (update)
            foreach (var file in Directory.GetFiles(folder, "*.verified.*")) StripByteOrderMark(file);
    }

    static void StripByteOrderMark(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes is [0xEF, 0xBB, 0xBF, ..]) File.WriteAllBytes(path, bytes[3..]);
    }
}
