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
    /// <summary>
    /// Returns the project, among those loaded from the same file as <paramref name="project"/>, that
    /// targets <paramref name="targetFramework"/>. Falls back to <paramref name="project"/>, with a
    /// warning, when the file does not target that framework.
    /// </summary>
    public static Project SelectProject(Solution solution, Project project, string targetFramework)
    {
        var candidates = solution.Projects
            .Where(p => FilePathComparer.OSPlatformSensitiveRelativePathComparer.Equals(p.FilePath, project.FilePath))
            .ToList();

        if (candidates.Count > 1)
        {
            if (candidates.FirstOrDefault(p => string.Equals(GetTargetFrameworkFromName(p), targetFramework, StringComparison.OrdinalIgnoreCase)) is { } match)
            {
                return match;
            }

            var available = string.Join(", ", candidates.Select(GetTargetFrameworkFromName).Where(tfm => tfm is not null).Select(tfm => $"'{tfm}'"));
            Logger.LogWarning(
                $"Project '{project.FilePath}' does not target '{targetFramework}', documenting '{GetTargetFrameworkFromName(project)}' instead. It targets {available}.",
                code: WarningCodes.Metadata.TargetFrameworkNotFound);
            return project;
        }

        if (!DefinesTargetFrameworkSymbol(project, targetFramework))
        {
            Logger.LogWarning(
                $"Project '{project.FilePath}' does not target '{targetFramework}', documenting the target framework it has instead.",
                code: WarningCodes.Metadata.TargetFrameworkNotFound);
        }

        return project;
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

    private static bool DefinesTargetFrameworkSymbol(Project project, string targetFramework)
    {
        var symbol = GetTargetFrameworkSymbol(targetFramework);
        return project.ParseOptions?.PreprocessorSymbolNames.Contains(symbol, StringComparer.OrdinalIgnoreCase) ?? false;
    }
}
