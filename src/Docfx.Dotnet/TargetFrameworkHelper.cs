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
/// framework can be read from. A project that targets a single framework is not given the suffix: its
/// framework is read from the <c>TargetFrameworkAttribute</c> and <c>TargetPlatformAttribute</c> the .NET SDK
/// generates into its compilation, and from the preprocessor symbols the SDK defines only what those leave out.
/// </remarks>
internal static partial class TargetFrameworkHelper
{
    // The symbols the .NET SDK defines for the platform of an OS-specific framework such as net8.0-windows, of the
    // platforms it ships. It defines one for any other platform too, which only the framework asked for tells
    // apart from a project's own symbol.
    private static readonly string[] s_platformSymbols = ["ANDROID", "BROWSER", "IOS", "MACCATALYST", "MACOS", "TIZEN", "TVOS", "WASI", "WINDOWS"];

    // The platforms whose frameworks have no version, `net8.0-browser`: the SDK defines no `_OR_GREATER` symbol for
    // them, so their symbol alone names the platform.
    private static readonly string[] s_versionlessPlatformSymbols = ["BROWSER", "WASI"];

    // The symbols the .NET SDK defines for every framework of a family; a project without any of them
    // (a non-SDK project, or `DisableImplicitFrameworkDefines`) does not say which framework it targets.
    private static readonly string[] s_frameworkFamilySymbols = ["NET", "NETCOREAPP", "NETFRAMEWORK", "NETSTANDARD"];

    // The exact-version symbols only: `NET8_0_OR_GREATER`, `NET` and `NETFRAMEWORK` name no single framework.
    // Case-sensitive, as C# symbols are: a project's own `net8_0` is not the SDK's.
    [GeneratedRegex(@"^(NETSTANDARD\d+_\d+|NETCOREAPP\d+_\d+|NET\d+_\d+|NET\d{2,3})$")]
    private static partial Regex TargetFrameworkSymbolRegex();

    // `WINDOWS10_0_19041_0_OR_GREATER`: the SDK defines one for every platform version up to the one targeted.
    [GeneratedRegex(@"^([A-Z]+)(\d+(?:_\d+)*)_OR_GREATER$")]
    private static partial Regex PlatformVersionSymbolRegex();

    [GeneratedRegex(@"^\.(NETCoreApp|NETStandard|NETFramework),Version=v(\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.IgnoreCase)]
    private static partial Regex FrameworkNameRegex();

    /// <summary>
    /// Returns the project, among those loaded from the same file as <paramref name="project"/>, that
    /// targets <paramref name="targetFramework"/>. Falls back to <paramref name="project"/> when the file
    /// does not target that framework, and reports it, unless <paramref name="report"/> is <see langword="false"/>:
    /// as a warning for a project the entry lists, and as information for one that comes from a solution,
    /// which commonly holds analyzers, tools and test projects that target other frameworks.
    /// </summary>
    /// <param name="beforeCompile">Called with the project to document before it is first compiled here.</param>
    /// <returns>The project to document, and whether it is known to target <paramref name="targetFramework"/>.</returns>
    public static async Task<(Project project, bool matched)> SelectProject(
        Solution solution, Project project, string targetFramework, bool fromSolution = false, bool report = true, Action<Project>? beforeCompile = null)
    {
        var candidates = solution.Projects
            .Where(p => FilePathComparer.OSPlatformSensitiveRelativePathComparer.Equals(p.FilePath, project.FilePath))
            .ToList();

        if (candidates.Count > 1)
        {
            var (selected, matched) = SelectByName();
            beforeCompile?.Invoke(selected);
            return (selected, matched);
        }

        beforeCompile?.Invoke(project);

        // The compilation is the one documented, so reading its attributes builds nothing twice.
        var compilation = await project.GetCompilationAsync();
        var projectFramework = GetTargetFramework(compilation?.Assembly, project.ParseOptions?.PreprocessorSymbolNames ?? [], GetTargetFrameworkPlatform(targetFramework));
        if (projectFramework is null)
        {
            Report($"Project '{project.FilePath}' says neither in a TargetFrameworkAttribute nor in its preprocessor symbols which framework it targets, so whether it targets '{targetFramework}' is not known. It is documented as it is.");
            return (project, false);
        }

        if (MatchesFramework(projectFramework, targetFramework))
        {
            return (project, true);
        }

        Report($"Project '{project.FilePath}' does not target '{targetFramework}', documenting '{projectFramework}' instead.");
        return (project, false);

        (Project, bool) SelectByName()
        {
            // An exact name wins over one that only writes the platform version differently, or that a
            // framework without a platform version selects among several versions of one platform.
            if (candidates.FirstOrDefault(p => string.Equals(GetTargetFrameworkFromName(p), targetFramework, StringComparison.OrdinalIgnoreCase)) is { } exact)
            {
                return (exact, true);
            }

            if (candidates.FirstOrDefault(p => GetFrameworkOfTarget(p, GetTargetFrameworkPlatform(targetFramework)) is { } tfm && MatchesFramework(tfm, targetFramework, GetPlatformVersion(p, tfm))) is { } match)
            {
                return (match, true);
            }

            var available = string.Join(", ", candidates.Select(GetTargetFrameworkFromName).Where(tfm => tfm is not null).Select(tfm => $"'{tfm}'"));
            Report($"Project '{project.FilePath}' does not target '{targetFramework}', documenting '{GetTargetFrameworkFromName(project)}' instead. It targets {available}.");
            return (project, false);
        }

        void Report(string message)
        {
            if (!report)
            {
                return;
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

    /// <summary>
    /// Gets the framework of a project loaded for one of several target frameworks: its name says it, unless
    /// <c>TargetFrameworks</c> lists an alias such as <c>modern</c>, whose framework its symbols then tell.
    /// Its compilation would say it too, but only by building every framework of the project.
    /// </summary>
    private static string? GetFrameworkOfTarget(Project project, string? requestedPlatform)
    {
        var name = GetTargetFrameworkFromName(project);
        return name is null || TargetFrameworkSymbolRegex().IsMatch(GetTargetFrameworkSymbol(name))
            ? name
            : GetTargetFramework(null, project.ParseOptions?.PreprocessorSymbolNames ?? [], requestedPlatform) ?? name;
    }

    /// <summary>
    /// Gets the framework a project that targets a single framework is built for, such as <c>net8.0-windows10.0.19041.0</c>.
    /// The <c>TargetFrameworkAttribute</c> and <c>TargetPlatformAttribute</c> the SDK generates into its compilation
    /// name it exactly; the preprocessor symbols give only what they leave out, as when <c>GenerateAssemblyInfo</c>
    /// is off, which generates no <c>TargetPlatformAttribute</c>, or <c>GenerateTargetFrameworkAttribute</c> is.
    /// </summary>
    /// <param name="requestedPlatform">
    /// The platform of the framework asked for: among the symbols, that of a platform outside the list the SDK
    /// ships is told apart from a project's own symbol, such as <c>SQLITE</c> and <c>SQLITE3</c>, only by being asked for.
    /// </param>
    /// <returns><see langword="null"/> when neither says which framework the project targets.</returns>
    internal static string? GetTargetFramework(IAssemblySymbol? assembly, IEnumerable<string> preprocessorSymbols, string? requestedPlatform = null)
    {
        var fromAttribute = assembly is null ? null : GetTargetFrameworkFromAttribute(assembly);
        if (fromAttribute is not null && GetTargetFrameworkPlatform(fromAttribute) is not null)
        {
            return fromAttribute;
        }

        var symbols = preprocessorSymbols.ToHashSet(StringComparer.Ordinal);
        var framework = fromAttribute ?? GetTargetFrameworkFromSymbols(symbols);
        if (framework is null)
        {
            return null;
        }

        var (platform, version) = GetPlatformFromSymbols(symbols, GetTargetFrameworkSymbol(framework), requestedPlatform);
        return platform is null ? framework : $"{framework}-{platform.ToLowerInvariant()}{FormatVersion(version)}";
    }

    // The framework symbol the SDK defines for a project, without its platform: `NET8_0` is `net8.0`.
    private static string? GetTargetFrameworkFromSymbols(HashSet<string> symbols)
    {
        // A project without the family symbols (a non-SDK project, or `DisableImplicitFrameworkDefines`) does not say.
        if (!s_frameworkFamilySymbols.Any(symbols.Contains))
        {
            return null;
        }

        // A migrated project may keep the symbol of a framework it targeted before, such as `NET45` in a net462
        // project: the SDK's is the highest of the family whose symbol the SDK defines too, as `NETFRAMEWORK`.
        var exact = symbols.Where(s => TargetFrameworkSymbolRegex().IsMatch(s)).ToList();
        var ofDefinedFamily = exact.Where(s => symbols.Contains(GetFamilySymbol(s))).ToList();
        return (ofDefinedFamily.Count > 0 ? ofDefinedFamily : exact)
            .OrderByDescending(GetFrameworkSymbolVersion)
            .ThenBy(s => s, StringComparer.Ordinal)
            .FirstOrDefault() is { } symbol
            ? symbol.ToLowerInvariant().Replace('_', '.')
            : null;

        // `NET462` is a .NET Framework, `NET8_0` a .NET 5 or later, which defines `NET`.
        static string GetFamilySymbol(string symbol) =>
            symbol.StartsWith("NETSTANDARD", StringComparison.Ordinal) ? "NETSTANDARD"
            : symbol.StartsWith("NETCOREAPP", StringComparison.Ordinal) ? "NETCOREAPP"
            : symbol.Contains('_') ? "NET"
            : "NETFRAMEWORK";

        // `NET462` is 4.6.2, `NET8_0` is 8.0.
        static Version? GetFrameworkSymbolVersion(string symbol)
        {
            var digits = new string(symbol.SkipWhile(char.IsLetter).ToArray());
            return ParseVersion(digits.Contains('_') ? digits.Replace('_', '.') : string.Join('.', digits.ToCharArray()));
        }
    }

    // `10.0.19041.0`, and `7.0` for 7.0.0.0, as frameworks write them.
    private static string FormatVersion(Version? version)
    {
        return version is null ? ""
            : version.Build <= 0 && version.Revision <= 0 ? $"{version.Major}.{version.Minor}"
            : version.ToString();
    }

    /// <summary>
    /// Whether a project that targets <paramref name="projectFramework"/> targets <paramref name="targetFramework"/>:
    /// the same framework on the same platform, and the same platform version unless <paramref name="targetFramework"/>
    /// leaves it out. <c>net8.0-windows</c> selects <c>net8.0-windows10.0.19041.0</c>, while
    /// <c>net8.0-windows10.0.22621.0</c> does not, and <c>net8.0</c> does not select <c>net8.0-windows</c>, nor the reverse.
    /// </summary>
    /// <param name="projectPlatformVersion">
    /// The platform version of a project whose framework leaves it out, as <c>net8.0-windows</c> stands for
    /// <c>net8.0-windows7.0</c>.
    /// </param>
    internal static bool MatchesFramework(string projectFramework, string targetFramework, Version? projectPlatformVersion = null)
    {
        return string.Equals(GetTargetFrameworkSymbol(projectFramework), GetTargetFrameworkSymbol(targetFramework), StringComparison.Ordinal)
            && string.Equals(GetTargetFrameworkPlatform(projectFramework), GetTargetFrameworkPlatform(targetFramework), StringComparison.Ordinal)
            && (GetTargetFrameworkPlatformVersion(targetFramework) is not { } version
                || version == (GetTargetFrameworkPlatformVersion(projectFramework) ?? projectPlatformVersion));
    }

    // The platform version the symbols of a project loaded for `framework` give.
    private static Version? GetPlatformVersion(Project project, string framework)
    {
        var symbols = (project.ParseOptions?.PreprocessorSymbolNames ?? []).ToHashSet(StringComparer.Ordinal);
        return GetPlatformFromSymbols(symbols, GetTargetFrameworkSymbol(framework), GetTargetFrameworkPlatform(framework)).Version;
    }

    // `XYZ1_0`: the SDK defines the platform's symbol together with one for the exact version targeted, for any
    // platform, also one the list of platforms does not know.
    [GeneratedRegex(@"^([A-Z]+)(\d+(?:_\d+)*)$")]
    private static partial Regex ExactPlatformVersionSymbolRegex();

    /// <summary>
    /// Gets the platform, and its version, of a project from its preprocessor symbols. The SDK defines one such
    /// as <c>WINDOWS</c> for .NET 5 and later only, together with the one of the exact version targeted,
    /// <c>WINDOWS10_0_19041_0</c>, and a <c>WINDOWS7_0_OR_GREATER</c> for every version it knows up to that one:
    /// a project's own <c>WINDOWS</c> alone is not it, except for a platform whose frameworks have no version,
    /// such as <c>BROWSER</c>. A platform with version symbols wins over such a symbol, which a project can also
    /// define itself for code it shares with that platform. A platform outside the list the SDK ships, such as
    /// <c>net8.0-xyz1.0</c>, has symbols of the same form as a project's own <c>SQLITE</c> and <c>SQLITE3</c>, so
    /// it counts only when <paramref name="requestedPlatform"/> names it.
    /// </summary>
    private static (string? Platform, Version? Version) GetPlatformFromSymbols(HashSet<string> symbols, string frameworkSymbol, string? requestedPlatform = null)
    {
        var underscore = frameworkSymbol.IndexOf('_');
        if (!frameworkSymbol.StartsWith("NET", StringComparison.Ordinal) || underscore < 0 ||
            !int.TryParse(frameworkSymbol.AsSpan(3, underscore - 3), out var major) || major < 5)
        {
            return (null, null);
        }

        var requestedOther = requestedPlatform is not null && !s_platformSymbols.Contains(requestedPlatform) && symbols.Contains(requestedPlatform)
            ? requestedPlatform
            : null;

        string? versionless = null;
        foreach (var platform in s_platformSymbols.Where(symbols.Contains).Concat(requestedOther is null ? [] : [requestedOther]))
        {
            // The exact version is the highest: an SDK whose list stops below it has no `_OR_GREATER` symbol of
            // it, and an older SDK may define no exact one.
            var version = symbols
                .Select(s => PlatformVersionSymbolRegex().Match(s) is { Success: true } m ? m : ExactPlatformVersionSymbolRegex().Match(s))
                .Where(m => m.Success && m.Groups[1].Value == platform)
                .Select(m => ParseVersion(m.Groups[2].Value.Replace('_', '.')))
                .Max();
            if (version is not null)
            {
                // A Windows SDK version ending in `.1` selects CsWinRT 3.0: the SDK gives its symbols the version
                // ending in `.0`, and defines `CSWINRT3_0` for it.
                return platform == "WINDOWS" && version.Revision == 0 && symbols.Contains("CSWINRT3_0")
                    ? (platform, new(version.Major, version.Minor, version.Build, 1))
                    : (platform, version);
            }

            if (versionless is null && s_versionlessPlatformSymbols.Contains(platform))
            {
                versionless = platform;
            }
        }

        // `net8.0-desktop`: without a version the SDK defines the platform's symbol only
        return (requestedOther ?? versionless, null);
    }

    /// <summary>
    /// Reads the framework from the <c>TargetFrameworkAttribute</c> of an assembly, and its platform from the
    /// <c>TargetPlatformAttribute</c>, which the SDK generates for a platform with a version unless
    /// <c>GenerateAssemblyInfo</c> is off: <c>net8.0-windows10.0.19041.0</c>, or <c>net8.0</c> without it.
    /// </summary>
    internal static string? GetTargetFrameworkFromAttribute(IAssemblySymbol assembly)
    {
        string? GetArgument(string attributeName) => assembly.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == attributeName)?
            .ConstructorArguments.FirstOrDefault().Value as string;

        if (GetArgument("System.Runtime.Versioning.TargetFrameworkAttribute") is not { } frameworkName || ParseFrameworkName(frameworkName) is not { } framework)
        {
            return null;
        }

        // `Windows10.0.19041.0`
        return GetArgument("System.Runtime.Versioning.TargetPlatformAttribute") is { Length: > 0 } platform
            ? $"{framework}-{platform.ToLowerInvariant()}"
            : framework;
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
