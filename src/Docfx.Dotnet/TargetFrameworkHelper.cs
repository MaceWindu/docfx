// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Docfx.Common;
using Microsoft.CodeAnalysis;

#nullable enable

namespace Docfx.Dotnet;

/// <summary>
/// Picks the target framework of a project to document.
/// </summary>
/// <remarks>
/// <see cref="Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace"/> loads every target framework of a
/// multi-targeted project as a separate project, and names each one after its framework, e.g.
/// <c>Foo(net8.0)</c>. Roslyn exposes no MSBuild properties, so that name is the only place the
/// framework can be read from. A project that targets a single framework is not given the suffix,
/// and its framework is recognized by the preprocessor symbol the .NET SDK defines for it instead.
/// </remarks>
internal static class TargetFrameworkHelper
{
    // The symbols the .NET SDK defines for the platform of an OS-specific framework such as net8.0-windows.
    private static readonly string[] s_platformSymbols = ["ANDROID", "BROWSER", "IOS", "MACCATALYST", "MACOS", "TIZEN", "TVOS", "WASI", "WINDOWS"];

    // The symbols the .NET SDK defines for every framework of a family; a project without any of them
    // (a non-SDK project, or `DisableImplicitFrameworkDefines`) does not say which framework it targets.
    private static readonly string[] s_frameworkFamilySymbols = ["NET", "NETCOREAPP", "NETFRAMEWORK", "NETSTANDARD"];

    /// <summary>
    /// Returns the project, among those loaded from the same file as <paramref name="project"/>, that
    /// targets <paramref name="targetFramework"/>. Falls back to <paramref name="project"/> when the file
    /// does not target that framework, and reports it: as a warning for a project the entry lists, and
    /// as information for one that comes from a solution, which commonly holds analyzers, tools and test
    /// projects that target other frameworks.
    /// </summary>
    /// <returns>The project to document, and whether it targets <paramref name="targetFramework"/>.</returns>
    public static (Project project, bool matched) SelectProject(Solution solution, Project project, string targetFramework, bool fromSolution = false)
    {
        var candidates = solution.Projects
            .Where(p => FilePathComparer.OSPlatformSensitiveRelativePathComparer.Equals(p.FilePath, project.FilePath))
            .ToList();

        if (candidates.Count > 1)
        {
            if (candidates.FirstOrDefault(p => string.Equals(GetTargetFrameworkFromName(p), targetFramework, StringComparison.OrdinalIgnoreCase)) is { } match)
            {
                return (match, true);
            }

            var available = string.Join(", ", candidates.Select(GetTargetFrameworkFromName).Where(tfm => tfm is not null).Select(tfm => $"'{tfm}'"));
            Report($"Project '{project.FilePath}' does not target '{targetFramework}', documenting '{GetTargetFrameworkFromName(project)}' instead. It targets {available}.");
            return (project, false);
        }

        switch (TargetsFramework(project.ParseOptions?.PreprocessorSymbolNames ?? [], targetFramework))
        {
            case true:
                return (project, true);
            case false:
                Report($"Project '{project.FilePath}' does not target '{targetFramework}', documenting the target framework it has instead.");
                return (project, false);
            default:
                Logger.LogInfo($"Project '{project.FilePath}' defines no target framework symbols, so whether it targets '{targetFramework}' is not known. It is documented as it is.");
                return (project, true);
        }

        void Report(string message)
        {
            if (fromSolution)
            {
                Logger.LogInfo(message);
            }
            else
            {
                Logger.LogWarning(message, code: WarningCodes.Metadata.TargetFrameworkNotFound);
            }
        }
    }

    /// <summary>
    /// Reads the framework from the <c>(tfm)</c> suffix Roslyn appends to the name of a project
    /// loaded for one of several target frameworks.
    /// </summary>
    internal static string? GetTargetFrameworkFromName(Project project)
    {
        var name = project.Name;
        if (!name.EndsWith(')'))
        {
            return null;
        }

        var start = name.LastIndexOf('(');
        return start < 0 ? null : name[(start + 1)..^1];
    }

    /// <summary>
    /// Gets the preprocessor symbol the .NET SDK defines for a target framework, which is the framework
    /// in upper case with dots replaced by underscores, without its platform: <c>net8.0</c> and
    /// <c>net8.0-windows</c> define <c>NET8_0</c>, <c>netstandard2.0</c> defines <c>NETSTANDARD2_0</c>.
    /// </summary>
    internal static string GetTargetFrameworkSymbol(string targetFramework)
    {
        var dash = targetFramework.IndexOf('-');
        var framework = dash < 0 ? targetFramework : targetFramework[..dash];
        return framework.ToUpperInvariant().Replace('.', '_');
    }

    /// <summary>
    /// Gets the platform of an OS-specific target framework in upper case, without its version:
    /// <c>WINDOWS</c> for <c>net8.0-windows10.0.19041.0</c>, <see langword="null"/> for <c>net8.0</c>.
    /// </summary>
    internal static string? GetTargetFrameworkPlatform(string targetFramework)
    {
        var dash = targetFramework.IndexOf('-');
        if (dash < 0)
        {
            return null;
        }

        var platform = new string(targetFramework[(dash + 1)..].TakeWhile(char.IsLetter).ToArray());
        return platform.Length == 0 ? null : platform.ToUpperInvariant();
    }

    /// <summary>
    /// Tells from the preprocessor symbols of a project that targets a single framework whether that
    /// framework is <paramref name="targetFramework"/>, platform included, the way a multi-targeted project
    /// is matched by name: <c>net8.0</c> does not match a <c>net8.0-windows</c> project, nor the reverse.
    /// </summary>
    /// <returns><see langword="null"/> when the symbols do not say which framework the project targets.</returns>
    internal static bool? TargetsFramework(IEnumerable<string> preprocessorSymbols, string targetFramework)
    {
        var symbols = preprocessorSymbols.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!s_frameworkFamilySymbols.Any(symbols.Contains))
        {
            return null;
        }

        if (!symbols.Contains(GetTargetFrameworkSymbol(targetFramework)))
        {
            return false;
        }

        var projectPlatform = s_platformSymbols.FirstOrDefault(symbols.Contains);
        return string.Equals(projectPlatform, GetTargetFrameworkPlatform(targetFramework), StringComparison.OrdinalIgnoreCase);
    }
}
