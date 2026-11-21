// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
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
internal sealed record ProjectEvaluation(ProjectFramework? Framework, string? Failure);

/// <summary>
/// What MSBuild says the target frameworks of the projects a workspace loads are.
/// </summary>
/// <remarks>
/// <see cref="MSBuildWorkspace"/> names a project loaded for one of several frameworks after the framework,
/// <c>Foo(net8.0)</c>, and reports the framework of every project it loads to the progress it is given, a
/// project that targets a single framework included. Neither says what an alias in <c>TargetFrameworks</c>
/// stands for, nor the platform version a framework such as <c>net8.0-windows</c> leaves out: for those, the
/// project is evaluated by <c>dotnet msbuild</c>, with the SDK a <c>global.json</c> of the project selects and the
/// MSBuild properties of the metadata item.
/// </remarks>
internal sealed class ProjectFrameworks : IProgress<ProjectLoadProgress>
{
    private static readonly string[] s_properties = ["TargetFramework", "TargetFrameworkMoniker", "TargetPlatformIdentifier", "TargetPlatformVersion"];

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> _loaded = new(FilePathComparer.OSPlatformSensitiveStringComparer);
    private readonly ConcurrentDictionary<(string Path, string? TargetFramework), Task<ProjectEvaluation>> _evaluated = new();
    private readonly Func<string, string?, Task<ProjectEvaluation>> _evaluate;

    /// <param name="properties">The MSBuild properties the workspace loads projects with.</param>
    public ProjectFrameworks(IReadOnlyDictionary<string, string> properties)
    {
        _evaluate = (path, targetFramework) => EvaluateWithMSBuild(path, targetFramework, properties);
    }

    /// <param name="evaluate">Evaluates a project, for a framework when it is given, as MSBuild does.</param>
    internal ProjectFrameworks(Func<string, string?, Task<ProjectEvaluation>> evaluate)
    {
        _evaluate = evaluate;
    }

    public void Report(ProjectLoadProgress value) => AddLoaded(value.FilePath, value.TargetFramework);

    internal void AddLoaded(string? path, string? targetFramework)
    {
        if (!string.IsNullOrEmpty(targetFramework) && !string.IsNullOrEmpty(path))
        {
            _loaded.GetOrAdd(path, _ => new(StringComparer.OrdinalIgnoreCase)).TryAdd(targetFramework, true);
        }
    }

    /// <summary>
    /// Gets the framework of a project as the project names it: <c>net8.0-windows</c>, or an alias.
    /// </summary>
    /// <returns>A <c>Name</c> of <see langword="null"/> when MSBuild cannot say, and the <c>Failure</c> that says why.</returns>
    public async Task<(string? Name, string? Failure)> GetName(Project project)
    {
        // A project that targets a single framework is loaded for that one alone, and has no framework in its name:
        // the `(Legacy)` of a `Foo(Legacy).csproj` is part of its file name.
        if (project.FilePath is { } path && _loaded.TryGetValue(path, out var loaded) && loaded.Count == 1)
        {
            return (loaded.Keys.First(), null);
        }

        if (TargetFrameworkHelper.GetTargetFrameworkFromName(project) is { } name)
        {
            return (name, null);
        }

        // which the workspace reports for SDK-style projects only
        if (project.FilePath is null)
        {
            return (null, $"The workspace names no file for project '{project.Name}', so which framework it targets is not known.");
        }

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

    private static async Task<ProjectEvaluation> EvaluateWithMSBuild(string path, string? targetFramework, IReadOnlyDictionary<string, string> properties)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            // so that the SDK is the one a `global.json` of the project selects, as for the workspace
            WorkingDirectory = Path.GetDirectoryName(path),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("msbuild");
        start.ArgumentList.Add(path);
        start.ArgumentList.Add("-nologo");
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

        string output;
        string error;
        int exitCode;
        try
        {
            using var process = Process.Start(start)!;
            var reading = process.StandardOutput.ReadToEndAsync();
            var readingError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            (output, error, exitCode) = (await reading, await readingError, process.ExitCode);
        }
        catch (Exception ex)
        {
            return Failed(ex.Message);
        }

        if (exitCode != 0)
        {
            return Failed(string.Join(" ", (output + error).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        }

        try
        {
            var values = JsonDocument.Parse(output).RootElement.GetProperty("Properties");
            string Get(string property) => values.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";
            return Create(Get("TargetFramework"), Get("TargetFrameworkMoniker"), Get("TargetPlatformIdentifier"), Get("TargetPlatformVersion"))
                is { } evaluated ? new(evaluated, null) : Failed("it names no target framework");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return Failed(output.Trim());
        }

        ProjectEvaluation Failed(string reason)
        {
            var framework = targetFramework is null ? "" : $" for '{targetFramework}'";
            return new(null, $"MSBuild cannot evaluate project '{path}'{framework}, so which framework it targets is not known: {reason}");
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

    // `;` and `,` separate properties on the command line.
    private static string Escape(string value)
    {
        return value.Replace("%", "%25").Replace(";", "%3B").Replace(",", "%2C");
    }
}
