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
/// <c>Foo(net8.0)</c>. What MSBuild says beyond that comes from <see cref="ProjectFrameworks"/>.
/// </remarks>
internal static partial class TargetFrameworkHelper
{
    // `net8.0` is `NET8_0`, `net462` is `NET462`: the frameworks whose names say which framework they are.
    [GeneratedRegex(@"^(NETSTANDARD\d+_\d+|NETCOREAPP\d+_\d+|NET\d+_\d+|NET\d{2,3})$")]
    private static partial Regex TargetFrameworkSymbolRegex();

    [GeneratedRegex(@"^\.(NETCoreApp|NETStandard|NETFramework),Version=v(\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.IgnoreCase)]
    private static partial Regex FrameworkNameRegex();

    /// <summary>
    /// Returns the project, among those loaded from the same file as <paramref name="project"/>, that
    /// targets <paramref name="targetFramework"/>. Falls back to <paramref name="project"/> when the file
    /// does not target that framework, and reports it, unless <paramref name="report"/> is <see langword="false"/>:
    /// as a warning for a project the entry lists, and as information for one that comes from a solution,
    /// which commonly holds analyzers, tools and test projects that target other frameworks.
    /// </summary>
    /// <returns>The project to document, and whether it is known to target <paramref name="targetFramework"/>.</returns>
    public static async Task<(Project project, bool matched)> SelectProject(
        Solution solution, Project project, string targetFramework, ProjectFrameworks frameworks, bool fromSolution = false, bool report = true)
    {
        var candidates = solution.Projects
            .Where(p => FilePathComparer.OSPlatformSensitiveRelativePathComparer.Equals(p.FilePath, project.FilePath))
            .ToList();

        // Why MSBuild cannot say what a framework stands for, reported in the message of the project it leaves
        // unmatched: one project, one warning.
        var failures = new List<string>();

        if (candidates.Count <= 1)
        {
            var (name, failure) = await frameworks.GetName(project);
            if (name is null)
            {
                if (failure is not null)
                {
                    failures.Add(failure);
                }

                Report($"Project '{project.FilePath}' is not known to target '{targetFramework}', documenting the framework it is loaded for.");
                return (project, false);
            }

            if (await Targets(project, name))
            {
                return (project, true);
            }

            Report($"Project '{project.FilePath}' does not target '{targetFramework}', documenting '{name}' instead.");
            return (project, false);
        }

        // An exact name wins over one that only writes the platform version differently, or that a
        // framework without a platform version selects among several versions of one platform.
        if (candidates.FirstOrDefault(p => string.Equals(GetTargetFrameworkFromName(p), targetFramework, StringComparison.OrdinalIgnoreCase)) is { } exact)
        {
            return (exact, true);
        }

        foreach (var candidate in candidates)
        {
            if (GetTargetFrameworkFromName(candidate) is { } name && await Targets(candidate, name))
            {
                return (candidate, true);
            }
        }

        var available = string.Join(", ", candidates.Select(GetTargetFrameworkFromName).Where(tfm => tfm is not null).Select(tfm => $"'{tfm}'"));
        Report($"Project '{project.FilePath}' does not target '{targetFramework}', documenting '{GetTargetFrameworkFromName(project)}' instead. It targets {available}.");
        return (project, false);

        async Task<bool> Targets(Project candidate, string name)
        {
            if (string.Equals(name, targetFramework, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // An alias does not say which framework it stands for, nor `net8.0-windows` the platform version it
            // does: MSBuild does, when the framework and platform it names are those requested.
            if (IsFrameworkName(name))
            {
                var leavesOutVersion = GetTargetFrameworkPlatform(name) is not null && GetTargetFrameworkPlatformVersion(name) is null
                    && GetTargetFrameworkPlatformVersion(targetFramework) is not null;
                if (!leavesOutVersion || !IsSameFrameworkAndPlatform(name, targetFramework))
                {
                    return MatchesFramework(name, targetFramework);
                }
            }

            if (candidate.FilePath is null)
            {
                return false;
            }

            var evaluation = await frameworks.Evaluate(candidate.FilePath, name);
            if (evaluation.Failure is { } failure)
            {
                failures.Add(failure);
            }

            return evaluation.Framework is { } framework && MatchesFramework(framework.Moniker, targetFramework);
        }

        void Report(string message)
        {
            if (!report)
            {
                return;
            }

            if (failures.Count > 0)
            {
                message = $"{message} {string.Join(" ", failures.Select(failure => $"{failure.TrimEnd('.')}."))}";
            }

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
    /// Says why <paramref name="targetFramework"/> cannot name a single framework: a list such as
    /// <c>net8.0;netstandard2.0</c>, a dash without a platform, as in <c>net8.0-</c>, or a platform version that is
    /// not a version, such as <c>net8.0-windows10.0.19041.O</c>.
    /// </summary>
    /// <returns><see langword="null"/> when it can.</returns>
    internal static string? GetInvalidReason(string targetFramework)
    {
        if (targetFramework.IndexOfAny([';', ',']) >= 0)
        {
            return "a list of frameworks, while it selects a single one";
        }

        var dash = targetFramework.IndexOf('-');
        if (dash < 0)
        {
            return null;
        }

        var platform = targetFramework[(dash + 1)..];
        var digits = new string(platform.SkipWhile(char.IsLetter).ToArray());
        if (digits.Length == platform.Length)
        {
            return "a framework with a dash but no platform after it: write the platform after the dash, as 'net8.0-windows' does, or leave out the dash";
        }

        return digits.Length == 0 || ParseVersion(digits) is not null
            ? null
            : $"a framework whose platform version '{digits}' is not a version: write it in digits and dots, as 'net8.0-windows10.0.19041.0' does";
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
        return start < 0 ? null : name[(start + 1)..^1].Trim();
    }

    // A framework rather than an alias, which `TargetFrameworks` can list too.
    private static bool IsFrameworkName(string name) => TargetFrameworkSymbolRegex().IsMatch(GetTargetFrameworkSymbol(name));

    // .NET 5 and later, the frameworks with platforms.
    internal static bool IsNet5OrLater(string framework)
    {
        return GetTargetFrameworkSymbol(framework) is var symbol && symbol.StartsWith("NET", StringComparison.Ordinal)
            && symbol.IndexOf('_') is > 3 and var underscore && int.TryParse(symbol.AsSpan(3, underscore - 3), out var major) && major >= 5;
    }

    /// <summary>
    /// Whether a project that targets <paramref name="projectFramework"/> targets <paramref name="targetFramework"/>:
    /// the same framework on the same platform, and the same platform version unless <paramref name="targetFramework"/>
    /// leaves it out. <c>net8.0-windows</c> selects <c>net8.0-windows10.0.19041.0</c>, while
    /// <c>net8.0-windows10.0.22621.0</c> does not, and <c>net8.0</c> does not select <c>net8.0-windows</c>, nor the reverse.
    /// </summary>
    internal static bool MatchesFramework(string projectFramework, string targetFramework)
    {
        return IsSameFrameworkAndPlatform(projectFramework, targetFramework)
            && (GetTargetFrameworkPlatformVersion(targetFramework) is not { } version
                || version == GetTargetFrameworkPlatformVersion(projectFramework));
    }

    private static bool IsSameFrameworkAndPlatform(string a, string b)
    {
        return string.Equals(GetTargetFrameworkSymbol(a), GetTargetFrameworkSymbol(b), StringComparison.Ordinal)
            && string.Equals(GetTargetFrameworkPlatform(a), GetTargetFrameworkPlatform(b), StringComparison.Ordinal);
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
    /// Gets the platform version of an OS-specific target framework, with the parts it leaves out as zero:
    /// <c>10.0.19041.0</c> for both <c>net8.0-windows10.0.19041.0</c> and <c>net8.0-windows10.0.19041</c>,
    /// <see langword="null"/> for <c>net8.0-windows</c> and <c>net8.0</c>.
    /// </summary>
    internal static Version? GetTargetFrameworkPlatformVersion(string targetFramework)
    {
        var dash = targetFramework.IndexOf('-');
        if (dash < 0)
        {
            return null;
        }

        var digits = new string(targetFramework[(dash + 1)..].SkipWhile(char.IsLetter).ToArray());
        return ParseVersion(digits);
    }

    private static Version? ParseVersion(string digits)
    {
        // Version needs at least two parts, a platform version may give one: `android34`.
        return Version.TryParse(digits.Contains('.') ? digits : $"{digits}.0", out var version)
            ? new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0))
            : null;
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
}
