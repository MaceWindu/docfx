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
    internal const string AllTargetFrameworks = "*";

    // `net8.0` is `NET8_0`, `net462` is `NET462`: the frameworks whose names say which framework they are.
    [GeneratedRegex(@"^(NETSTANDARD\d+_\d+|NETCOREAPP\d+_\d+|NET\d+_\d+|NET\d{2,3})$")]
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
            .OrderBy(f => IsSameFramework(f.framework, target) ? 0
                : parsedTarget is not { } t || f.parsed is not { } p || !CanReference(t, p) ? 3
                : p.Family == t.Family ? 1
                : 2)
            .ThenByDescending(f => f.parsed is { } p && parsedTarget is { } t && CanReference(t, p) ? p.Version : null)
            .ThenBy(f => f.index)
            .Select(f => f.framework)
            .ToList();
    }

    /// <summary>
    /// Whether two framework names name the same framework, either way round with the platform version one
    /// leaves out: <c>net8.0-windows</c> is the project loaded as <c>net8.0-windows10.0.19041.0</c>, and
    /// <c>net8.0-windows7.0</c> the one loaded as <c>net8.0-windows</c>.
    /// </summary>
    internal static bool IsSameFramework(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase) || MatchesFramework(left, right) || MatchesFramework(right, left);
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
    /// A framework is <see langword="null"/> for a project MSBuild cannot evaluate, which is reported.
    /// </returns>
    /// <param name="report">
    /// Whether to report why MSBuild cannot evaluate a project, which a project <c>src</c> lists does by itself
    /// when a listed solution holds it too.
    /// </param>
    public static async Task<List<(Project Project, string? Framework)>> SelectProjects(
        Solution solution, Project project, IReadOnlyCollection<string> targetFrameworks, ProjectFrameworks frameworks, bool fromSolution = false, bool report = true)
    {
        var candidates = GetProjectsFromSameFile(solution, project);

        // Why MSBuild cannot say what a framework stands for, reported as a framework the project lacks is.
        var failures = new List<string>();

        if (targetFrameworks.Contains(AllTargetFrameworks))
        {
            // The project the workspace opened goes first, so that the framework a project is documented
            // at by default comes first among its own. Which framework provides the content is decided per
            // entry, by the order the frameworks first appear across its projects, so with several projects
            // that list their frameworks in different orders, `defaultFramework` is what chooses it.
            var all = new List<(Project, string?)>();
            foreach (var candidate in candidates.OrderBy(p => p.Id == project.Id ? 0 : 1))
            {
                all.Add((candidate, await GetFrameworkLabel(candidate, frameworks, failures)));
            }

            ReportFailures();
            return all;
        }

        var names = new Dictionary<ProjectId, string?>();
        foreach (var candidate in candidates)
        {
            var (name, failure) = await frameworks.GetName(candidate);
            names[candidate.Id] = name;
            if (failure is not null)
            {
                failures.Add(failure);
            }
        }

        if (candidates.Count <= 1 && names[project.Id] is null)
        {
            // which framework to say it applies to is not known
            ReportFailures();
            return [(project, null)];
        }

        var selected = new List<(Project, string?)>();
        var missing = new List<string>();
        foreach (var targetFramework in targetFrameworks)
        {
            var available = candidates.Where(p => !selected.Any(s => s.Item1.Id == p.Id) && names[p.Id] is not null).ToList();
            // An exact name wins over one that differs only in the platform version, as with `targetFramework`.
            var match = available.FirstOrDefault(p => string.Equals(names[p.Id], targetFramework, StringComparison.OrdinalIgnoreCase));
            foreach (var candidate in available)
            {
                if (match is not null)
                {
                    break;
                }

                if (await TargetsFramework(candidate, names[candidate.Id]!, targetFramework, frameworks, failures))
                {
                    match = candidate;
                }
            }

            if (match is not null)
            {
                // named as the list names it, unless the project's name is a framework rather than an alias
                selected.Add((match, names[match.Id] is { } name && IsFrameworkName(name) ? name : targetFramework));
            }
            else
            {
                missing.Add(targetFramework);
            }
        }

        if (missing.Count > 0)
        {
            // as with `targetFramework`, the projects of a solution commonly target other frameworks
            ReportFailures();
            ReportNotFound(
                $"Project '{project.FilePath}' does not target {string.Join(", ", missing.Select(tfm => $"'{tfm}'"))}, so it is not documented for that.",
                fromSolution);
        }

        return selected;

        void ReportFailures()
        {
            if (report)
            {
                failures.ForEach(failure => frameworks.ReportFailure(failure, fromSolution));
            }
        }
    }

    /// <summary>
    /// Reports a framework a project lacks, or that MSBuild cannot say whether it has one: as a warning for a project
    /// the entry lists, and as information for one that comes from a solution, which commonly holds analyzers, tools
    /// and test projects that target other frameworks.
    /// </summary>
    internal static void ReportNotFound(string message, bool fromSolution)
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

    private static List<Project> GetProjectsFromSameFile(Solution solution, Project project)
    {
        return solution.Projects
            .Where(p => FilePathComparer.OSPlatformSensitiveRelativePathComparer.Equals(p.FilePath, project.FilePath))
            .ToList();
    }

    /// <summary>
    /// Names the framework of a project as <c>targetFrameworks: "*"</c> lists it: as the project names it, or, for
    /// an alias in <c>TargetFrameworks</c>, as the framework it stands for.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> for a project MSBuild cannot evaluate. Why it cannot, or cannot say what an alias stands
    /// for, which then names the project, is added to <paramref name="failures"/>, for the caller to report.
    /// </returns>
    internal static async Task<string?> GetFrameworkLabel(Project project, ProjectFrameworks frameworks, List<string> failures)
    {
        var (name, failure) = await frameworks.GetName(project);
        if (name is null || IsFrameworkName(name) || project.FilePath is not { } path)
        {
            if (failure is not null)
            {
                failures.Add(failure);
            }

            return name;
        }

        var evaluation = await frameworks.Evaluate(path, name);
        if (evaluation.Failure is not null)
        {
            failures.Add(evaluation.Failure);
        }

        return evaluation.Framework?.Moniker ?? name;
    }

    /// <summary>
    /// Names a framework with a platform version as the first of <paramref name="frameworks"/> that gives the same
    /// version, however it writes it: <c>net8.0-windows7</c> and <c>net8.0-windows7.0</c> are one framework.
    /// </summary>
    internal static string AsNamedFirst(string framework, IEnumerable<string> frameworks)
    {
        return GetTargetFrameworkPlatformVersion(framework) is { } version
            ? frameworks.FirstOrDefault(other => MatchesFramework(other, framework) && GetTargetFrameworkPlatformVersion(other) == version) ?? framework
            : framework;
    }

    /// <summary>
    /// Names a framework that leaves out its platform version, <c>net8.0-windows</c>, as the one of
    /// <paramref name="frameworks"/> that gives the version MSBuild evaluates it to, so that both are one framework:
    /// <c>net8.0-windows7.0</c>, if another project is named that.
    /// </summary>
    internal static async Task<string> WithPlatformVersionOf(string framework, string projectPath, IEnumerable<string> frameworks, ProjectFrameworks projectFrameworks)
    {
        if (GetTargetFrameworkPlatform(framework) is null || GetTargetFrameworkPlatformVersion(framework) is not null)
        {
            return framework;
        }

        // only a framework another project names with a version is worth evaluating the project for
        var versioned = frameworks.Where(other => GetTargetFrameworkPlatformVersion(other) is not null && MatchesFramework(other, framework)).ToList();
        if (versioned.Count == 0)
        {
            return framework;
        }

        // A project documented under a name another one gives too is one framework named twice on its pages: worth
        // a warning whichever input the project comes from.
        var evaluation = await projectFrameworks.Evaluate(projectPath, framework);
        if (evaluation.Framework is not { } evaluated)
        {
            if (evaluation.Failure is not null)
            {
                projectFrameworks.ReportFailure(evaluation.Failure, fromSolution: false);
            }

            return framework;
        }

        return versioned.FirstOrDefault(other => GetTargetFrameworkPlatformVersion(other) == GetTargetFrameworkPlatformVersion(evaluated.Moniker)) ?? framework;
    }

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
        var candidates = GetProjectsFromSameFile(solution, project);

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

                ReportFailures();
                return (project, false);
            }

            if (await TargetsFramework(project, name, targetFramework, frameworks, failures))
            {
                return (project, true);
            }

            ReportFailures();
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
            if (GetTargetFrameworkFromName(candidate) is { } name && await TargetsFramework(candidate, name, targetFramework, frameworks, failures))
            {
                return (candidate, true);
            }
        }

        var available = string.Join(", ", candidates.Select(GetTargetFrameworkFromName).Where(tfm => tfm is not null).Select(tfm => $"'{tfm}'"));
        ReportFailures();
        Report($"Project '{project.FilePath}' does not target '{targetFramework}', documenting '{GetTargetFrameworkFromName(project)}' instead. It targets {available}.");
        return (project, false);

        void Report(string message)
        {
            if (report)
            {
                ReportNotFound(message, fromSolution);
            }
        }

        void ReportFailures()
        {
            if (report)
            {
                failures.ForEach(failure => frameworks.ReportFailure(failure, fromSolution));
            }
        }
    }

    // Whether a project MSBuild loaded for the framework it names `name` targets `targetFramework`; why MSBuild cannot
    // say goes to `failures`.
    private static async Task<bool> TargetsFramework(Project project, string name, string targetFramework, ProjectFrameworks frameworks, List<string> failures)
    {
        if (string.Equals(name, targetFramework, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // An alias does not say which framework it stands for, nor `net8.0-windows` the platform version it
        // does: MSBuild does.
        var leavesOutVersion = GetTargetFrameworkPlatform(name) is not null && GetTargetFrameworkPlatformVersion(name) is null
            && GetTargetFrameworkPlatformVersion(targetFramework) is not null;
        if (IsFrameworkName(name) && !leavesOutVersion)
        {
            return MatchesFramework(name, targetFramework);
        }

        if (project.FilePath is null)
        {
            return false;
        }

        var evaluation = await frameworks.Evaluate(project.FilePath, name);
        if (evaluation.Failure is { } failure)
        {
            failures.Add(failure);
        }

        return evaluation.Framework is { } framework && MatchesFramework(framework.Moniker, targetFramework);
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

        // An alias, which may hold a dash and digits as any name may, is selected by itself.
        var dash = targetFramework.IndexOf('-');
        if (dash < 0 || !IsFrameworkName(targetFramework))
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
