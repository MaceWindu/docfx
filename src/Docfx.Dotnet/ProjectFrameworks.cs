// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Docfx.Common;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

#nullable enable

namespace Docfx.Dotnet;

/// <summary>
/// The framework MSBuild evaluates a project to.
/// </summary>
/// <param name="Name">The framework as the project names it, <c>net8.0-windows</c>, or an alias such as <c>modern</c>.</param>
/// <param name="Moniker">The framework with the platform version it stands for, <c>net8.0-windows7.0</c>.</param>
internal sealed record ProjectFramework(string Name, string Moniker);

/// <summary>
/// What <c>dotnet msbuild</c> evaluates a project to: its <paramref name="Framework"/>, or the
/// <paramref name="Failure"/> that says why it cannot, for the caller to report.
/// </summary>
/// <param name="ListsFrameworks">
/// Evaluated without a framework, the project sets no <c>TargetFramework</c> but lists frameworks in
/// <c>TargetFrameworks</c>, which the workspace loads one at a time, with each as a global property.
/// </param>
internal sealed record ProjectEvaluation(ProjectFramework? Framework, string? Failure, bool ListsFrameworks = false);

/// <summary>
/// What MSBuild says the target frameworks of the projects a workspace loads are.
/// </summary>
/// <remarks>
/// <see cref="MSBuildWorkspace"/> names a project loaded for one of several frameworks after the framework,
/// <c>Foo(net8.0)</c>, and reports the framework of every project it loads to the progress it is given, a
/// project that targets a single framework included. Neither says what an alias in <c>TargetFrameworks</c>
/// stands for, nor the platform version a framework such as <c>net8.0-windows</c> leaves out: for those, the
/// project is evaluated by <c>dotnet msbuild</c>, with the SDK the workspace loaded it with and the MSBuild
/// properties of the metadata item. Its <c>-getProperty</c> switch needs the .NET SDK 8.0.100 or later.
/// </remarks>
internal sealed class ProjectFrameworks : IProgress<ProjectLoadProgress>
{
    // An evaluation takes seconds; one that takes this long is waiting on something that may never come.
    internal static readonly TimeSpan EvaluationTimeout = TimeSpan.FromMinutes(2);

    private static readonly string[] s_properties = ["TargetFramework", "TargetFrameworks", "TargetFrameworkMoniker", "TargetPlatformIdentifier", "TargetPlatformVersion"];

    // What MSBuildLocator sets in a process to point MSBuild at an SDK.
    private static readonly string[] s_locatedSdkVariables = ["MSBuildSDKsPath", "MSBuildExtensionsPath", "MSBUILD_EXE_PATH"];

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> _loaded = new(FilePathComparer.OSPlatformSensitiveStringComparer);
    private readonly ConcurrentDictionary<string, string> _openedWith = new(FilePathComparer.OSPlatformSensitiveStringComparer);
    private readonly ConcurrentDictionary<(string Path, string? TargetFramework), Task<ProjectEvaluation>> _evaluated = new(EvaluationKeyComparer.Instance);
    private readonly Func<string, string?, Task<ProjectEvaluation>> _evaluate;
    private string? _opening;

    /// <param name="properties">The MSBuild properties the workspace loads projects with.</param>
    /// <param name="evaluationTimeout">How long an evaluation may take, <see cref="EvaluationTimeout"/> when not given.</param>
    public ProjectFrameworks(IReadOnlyDictionary<string, string> properties, TimeSpan? evaluationTimeout = null)
    {
        var timeout = evaluationTimeout ?? EvaluationTimeout;
        var dotnet = GetDotnet(Environment.GetEnvironmentVariable("DOTNET_ROOT"));
        _evaluate = (path, targetFramework) => EvaluateWithMSBuild(dotnet, path, GetSdkDirectory(path), targetFramework, properties, timeout);
    }

    /// <param name="evaluate">Evaluates a project, for a framework when it is given, as MSBuild does.</param>
    internal ProjectFrameworks(Func<string, string?, Task<ProjectEvaluation>> evaluate)
    {
        _evaluate = evaluate;
    }

    /// <summary>
    /// Says that the workspace opens <paramref name="path"/> next, for the projects it loads with it.
    /// </summary>
    public void Opening(string path) => _opening = path;

    public void Report(ProjectLoadProgress value) => AddLoaded(value.FilePath, value.TargetFramework);

    internal void AddLoaded(string? path, string? targetFramework)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        if (_opening is { } opening)
        {
            _openedWith.TryAdd(path, opening);
        }

        if (!string.IsNullOrEmpty(targetFramework))
        {
            _loaded.GetOrAdd(path, _ => new(StringComparer.OrdinalIgnoreCase)).TryAdd(targetFramework, true);
        }
    }

    /// <summary>
    /// The folder whose <c>global.json</c> selects the SDK the workspace loaded <paramref name="path"/> with.
    /// </summary>
    /// <remarks>
    /// Each project the workspace opens gets a build host of its own, which takes its SDK from the folder of that
    /// project and keeps it for the projects it references, which it loads too: a project first loaded as a
    /// reference has the SDK of the project that references it.
    /// </remarks>
    internal string GetSdkDirectory(string path)
    {
        return Path.GetDirectoryName(_openedWith.TryGetValue(path, out var opened) ? opened : path)!;
    }

    /// <summary>
    /// The <c>dotnet</c> the workspace's build host takes its SDK from: the one in <paramref name="dotnetRoot"/>, a
    /// <c>DOTNET_ROOT</c>, before the <c>dotnet</c> both start as, as MSBuildLocator does.
    /// </summary>
    internal static string GetDotnet(string? dotnetRoot)
    {
        var dotnet = Path.Combine(dotnetRoot ?? "", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return !string.IsNullOrEmpty(dotnetRoot) && File.Exists(dotnet) ? dotnet : "dotnet";
    }

    /// <summary>
    /// Gets the framework of a project as the project names it: <c>net8.0-windows</c>, or an alias.
    /// </summary>
    /// <returns>A <c>Name</c> of <see langword="null"/> when MSBuild cannot say, and the <c>Failure</c> that says why.</returns>
    public async Task<(string? Name, string? Failure)> GetName(Project project)
    {
        if (project.FilePath is null)
        {
            return TargetFrameworkHelper.GetTargetFrameworkFromName(project) is { } unsaved
                ? (unsaved, null)
                : (null, $"The workspace names no file for project '{project.Name}', so which framework it targets is not known.");
        }

        // A project that targets a single framework is loaded for that one alone, and has no framework in its name:
        // the `(Legacy)` of a `Foo(Legacy).csproj` is part of its file name. Only a file loaded for several
        // frameworks names one there.
        var path = project.FilePath;
        _loaded.TryGetValue(path, out var loaded);
        if (loaded is { Count: 1 })
        {
            return (loaded.Keys.First(), null);
        }

        if (loaded is { Count: > 1 } && TargetFrameworkHelper.GetTargetFrameworkFromName(project) is { } name)
        {
            return (name, null);
        }

        // The workspace reports the framework of SDK-style projects only: a non-SDK project is evaluated, also
        // when its file name ends in parentheses.
        var evaluation = await Evaluate(project.FilePath, null);
        return (evaluation.Framework?.Name, evaluation.Failure);
    }

    /// <summary>
    /// Evaluates a project, for <paramref name="targetFramework"/> when it is given, once.
    /// </summary>
    public Task<ProjectEvaluation> Evaluate(string path, string? targetFramework)
    {
        return _evaluated.GetOrAdd((path, targetFramework), key => _evaluate(key.Path, key.TargetFramework));
    }

    /// <summary>
    /// Evaluates a project the workspace loaded for <paramref name="targetFramework"/> alone as it loaded it: one that
    /// sets <c>TargetFramework</c> without the framework as a global property, one whose <c>TargetFrameworks</c> lists
    /// only that framework with it. What a <c>Directory.Build.props</c> sets for a <c>TargetFramework</c>, before the
    /// project sets it, then is what the workspace saw.
    /// </summary>
    public async Task<ProjectEvaluation> EvaluateAsLoaded(string path, string targetFramework)
    {
        var evaluation = await Evaluate(path, null);
        return evaluation.ListsFrameworks ? await Evaluate(path, targetFramework) : evaluation;
    }

    private static async Task<ProjectEvaluation> EvaluateWithMSBuild(
        string dotnet, string path, string sdkDirectory, string? targetFramework, IReadOnlyDictionary<string, string> properties, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(dotnet)
        {
            // so that the SDK is the one a `global.json` selects for the workspace
            WorkingDirectory = sdkDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // The output is read as JSON: no first-run banner before it. And no MSBuild server or node outlives the
        // evaluation, holding its output open.
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

        // The SDK a parent MSBuild, such as one that runs docfx from an `Exec` task, located: the workspace's build
        // host locates its own, which these would otherwise override for the evaluation.
        foreach (var variable in s_locatedSdkVariables)
        {
            start.Environment.Remove(variable);
        }

        start.ArgumentList.Add("msbuild");
        start.ArgumentList.Add(path);
        start.ArgumentList.Add("-nologo");
        start.ArgumentList.Add("-nodeReuse:false");

        // The workspace reads no `Directory.Build.rsp`, whose switches, such as `-bl` or `-p:`, would write files next
        // to the project or evaluate it otherwise.
        start.ArgumentList.Add("-noAutoResponse");
        foreach (var property in s_properties)
        {
            start.ArgumentList.Add($"-getProperty:{property}");
        }

        foreach (var (name, value) in properties)
        {
            if (targetFramework is null || !name.Equals("TargetFramework", StringComparison.OrdinalIgnoreCase))
            {
                start.ArgumentList.Add($"-property:{name}={Escape(value)}");
            }
        }

        if (targetFramework is not null)
        {
            start.ArgumentList.Add($"-property:TargetFramework={Escape(targetFramework)}");
        }

        try
        {
            return await Run(start, timeout) is { } result
                ? ReadEvaluation(path, targetFramework, result.Output, result.Error, result.ExitCode)
                : Failed(path, targetFramework, $"it did not answer within {timeout.TotalSeconds} seconds");
        }
        catch (Exception ex)
        {
            return Failed(path, targetFramework, ex.Message);
        }
    }

    /// <summary>
    /// Reads what <c>dotnet msbuild -getProperty</c> wrote for a project.
    /// </summary>
    internal static ProjectEvaluation ReadEvaluation(string path, string? targetFramework, string output, string error, int exitCode)
    {
        var text = string.Join(" ", $"{output}\n{error}".Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (exitCode != 0)
        {
            // MSBuild before 17.8 knows no `-getProperty`
            return Failed(path, targetFramework, text.Contains("MSB1001", StringComparison.Ordinal)
                ? $"its '-getProperty' switch needs the .NET SDK 8.0.100 or later: {text}"
                : text);
        }

        try
        {
            using var document = JsonDocument.Parse(ReadJson(output));
            var values = document.RootElement.GetProperty("Properties");
            string Get(string property) => values.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";

            // as the workspace tells a project it loads once per framework from one it loads once
            if (targetFramework is null && Get("TargetFramework").Length == 0 && Get("TargetFrameworks").Length > 0)
            {
                return Failed(path, targetFramework, "it lists its target frameworks in 'TargetFrameworks' only") with { ListsFrameworks = true };
            }

            return Create(Get("TargetFramework"), Get("TargetFrameworkMoniker"), Get("TargetPlatformIdentifier"), Get("TargetPlatformVersion"))
                is { } evaluated ? new(evaluated, null) : Failed(path, targetFramework, "it names no target framework");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return Failed(path, targetFramework, text.Length > 0 ? text : $"it wrote nothing, and exited with code {exitCode}");
        }
    }

    private static ProjectEvaluation Failed(string path, string? targetFramework, string reason)
    {
        var framework = targetFramework is null ? "" : $" for '{targetFramework}'";
        return new(null, $"MSBuild cannot evaluate project '{path}'{framework}, so which framework it targets is not known: {reason}");
    }

    /// <summary>
    /// Reads the JSON <c>-getProperty</c> writes out of the output of <c>dotnet msbuild</c>, which can have text
    /// around it, such as a notice of a first run or a warning, that may hold a <c>{</c> as well.
    /// </summary>
    internal static string ReadJson(string output)
    {
        // The JSON starts a line, and only its first line starts with `{`: the lines within it are indented.
        var start = output.StartsWith('{') ? 0 : -1;
        if (output.LastIndexOf("\n{", StringComparison.Ordinal) is var line and >= 0)
        {
            start = line + 1;
        }

        if (start < 0)
        {
            return output;
        }

        var end = output.LastIndexOf('}');
        return end > start ? output[start..(end + 1)] : output[start..];
    }

    /// <summary>
    /// Runs a process to its end, or stops it after <paramref name="timeout"/>.
    /// </summary>
    /// <returns><see langword="null"/> when it did not end in time.</returns>
    internal static async Task<(string Output, string Error, int ExitCode)?> Run(ProcessStartInfo start, TimeSpan timeout)
    {
        using var process = Process.Start(start)!;
        var reading = process.StandardOutput.ReadToEndAsync();
        var readingError = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);

            // A process it started can hold its output open after it ends.
            var output = await reading.WaitAsync(cancellation.Token);
            var error = await readingError.WaitAsync(cancellation.Token);
            return (output, error, process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            // An evaluation that waits on something, such as an SDK resolver, would otherwise block the whole run.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or AggregateException or Win32Exception or NotSupportedException)
            {
                // It ended meanwhile, or a process of its tree cannot be stopped: the evaluation did not answer
                // either way, which is what is reported.
            }

            // The output ends with the processes that hold it; read it before the process is disposed.
            await Task.WhenAny(Task.WhenAll(reading, readingError), Task.Delay(TimeSpan.FromSeconds(5)));
            return null;
        }
    }

    /// <summary>
    /// Names the framework MSBuild evaluated: <c>TargetFrameworkMoniker</c> is the framework an alias stands for,
    /// and <c>TargetPlatformVersion</c> the version a platform it leaves out defaults to.
    /// </summary>
    internal static ProjectFramework? Create(string targetFramework, string targetFrameworkMoniker, string targetPlatformIdentifier, string targetPlatformVersion)
    {
        var framework = TargetFrameworkHelper.ParseFrameworkName(targetFrameworkMoniker);
        var name = targetFramework.Length > 0 ? targetFramework : framework;
        if (name is null)
        {
            return null;
        }

        // The SDK sets a platform for .NET Framework as well, which no framework name gives.
        var moniker = framework is null ? name
            : targetPlatformIdentifier.Length > 0 && TargetFrameworkHelper.IsNet5OrLater(framework)
                ? $"{framework}-{targetPlatformIdentifier.ToLowerInvariant()}{targetPlatformVersion}"
                : framework;
        return new(name, moniker);
    }

    // `;` and `,` separate properties on the command line. A `%` is left alone: MSBuild reads a value on the command
    // line as escaped, as the workspace's global properties, so `%3B` is a `;` in both.
    private static string Escape(string value)
    {
        return value.Replace(";", "%3B").Replace(",", "%2C");
    }

    // A path as the file system compares it, a framework as MSBuild does: case-insensitively.
    private sealed class EvaluationKeyComparer : IEqualityComparer<(string Path, string? TargetFramework)>
    {
        public static readonly EvaluationKeyComparer Instance = new();

        public bool Equals((string Path, string? TargetFramework) x, (string Path, string? TargetFramework) y)
        {
            return FilePathComparer.OSPlatformSensitiveStringComparer.Equals(x.Path, y.Path)
                && StringComparer.OrdinalIgnoreCase.Equals(x.TargetFramework, y.TargetFramework);
        }

        public int GetHashCode((string Path, string? TargetFramework) obj)
        {
            return HashCode.Combine(
                FilePathComparer.OSPlatformSensitiveStringComparer.GetHashCode(obj.Path),
                obj.TargetFramework is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(obj.TargetFramework));
        }
    }
}
