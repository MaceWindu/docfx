// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
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
internal static partial class TargetFrameworkHelper
{
    private const string AllTargetFrameworks = "*";

    // The exact-version symbols only: `NET8_0_OR_GREATER`, `NET` and `NETFRAMEWORK` name no single framework.
    [GeneratedRegex(@"^(NETSTANDARD\d+_\d+|NETCOREAPP\d+_\d+|NET\d+_\d+|NET\d{2,3})$", RegexOptions.IgnoreCase)]
    private static partial Regex TargetFrameworkSymbolRegex();

    [GeneratedRegex(@"^\.(NETCoreApp|NETStandard|NETFramework),Version=v(\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.IgnoreCase)]
    private static partial Regex FrameworkNameRegex();

    [GeneratedRegex(@"^(netstandard|netcoreapp|net)(\d+(?:\.\d+)*)(?:-([a-z]*))?", RegexOptions.IgnoreCase)]
    private static partial Regex TargetFrameworkMonikerRegex();

    private enum FrameworkFamily { Standard, Core, Framework }

    // The lowest .NET Framework version implementing each .NET Standard version that it implements at all.
    private static readonly (Version Standard, Version Framework)[] StandardOnFramework =
    [
        (new(1, 1), new(4, 5)),
        (new(1, 2), new(4, 5, 1)),
        (new(1, 3), new(4, 6)),
        (new(2, 0), new(4, 6, 1)),
    ];

    /// <summary>
    /// Orders <paramref name="frameworks"/> by how well each one stands in for <paramref name="target"/>:
    /// the target itself, then the frameworks a project targeting it could reference, nearest first, then
    /// the others in the order given.
    /// </summary>
    /// <remarks>
    /// A simplified form of the NuGet compatibility rules, which covers the frameworks a library targets.
    /// NuGet.Frameworks is not used because MSBuildWorkspace loads the copy that comes with the SDK.
    /// </remarks>
    internal static List<string> OrderByNearest(string target, IEnumerable<string> frameworks)
    {
        var parsedTarget = ParseMoniker(target);
        return frameworks
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select((framework, index) => (framework, index, parsed: ParseMoniker(framework)))
            .OrderBy(f => string.Equals(f.framework, target, StringComparison.OrdinalIgnoreCase) ? 0
                : parsedTarget is not { } t || f.parsed is not { } p || !CanReference(t, p) ? 3
                : p.Family == t.Family ? 1
                : 2)
            .ThenByDescending(f => f.parsed is { } p && parsedTarget is { } t && CanReference(t, p) ? p.Version : null)
            .ThenBy(f => f.index)
            .Select(f => f.framework)
            .ToList();
    }

    private static (FrameworkFamily Family, Version Version, string? Platform)? ParseMoniker(string moniker)
    {
        var match = TargetFrameworkMonikerRegex().Match(moniker);
        if (!match.Success)
        {
            return null;
        }

        var digits = match.Groups[2].Value;
        var platform = match.Groups[3].Success ? match.Groups[3].Value.ToLowerInvariant() : null;
        return match.Groups[1].Value.ToLowerInvariant() switch
        {
            "netstandard" when Version.TryParse(digits, out var version) => (FrameworkFamily.Standard, version, platform),
            "netcoreapp" when Version.TryParse(digits, out var version) => (FrameworkFamily.Core, version, platform),
            "net" when digits.Contains('.') && Version.TryParse(digits, out var version) => (FrameworkFamily.Core, version, platform),
            // `net462` is .NET Framework 4.6.2, one digit per part.
            "net" when !digits.Contains('.') && digits.Length is 2 or 3 => (FrameworkFamily.Framework, Version.Parse(string.Join('.', digits.ToCharArray())), platform),
            _ => null,
        };
    }

    private static bool CanReference((FrameworkFamily Family, Version Version, string? Platform) target, (FrameworkFamily Family, Version Version, string? Platform) framework)
    {
        if (framework.Platform is not null && framework.Platform != target.Platform)
        {
            return false;
        }

        return (target.Family, framework.Family) switch
        {
            _ when target.Family == framework.Family => framework.Version <= target.Version,
            (FrameworkFamily.Core, FrameworkFamily.Standard) => framework.Version <= (target.Version >= new Version(3, 0) ? new Version(2, 1) : target.Version >= new Version(2, 0) ? new Version(2, 0) : new Version(1, 6)),
            (FrameworkFamily.Framework, FrameworkFamily.Standard) => StandardOnFramework.Any(s => framework.Version <= s.Standard && target.Version >= s.Framework),
            _ => false,
        };
    }

    /// <summary>
    /// Returns the projects, among those loaded from the same file as <paramref name="project"/>, for each
    /// of <paramref name="targetFrameworks"/> in that order, with the framework each one targets.
    /// </summary>
    /// <returns>
    /// The projects found, which is none when the file targets none of the frameworks: unlike a single
    /// framework, a list states what to include, so nothing else is documented in place of a missing one.
    /// A framework is <see langword="null"/> when it can only be read from the compilation.
    /// </returns>
    public static List<(Project Project, string? Framework)> SelectProjects(Solution solution, Project project, IReadOnlyCollection<string> targetFrameworks)
    {
        var candidates = GetProjectsFromSameFile(solution, project);

        if (targetFrameworks.Contains(AllTargetFrameworks))
        {
            // The project the workspace opened goes first, so that the framework a project is documented
            // at by default stays the one that provides the content.
            return candidates
                .OrderBy(p => p.Id == project.Id ? 0 : 1)
                .Select(p => (p, GetTargetFrameworkFromName(p) ?? GetTargetFrameworkFromSymbols(p.ParseOptions?.PreprocessorSymbolNames ?? [])))
                .ToList();
        }

        var selected = new List<(Project, string?)>();
        var missing = new List<string>();
        foreach (var targetFramework in targetFrameworks)
        {
            if (candidates.FirstOrDefault(p => Targets(p, targetFramework)) is { } match)
            {
                selected.Add((match, GetTargetFrameworkFromName(match) ?? targetFramework));
            }
            else
            {
                missing.Add(targetFramework);
            }
        }

        if (missing.Count > 0)
        {
            Logger.LogWarning(
                $"Project '{project.FilePath}' does not target {string.Join(", ", missing.Select(tfm => $"'{tfm}'"))}, so it is not documented for that.",
                code: WarningCodes.Metadata.TargetFrameworkNotFound);
        }

        return selected;
    }

    private static List<Project> GetProjectsFromSameFile(Solution solution, Project project)
    {
        return solution.Projects
            .Where(p => FilePathComparer.OSPlatformSensitiveRelativePathComparer.Equals(p.FilePath, project.FilePath))
            .ToList();
    }

    private static bool Targets(Project project, string targetFramework)
    {
        return GetTargetFrameworkFromName(project) is { } name
            ? string.Equals(name, targetFramework, StringComparison.OrdinalIgnoreCase)
            : DefinesTargetFrameworkSymbol(project, targetFramework);
    }

    /// <summary>
    /// Reads the framework back from the preprocessor symbol the .NET SDK defines for it.
    /// </summary>
    internal static string? GetTargetFrameworkFromSymbols(IEnumerable<string> preprocessorSymbols)
    {
        return preprocessorSymbols.FirstOrDefault(s => TargetFrameworkSymbolRegex().IsMatch(s))?.ToLowerInvariant().Replace('_', '.');
    }

    /// <summary>
    /// Reads the framework from the <c>TargetFrameworkAttribute</c> of an assembly, for a project whose
    /// preprocessor symbols do not name it.
    /// </summary>
    internal static string? GetTargetFrameworkFromAttribute(IAssemblySymbol assembly)
    {
        var attribute = assembly.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "System.Runtime.Versioning.TargetFrameworkAttribute");
        return attribute?.ConstructorArguments.FirstOrDefault().Value is string frameworkName ? ParseFrameworkName(frameworkName) : null;
    }

    /// <summary>
    /// Converts a framework name such as <c>.NETCoreApp,Version=v8.0</c> to its moniker, <c>net8.0</c>.
    /// </summary>
    internal static string? ParseFrameworkName(string frameworkName)
    {
        var match = FrameworkNameRegex().Match(frameworkName);
        if (!match.Success)
        {
            return null;
        }

        var (major, minor) = (match.Groups[2].Value, match.Groups[3].Value);
        return match.Groups[1].Value.ToUpperInvariant() switch
        {
            "NETSTANDARD" => $"netstandard{major}.{minor}",
            "NETCOREAPP" => int.Parse(major) >= 5 ? $"net{major}.{minor}" : $"netcoreapp{major}.{minor}",
            _ => $"net{major}{minor}{match.Groups[4].Value}",
        };
    }

    /// <summary>
    /// Returns the project, among those loaded from the same file as <paramref name="project"/>, that
    /// targets <paramref name="targetFramework"/>. Falls back to <paramref name="project"/>, with a
    /// warning, when the file does not target that framework.
    /// </summary>
    public static Project SelectProject(Solution solution, Project project, string targetFramework)
    {
        var candidates = GetProjectsFromSameFile(solution, project);

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
