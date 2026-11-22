// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using AwesomeAssertions;
using Docfx.Common;
using Docfx.Tests.Common;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Docfx.Dotnet.Tests;

// SelectProject logs through the global logger, which other tests of the collection listen to.
[Collection("docfx STA")]
public class TargetFrameworkHelperTest
{
    private const string LibPath = "/src/Lib/Lib.csproj";

    [Theory]
    [InlineData("net8.0", "NET8_0")]
    [InlineData("net10.0", "NET10_0")]
    [InlineData("net8.0-windows", "NET8_0")]
    [InlineData("net8.0-windows10.0.19041.0", "NET8_0")]
    [InlineData("netstandard2.0", "NETSTANDARD2_0")]
    [InlineData("netcoreapp3.1", "NETCOREAPP3_1")]
    [InlineData("net462", "NET462")]
    public void GetTargetFrameworkSymbol(string targetFramework, string expected)
    {
        TargetFrameworkHelper.GetTargetFrameworkSymbol(targetFramework).Should().Be(expected);
    }

    [Theory]
    [InlineData("net8.0", null)]
    [InlineData("net8.0-windows", "WINDOWS")]
    [InlineData("net8.0-windows10.0.19041.0", "WINDOWS")]
    [InlineData("net9.0-maccatalyst18.0", "MACCATALYST")]
    [InlineData("netstandard2.0", null)]
    public void GetTargetFrameworkPlatform(string targetFramework, string expected)
    {
        TargetFrameworkHelper.GetTargetFrameworkPlatform(targetFramework).Should().Be(expected);
    }

    [Theory]
    [InlineData("net8.0", "net8.0", true)]
    [InlineData("net8.0", "NET8.0", true)]
    [InlineData("net8.0", "net9.0", false)]
    [InlineData("net8.0", "net8.0-windows", false)]
    [InlineData("net8.0-windows", "net8.0", false)]
    [InlineData("net8.0-windows10.0.19041.0", "net8.0-windows", true)]
    [InlineData("net8.0-windows10.0.19041.0", "net8.0-windows10.0.19041", true)]
    [InlineData("net8.0-windows10.0.17763.0", "net8.0-windows10.0.19041.0", false)]
    [InlineData("net8.0-windows", "net8.0-windows10.0.19041.0", false)]
    [InlineData("net8.0-windows7.0", "net8.0-windows7.0", true)]
    [InlineData("net10.0-windows10.0.26100.1", "net10.0-windows10.0.26100.0", false)]
    [InlineData("net8.0-android34.0", "net8.0-android34", true)]
    [InlineData("net8.0-windows", "net8.0-android", false)]
    [InlineData("net8.0-browser1.0", "net8.0-browser", true)]
    [InlineData("net8.0", "net", false)]
    public void MatchesFramework(string projectFramework, string targetFramework, bool expected)
    {
        TargetFrameworkHelper.MatchesFramework(projectFramework, targetFramework).Should().Be(expected);
    }

    [Theory]
    [InlineData("net8.0", null)]
    [InlineData("net8.0-windows", null)]
    [InlineData("net8.0-windows10.0.19041", "10.0.19041.0")]
    [InlineData("net8.0-windows10.0.19041.0", "10.0.19041.0")]
    [InlineData("net8.0-android34", "34.0.0.0")]
    public void GetTargetFrameworkPlatformVersion(string targetFramework, string expected)
    {
        TargetFrameworkHelper.GetTargetFrameworkPlatformVersion(targetFramework).Should().Be(expected is null ? null : Version.Parse(expected));
    }

    [Theory]
    [InlineData("net8.0", true)]
    [InlineData("net8.0-windows", true)]
    [InlineData("net8.0-windows10.0.19041.0", true)]
    [InlineData("net8.0-android34", true)]
    [InlineData("modern", true)]
    [InlineData("net", true)]
    // a platform version that is not one would otherwise match every version of its platform
    [InlineData("net8.0-windows10.0.19041.O", false)]
    [InlineData("net8.0-windows10.0.19041.0.1", false)]
    [InlineData("net8.0-windows10..0", false)]
    // a list, or a dash without a platform, cannot be a single framework either
    [InlineData("net8.0;netstandard2.0", false)]
    [InlineData("net8.0,netstandard2.0", false)]
    [InlineData("net8.0-", false)]
    [InlineData("net8.0-10.0", false)]
    public void GetInvalidReason(string targetFramework, bool expectValid)
    {
        (TargetFrameworkHelper.GetInvalidReason(targetFramework) is null).Should().Be(expectValid);
    }

    [Fact]
    public void GetInvalidReasonQuotesThePlatformVersionThatIsNotOne()
    {
        TargetFrameworkHelper.GetInvalidReason("net8.0-windows10.0.19041.O").Should().Contain("'10.0.19041.O'");
    }

    [Theory]
    [InlineData("Lib(net8.0)", "net8.0")]
    [InlineData("Lib(netstandard2.0)", "netstandard2.0")]
    [InlineData("My(Lib)(net8.0)", "net8.0")]
    [InlineData("Lib( netstandard2.0 )", "netstandard2.0")]
    [InlineData("Lib", null)]
    public void GetTargetFrameworkFromName(string projectName, string expected)
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(projectName, LanguageNames.CSharp);

        TargetFrameworkHelper.GetTargetFrameworkFromName(project).Should().Be(expected);
    }

    [Theory]
    [InlineData(".NETCoreApp,Version=v8.0", "net8.0")]
    [InlineData(".NETCoreApp,Version=v3.1", "netcoreapp3.1")]
    [InlineData(".NETStandard,Version=v2.0", "netstandard2.0")]
    [InlineData(".NETFramework,Version=v4.6.2", "net462")]
    [InlineData(".NETFramework,Version=v4.8", "net48")]
    [InlineData("Silverlight,Version=v5.0", null)]
    public void ParseFrameworkName(string frameworkName, string expected)
    {
        TargetFrameworkHelper.ParseFrameworkName(frameworkName).Should().Be(expected);
    }

    [Theory]
    // what `dotnet msbuild -getProperty` gives for these projects
    [InlineData("net8.0-windows10.0.19041.0", ".NETCoreApp,Version=v8.0", "Windows", "10.0.19041.0", "net8.0-windows10.0.19041.0", "net8.0-windows10.0.19041.0")]
    [InlineData("net8.0-windows", ".NETCoreApp,Version=v8.0", "Windows", "7.0", "net8.0-windows", "net8.0-windows7.0")]
    [InlineData("net8.0-browser", ".NETCoreApp,Version=v8.0", "browser", "1.0", "net8.0-browser", "net8.0-browser1.0")]
    [InlineData("net8.0", ".NETCoreApp,Version=v8.0", "", "", "net8.0", "net8.0")]
    // the SDK sets a platform for .NET Framework, which its name does not give
    [InlineData("net462", ".NETFramework,Version=v4.6.2", "Windows", "7.0", "net462", "net462")]
    [InlineData("netstandard2.0", ".NETStandard,Version=v2.0", "", "", "netstandard2.0", "netstandard2.0")]
    // an alias, and a project without `TargetFramework`, as a non-SDK project is
    [InlineData("modern", ".NETCoreApp,Version=v8.0", "", "", "modern", "net8.0")]
    [InlineData("", ".NETFramework,Version=v4.8", "", "", "net48", "net48")]
    public void CreateProjectFramework(string targetFramework, string moniker, string platform, string platformVersion, string expectedName, string expectedMoniker)
    {
        ProjectFrameworks.Create(targetFramework, moniker, platform, platformVersion).Should().Be(new ProjectFramework(expectedName, expectedMoniker));
    }

    [Fact]
    public void CreateProjectFrameworkOfAProjectThatNamesNone()
    {
        ProjectFrameworks.Create("", "", "", "").Should().BeNull();
    }

    [Theory]
    [InlineData("net8.0", "Lib(net8.0)")]
    [InlineData("NET8.0", "Lib(net8.0)")]
    [InlineData("NetStandard2.0", "Lib(netstandard2.0)")]
    [InlineData("net8.0-windows", "Lib(net8.0-windows10.0.17763.0)")]
    [InlineData("net8.0-windows10.0.19041.0", "Lib(net8.0-windows10.0.19041.0)")]
    [InlineData("net8.0-windows10.0.19041", "Lib(net8.0-windows10.0.19041.0)")]
    public async Task SelectProjectOfAMultiTargetedProject(string targetFramework, string expected)
    {
        var (solution, projects) = CreateProjects("Lib(net8.0)", "Lib(netstandard2.0)", "Lib(net8.0-windows10.0.17763.0)", "Lib(net8.0-windows10.0.19041.0)");

        var (selected, matched) = await TargetFrameworkHelper.SelectProject(solution, projects[0], targetFramework, NotEvaluated());

        selected.Name.Should().Be(expected);
        matched.Should().BeTrue();
    }

    [Fact]
    public async Task SelectProjectOfAMultiTargetedProjectPrefersTheExactName()
    {
        // `net8.0-windows` also selects `net8.0-windows10.0.17763.0`, which comes first.
        var (solution, projects) = CreateProjects("Lib(net8.0-windows10.0.17763.0)", "Lib(net8.0-windows)");

        var (selected, matched) = await TargetFrameworkHelper.SelectProject(solution, projects[0], "net8.0-windows", NotEvaluated());

        selected.Name.Should().Be("Lib(net8.0-windows)");
        matched.Should().BeTrue();
    }

    [Theory]
    [InlineData("net8.0-windows7.0", true)]
    [InlineData("net8.0-windows10.0.19041.0", false)]
    public async Task SelectProjectAsksMSBuildThePlatformVersionANameLeavesOut(string targetFramework, bool expectMatch)
    {
        var (solution, projects) = CreateProjects("Lib(net8.0)", "Lib(net8.0-windows)");
        var evaluated = new List<string>();
        var frameworks = new ProjectFrameworks((path, tfm) =>
        {
            evaluated.Add(tfm);
            return Evaluated(tfm == "net8.0-windows" ? new ProjectFramework("net8.0-windows", "net8.0-windows7.0") : null);
        });

        var (selected, matched) = await TargetFrameworkHelper.SelectProject(solution, projects[0], targetFramework, frameworks);

        matched.Should().Be(expectMatch);
        selected.Should().BeSameAs(expectMatch ? projects[1] : projects[0]);
        // a name with the platform version says it, and a name without a platform has none to leave out
        evaluated.Should().Equal("net8.0-windows");
    }

    [Theory]
    [InlineData("net8.0", "Lib(modern)")]
    [InlineData("netstandard2.0", "Lib(legacy)")]
    // the alias itself names it as well
    [InlineData("legacy", "Lib(legacy)")]
    public async Task SelectProjectOfAMultiTargetedProjectWithAliasesAsksMSBuild(string targetFramework, string expected)
    {
        // `<TargetFrameworks>modern;legacy</TargetFrameworks>`, with `TargetFrameworkIdentifier` and version set per alias
        var (solution, projects) = CreateProjects("Lib(modern)", "Lib(legacy)");
        var frameworks = new ProjectFrameworks((path, tfm) => Evaluated(tfm switch
        {
            "modern" => new("modern", "net8.0"),
            "legacy" => new("legacy", "netstandard2.0"),
            _ => null,
        }));

        var (selected, matched) = await TargetFrameworkHelper.SelectProject(solution, projects[0], targetFramework, frameworks);

        selected.Name.Should().Be(expected);
        matched.Should().BeTrue();
    }

    [Theory]
    [InlineData("net9.0")]
    [InlineData("net8.0-android")]
    // a platform version the project does not target is not one it does
    [InlineData("net8.0-windows10.0.22621.0")]
    [InlineData("net")]
    public async Task SelectProjectFallsBackToTheProjectGiven(string targetFramework)
    {
        var (solution, projects) = CreateProjects("Lib(net8.0)", "Lib(net8.0-windows10.0.17763.0)", "Lib(net8.0-windows10.0.19041.0)");

        var (selected, matched) = await TargetFrameworkHelper.SelectProject(solution, projects[0], targetFramework, NotEvaluated());

        selected.Should().BeSameAs(projects[0]);
        matched.Should().BeFalse();
    }

    [Theory]
    [InlineData("net8.0-windows10.0.19041.0", "net8.0-windows10.0.19041.0", true)]
    [InlineData("net8.0-windows10.0.19041.0", "net8.0-windows", true)]
    [InlineData("net8.0-windows10.0.19041.0", "net8.0", false)]
    [InlineData("net8.0", "NET8.0", true)]
    [InlineData("net8.0", "netstandard2.0", false)]
    // a project's own symbols, such as `SQLITE;SQLITE3`, are not read
    [InlineData("net8.0", "net8.0-sqlite3.0", false)]
    public async Task SelectProjectOfASingleTargetProjectReadsTheFrameworkTheWorkspaceLoaded(string loaded, string targetFramework, bool expectMatch)
    {
        var (solution, projects) = CreateProjects("Lib");
        var frameworks = NotEvaluated();
        frameworks.AddLoaded(LibPath, loaded);

        var (selected, matched) = await TargetFrameworkHelper.SelectProject(solution, projects[0], targetFramework, frameworks);

        selected.Should().BeSameAs(projects[0]);
        matched.Should().Be(expectMatch);
    }

    [Theory]
    [InlineData("net8.0-windows7.0", true)]
    [InlineData("net8.0-windows10.0.19041.0", false)]
    public async Task SelectProjectOfASingleTargetProjectAsksMSBuildThePlatformVersionItLeavesOut(string targetFramework, bool expectMatch)
    {
        var (solution, projects) = CreateProjects("Lib");
        var frameworks = new ProjectFrameworks((path, tfm) => Evaluated(new ProjectFramework("net8.0-windows", "net8.0-windows7.0")));
        frameworks.AddLoaded(LibPath, "net8.0-windows");

        var (_, matched) = await TargetFrameworkHelper.SelectProject(solution, projects[0], targetFramework, frameworks);

        matched.Should().Be(expectMatch);
    }

    [Fact]
    public async Task SelectProjectOfASingleTargetProjectTheWorkspaceNamesNoFrameworkForEvaluatesIt()
    {
        // a non-SDK project, for which the workspace reports no framework
        var (solution, projects) = CreateProjects("Lib");
        string[] evaluated = null;
        var frameworks = new ProjectFrameworks((path, tfm) =>
        {
            evaluated = [path, tfm];
            return Evaluated(new ProjectFramework("net48", "net48"));
        });

        var (_, matched) = await TargetFrameworkHelper.SelectProject(solution, projects[0], "net48", frameworks);

        matched.Should().BeTrue();
        evaluated.Should().Equal(LibPath, null);
    }

    [Fact]
    public async Task SelectProjectOfAProjectMSBuildCannotEvaluateIsNotAMatch()
    {
        var (solution, projects) = CreateProjects("Lib");

        var (selected, matched) = await TargetFrameworkHelper.SelectProject(solution, projects[0], "net8.0", NotEvaluated());

        selected.Should().BeSameAs(projects[0]);
        matched.Should().BeFalse();
    }

    [Fact]
    public async Task ProjectFrameworksEvaluatesAProjectOnce()
    {
        var count = 0;
        var frameworks = new ProjectFrameworks((path, tfm) =>
        {
            count++;
            return Evaluated(new ProjectFramework("net8.0", "net8.0"));
        });

        await frameworks.Evaluate(LibPath, "net8.0");
        await frameworks.Evaluate(LibPath, "net8.0");
        await frameworks.Evaluate(LibPath, null);

        count.Should().Be(2);
    }

    [Fact]
    public async Task SelectProjectOfASingleTargetProjectWithParenthesesInItsNameReadsTheFrameworkTheWorkspaceLoaded()
    {
        // `Lib(Legacy).csproj`: the workspace adds a framework to the name of a project with several only
        var (solution, projects) = CreateProjects("Lib(Legacy)");
        var evaluated = false;
        var frameworks = new ProjectFrameworks((path, tfm) =>
        {
            evaluated = true;
            return Evaluated(null);
        });
        frameworks.AddLoaded(LibPath, "net8.0");

        var (_, matched) = await TargetFrameworkHelper.SelectProject(solution, projects[0], "net8.0", frameworks);

        matched.Should().BeTrue();
        evaluated.Should().BeFalse();
    }

    [Fact]
    public async Task SelectProjectOfANonSdkProjectWithParenthesesInItsNameEvaluatesItWithoutAFramework()
    {
        // a non-SDK `Lib(Legacy).csproj`, for which the workspace reports no framework: `Legacy` is no framework of it
        using var listener = new TestListenerScope(LogLevel.Info);
        var (solution, projects) = CreateProjects("Lib(Legacy)");
        var evaluated = new List<string>();
        var frameworks = new ProjectFrameworks((path, tfm) =>
        {
            evaluated.Add(tfm);
            return Evaluated(new ProjectFramework("net48", "net48"));
        });

        var (_, matched) = await TargetFrameworkHelper.SelectProject(solution, projects[0], "net472", frameworks);

        matched.Should().BeFalse();
        evaluated.Should().ContainSingle().Which.Should().BeNull();
        listener.Items.Should().ContainSingle(i => i.Message.Contains("documenting 'net48' instead"));
    }

    [Fact]
    public async Task ProjectFrameworksStopsAnEvaluationThatDoesNotAnswer()
    {
        var start = OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo("cmd", "/c ping -n 60 127.0.0.1")
            : new System.Diagnostics.ProcessStartInfo("sleep", "60");
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = await ProjectFrameworks.Run(start, TimeSpan.FromSeconds(1));

        result.Should().BeNull();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task ProjectFrameworksNamesAProjectAfterItsFrameworkWhenItsFileIsLoadedForSeveral()
    {
        var (_, projects) = CreateProjects("Lib(net8.0)", "Lib(netstandard2.0)");
        var frameworks = NotEvaluated();
        frameworks.AddLoaded(LibPath, "net8.0");
        frameworks.AddLoaded(LibPath, "netstandard2.0");

        var (name, failure) = await frameworks.GetName(projects[1]);

        name.Should().Be("netstandard2.0");
        failure.Should().BeNull();
    }

    [Theory]
    [InlineData(false, true, LogLevel.Warning)]
    [InlineData(true, true, LogLevel.Info)]
    [InlineData(true, false, null)]
    public async Task SelectProjectReportsASingleTargetProjectMSBuildCannotEvaluateWithTheFrameworkRequested(bool fromSolution, bool report, LogLevel? expected)
    {
        // a non-SDK project, for which the workspace reports no framework, and whose evaluation fails
        using var listener = new TestListenerScope(LogLevel.Info);
        var (solution, projects) = CreateProjects("Lib");

        var (selected, matched) = await TargetFrameworkHelper.SelectProject(solution, projects[0], "net8.0", NotEvaluated(), fromSolution, report);

        selected.Should().BeSameAs(projects[0]);
        matched.Should().BeFalse();
        if (expected is { } level)
        {
            var item = listener.Items.Should().ContainSingle().Which;
            item.LogLevel.Should().Be(level);
            item.Message.Should().StartWith($"Project '{LibPath}' is not known to target 'net8.0', documenting the framework it is loaded for.")
                .And.Contain($"MSBuild cannot evaluate project '{LibPath}' for '': test.");
        }
        else
        {
            listener.Items.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task SelectProjectOfAProjectLoadedForSeveralFrameworksReadsTheFrameworkFromItsName()
    {
        // the other frameworks of the file failed to load, so the solution holds one project of it
        var (solution, projects) = CreateProjects("Lib(net8.0)");
        var frameworks = NotEvaluated();
        frameworks.AddLoaded(LibPath, "net8.0");
        frameworks.AddLoaded(LibPath, "netstandard2.0");

        var (_, matched) = await TargetFrameworkHelper.SelectProject(solution, projects[0], "net8.0", frameworks);

        matched.Should().BeTrue();
    }

    [Theory]
    [InlineData(false, true, LogLevel.Warning)]
    // a solution's projects commonly target other frameworks, so that MSBuild cannot evaluate one is no warning either
    [InlineData(true, true, LogLevel.Info)]
    // a project a listed solution holds too is reported once, as the project `src` lists
    [InlineData(true, false, null)]
    public async Task SelectProjectReportsAProjectMSBuildCannotEvaluateAsItReportsAFrameworkItLacks(bool fromSolution, bool report, LogLevel? expected)
    {
        using var listener = new TestListenerScope(LogLevel.Info);
        var (solution, projects) = CreateProjects("Lib(modern)", "Lib(legacy)");

        await TargetFrameworkHelper.SelectProject(solution, projects[0], "net8.0", NotEvaluated(), fromSolution, report);

        if (expected is { } level)
        {
            // one message for the project, which says why each alias says nothing without MSBuild
            var item = listener.Items.Should().ContainSingle().Which;
            item.LogLevel.Should().Be(level);
            item.Message.Should().StartWith($"Project '{LibPath}' does not target 'net8.0'")
                .And.Contain($"MSBuild cannot evaluate project '{LibPath}' for 'modern': test.")
                .And.Contain($"MSBuild cannot evaluate project '{LibPath}' for 'legacy': test.");
        }
        else
        {
            listener.Items.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task SelectProjectEvaluatesNoFrameworkThatCannotMatch()
    {
        // neither family is net10.0's, so the platform version they leave out does not matter
        using var listener = new TestListenerScope(LogLevel.Info);
        var (solution, projects) = CreateProjects("Lib(net8.0-windows)", "Lib(net9.0-windows)");
        var evaluated = new List<string>();
        var frameworks = new ProjectFrameworks((path, tfm) =>
        {
            evaluated.Add(tfm);
            return Evaluated(null);
        });

        var (_, matched) = await TargetFrameworkHelper.SelectProject(solution, projects[0], "net10.0-windows10.0.19041.0", frameworks);

        matched.Should().BeFalse();
        evaluated.Should().BeEmpty();
        listener.Items.Should().ContainSingle().Which.Message.Should().NotContain("MSBuild cannot evaluate");
    }

    [Fact]
    public async Task ProjectFrameworksEvaluatesAProjectOnceWhateverTheCaseOfTheFramework()
    {
        var count = 0;
        var frameworks = new ProjectFrameworks((path, tfm) =>
        {
            count++;
            return Evaluated(new ProjectFramework("net8.0", "net8.0"));
        });

        await frameworks.Evaluate(LibPath, "net8.0-windows");
        await frameworks.Evaluate(LibPath, "NET8.0-Windows");

        count.Should().Be(1);
    }

    [Fact]
    public async Task SelectProjectReportsNoEvaluationFailureOfAFrameworkItDoesNotSelect()
    {
        using var listener = new TestListenerScope(LogLevel.Info);
        var (solution, projects) = CreateProjects("Lib(legacy)", "Lib(modern)");
        var frameworks = new ProjectFrameworks((path, tfm) => Evaluated(tfm == "modern" ? new ProjectFramework("modern", "net8.0") : null));

        var (selected, matched) = await TargetFrameworkHelper.SelectProject(solution, projects[0], "net8.0", frameworks);

        matched.Should().BeTrue();
        selected.Should().BeSameAs(projects[1]);
        listener.Items.Should().BeEmpty();
    }

    [Theory]
    [InlineData("net8.O")]
    [InlineData("net")]
    public async Task SelectProjectEvaluatesNoAliasForAValueThatNamesNoFramework(string targetFramework)
    {
        // what an alias stands for is a framework, which such a value never is
        using var listener = new TestListenerScope(LogLevel.Info);
        var (solution, projects) = CreateProjects("Lib(modern)", "Lib(legacy)");
        var evaluated = new List<string>();
        var frameworks = new ProjectFrameworks((path, tfm) =>
        {
            evaluated.Add(tfm);
            return Evaluated(new ProjectFramework(tfm, "net8.0"));
        });

        var (selected, matched) = await TargetFrameworkHelper.SelectProject(solution, projects[0], targetFramework, frameworks);

        matched.Should().BeFalse();
        selected.Should().BeSameAs(projects[0]);
        evaluated.Should().BeEmpty();
    }

    [Theory]
    [InlineData("{\n  \"Properties\": {\n    \"TargetFramework\": \"net8.0\"\n  }\n}\n")]
    // a notice of a first run, or a warning that quotes a `{`, before it, and text after it
    [InlineData("Welcome to .NET!\r\nwarning MSB4011: 'C:\\{x}\\a.props' cannot be imported again.\r\n{\r\n  \"Properties\": {\r\n    \"TargetFramework\": \"net8.0\"\r\n  }\r\n}\r\nBuild succeeded.\r\n")]
    public void ProjectFrameworksReadsTheJsonOfTheEvaluationWhateverIsAroundIt(string output)
    {
        using var document = System.Text.Json.JsonDocument.Parse(ProjectFrameworks.ReadJson(output));

        document.RootElement.GetProperty("Properties").GetProperty("TargetFramework").GetString().Should().Be("net8.0");
    }

    private static Task<ProjectEvaluation> Evaluated(ProjectFramework framework) =>
        Task.FromResult(new ProjectEvaluation(framework, framework is null ? "MSBuild cannot evaluate project 'Lib.csproj': test" : null));

    // a failure per project and framework, as `dotnet msbuild` gives
    private static ProjectFrameworks NotEvaluated() =>
        new((path, tfm) => Task.FromResult(new ProjectEvaluation(null, $"MSBuild cannot evaluate project '{path}' for '{tfm}': test")));

    private static (Solution, List<Project>) CreateProjects(params string[] names)
    {
        var workspace = new AdhocWorkspace();
        var projects = names
            .Select(name => workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, name, "Lib", LanguageNames.CSharp, filePath: LibPath)))
            .ToList();
        return (workspace.CurrentSolution, projects);
    }
}
