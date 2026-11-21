// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using AwesomeAssertions;
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
    public void IsValid(string targetFramework, bool expected)
    {
        TargetFrameworkHelper.IsValid(targetFramework).Should().Be(expected);
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
            return Task.FromResult(tfm == "net8.0-windows" ? new ProjectFramework("net8.0-windows", "net8.0-windows7.0") : null);
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
        var frameworks = new ProjectFrameworks((path, tfm) => Task.FromResult<ProjectFramework>(tfm switch
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
        var frameworks = new ProjectFrameworks((path, tfm) => Task.FromResult(new ProjectFramework("net8.0-windows", "net8.0-windows7.0")));
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
            return Task.FromResult(new ProjectFramework("net48", "net48"));
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
            return Task.FromResult(new ProjectFramework("net8.0", "net8.0"));
        });

        await frameworks.Evaluate(LibPath, "net8.0");
        await frameworks.Evaluate(LibPath, "net8.0");
        await frameworks.Evaluate(LibPath, null);

        count.Should().Be(2);
    }

    [Theory]
    [InlineData("""{ "targetFrameworks": "*" }""", new[] { "*" })]
    [InlineData("""{ "targetFrameworks": [ "net8.0", "net462" ] }""", new[] { "net8.0", "net462" })]
    public void TargetFrameworksIsReadFromAStringOrAnArray(string json, string[] expected)
    {
        Newtonsoft.Json.JsonConvert.DeserializeObject<MetadataJsonItemConfig>(json).TargetFrameworks.Should().Equal(expected);
        System.Text.Json.JsonSerializer.Deserialize<MetadataJsonItemConfig>(json).TargetFrameworks.Should().Equal(expected);
    }

    [Theory]
    [InlineData("net8.0-windows", "net8.0-windows")]
    // an alias in `TargetFrameworks` is named for the framework it stands for
    [InlineData("modern", "net8.0")]
    public async Task GetFrameworkLabelOfASingleTargetProject(string loaded, string expected)
    {
        var (_, projects) = CreateProjects("Lib");
        var frameworks = new ProjectFrameworks((path, tfm) => Task.FromResult(new ProjectFramework(tfm, tfm == "modern" ? "net8.0" : "net8.0-windows7.0")));
        frameworks.AddLoaded(LibPath, loaded);

        (await TargetFrameworkHelper.GetFrameworkLabel(projects[0], frameworks)).Should().Be(expected);
    }

    [Theory]
    [InlineData("Lib(net8.0-windows)", "net8.0-windows")]
    [InlineData("Lib(modern)", "net8.0")]
    public async Task GetFrameworkLabelOfAMultiTargetedProject(string name, string expected)
    {
        var (_, projects) = CreateProjects(name);
        var frameworks = new ProjectFrameworks((path, tfm) => Task.FromResult(tfm == "modern" ? new ProjectFramework("modern", "net8.0") : null));

        (await TargetFrameworkHelper.GetFrameworkLabel(projects[0], frameworks)).Should().Be(expected);
    }

    [Fact]
    public async Task GetFrameworkLabelOfAProjectMSBuildCannotEvaluateIsNone()
    {
        var (_, projects) = CreateProjects("Lib");

        (await TargetFrameworkHelper.GetFrameworkLabel(projects[0], NotEvaluated())).Should().BeNull();
    }

    [Theory]
    // `net8.0-windows` that MSBuild evaluates to `net8.0-windows7.0` is the other project's `net8.0-windows7.0`
    [InlineData("net8.0-windows", "net8.0-windows7.0", "net8.0-windows7.0", "net8.0-windows7.0")]
    [InlineData("net8.0-windows", "net8.0-windows10.0.19041.0", "net8.0-windows7.0", "net8.0-windows")]
    public async Task WithPlatformVersionOfMergesAFrameworkNamedWithItsVersion(string framework, string named, string evaluated, string expected)
    {
        var frameworks = new ProjectFrameworks((path, tfm) => Task.FromResult(new ProjectFramework(tfm, evaluated)));

        (await TargetFrameworkHelper.WithPlatformVersionOf(framework, LibPath, [framework, named], frameworks)).Should().Be(expected);
    }

    [Fact]
    public async Task WithPlatformVersionOfEvaluatesOnlyWhenAnotherProjectNamesAVersion()
    {
        var evaluated = false;
        var frameworks = new ProjectFrameworks((path, tfm) =>
        {
            evaluated = true;
            return Task.FromResult(new ProjectFramework(tfm, "net8.0-windows7.0"));
        });

        (await TargetFrameworkHelper.WithPlatformVersionOf("net8.0-windows", LibPath, ["net8.0-windows", "net8.0"], frameworks)).Should().Be("net8.0-windows");
        evaluated.Should().BeFalse();
    }

    [Theory]
    [InlineData("net8.0", "net8.0")]
    // named as the project names it
    [InlineData("net8.0-windows7.0", "net8.0-windows")]
    [InlineData("net9.0", null)]
    public async Task SelectProjectsOfASingleTargetProjectReadsTheFrameworkTheWorkspaceLoaded(string targetFramework, string expected)
    {
        var (solution, projects) = CreateProjects("Lib");
        var frameworks = new ProjectFrameworks((path, tfm) => Task.FromResult(new ProjectFramework(tfm, tfm == "net8.0-windows" ? "net8.0-windows7.0" : tfm)));
        frameworks.AddLoaded(LibPath, targetFramework.StartsWith("net8.0-windows") ? "net8.0-windows" : "net8.0");

        var selected = await TargetFrameworkHelper.SelectProjects(solution, projects[0], [targetFramework], frameworks);

        selected.Select(s => s.Framework).Should().Equal(expected is null ? Array.Empty<string>() : new[] { expected });
    }

    [Fact]
    public async Task SelectProjectsOfAProjectMSBuildCannotEvaluateNamesNoFramework()
    {
        var (solution, projects) = CreateProjects("Lib");

        var selected = await TargetFrameworkHelper.SelectProjects(solution, projects[0], ["net8.0"], NotEvaluated());

        selected.Should().ContainSingle().Which.Framework.Should().BeNull();
    }

    [Fact]
    public void AppliesToIsGroupedByValueInTheOrderOfTheAxis()
    {
        var axis = TargetFrameworkAxis.Create(
            [new() { Name = "EF Core", Values = [new() { Id = "ef3", Label = "EF Core 3.1", Files = ["a"] }, new() { Id = "ef9", Files = ["b"] }] }],
            [Directory.GetCurrentDirectory()]);

        var groups = YamlViewModelExtensions.ToAppliesToViewModel(["ef9:net8.0", "ef3:netstandard2.0", "ef3:net462"], axis);

        groups.Select(g => g.Version).Should().Equal("ef3", "ef9");
        groups.Select(g => g.Label).Should().Equal("EF Core 3.1", "ef9");
        groups[0].Frameworks.Should().Equal("netstandard2.0", "net462");
    }

    [Fact]
    public void AProjectGetsTheFirstValueThatMatchesItWhicheverSrcFolderItIsMatchedFrom()
    {
        // `src/Ef3/Lib.csproj` is `Ef3/Lib.csproj` from the first folder and `Lib.csproj` from the second.
        var root = Path.Combine(Directory.GetCurrentDirectory(), "src");
        var axis = TargetFrameworkAxis.Create(
            [new() { Name = "EF Core", Values = [new() { Id = "ef3", Files = ["Lib.csproj"] }, new() { Id = "ef9", Files = ["Ef3/**"] }] }],
            [root, Path.Combine(root, "Ef3")]);

        axis.Match(Path.Combine(root, "Ef3", "Lib.csproj")).Should().Be("ef3");
    }

    [Fact]
    public void AppliesToWithoutAnAxisIsOneGroup()
    {
        var groups = YamlViewModelExtensions.ToAppliesToViewModel(["net8.0", "net462"], null);

        groups.Should().ContainSingle().Which.Frameworks.Should().Equal("net8.0", "net462");
        groups[0].Version.Should().BeNull();
    }

    [Theory]
    [InlineData("ef9:net8.0", "ef9", "net8.0")]
    [InlineData("net8.0", null, "net8.0")]
    public void SplitUnit(string unit, string value, string framework)
    {
        TargetFrameworkAxis.SplitUnit(unit).Should().Be((value, framework));
    }

    [Theory]
    [InlineData("net10.0", "net462 netstandard2.0 net8.0 net10.0", "net10.0 net8.0 netstandard2.0 net462")]
    [InlineData("net10.0", "net462 netstandard2.0", "netstandard2.0 net462")]
    [InlineData("net48", "netstandard2.0 net8.0 net462", "net462 netstandard2.0 net8.0")]
    [InlineData("net8.0", "netstandard2.0 netstandard2.1 netcoreapp3.1", "netcoreapp3.1 netstandard2.1 netstandard2.0")]
    [InlineData("net472", "netstandard2.1 netstandard2.0 netstandard1.3", "netstandard2.0 netstandard1.3 netstandard2.1")]
    [InlineData("net45", "netstandard1.3 netstandard1.1", "netstandard1.1 netstandard1.3")]
    [InlineData("netcoreapp2.1", "netstandard2.1 netstandard2.0", "netstandard2.0 netstandard2.1")]
    [InlineData("net8.0", "net8.0-windows netstandard2.0", "netstandard2.0 net8.0-windows")]
    [InlineData("net8.0-windows", "net8.0 net8.0-windows", "net8.0-windows net8.0")]
    [InlineData("net8.0-windows", "net8.0 net8.0-windows10.0.19041.0", "net8.0-windows10.0.19041.0 net8.0")]
    [InlineData("net8.0-windows7.0", "net8.0 net8.0-windows", "net8.0-windows net8.0")]
    [InlineData("NET8.0", "net9.0 net8.0", "net8.0 net9.0")]
    [InlineData("unknown", "net8.0 netstandard2.0", "net8.0 netstandard2.0")]
    public void OrderByNearest(string target, string frameworks, string expected)
    {
        TargetFrameworkHelper.OrderByNearest(target, frameworks.Split(' ')).Should().Equal(expected.Split(' '));
    }

    [Theory]
    [InlineData("ef 9")]
    [InlineData("ef\t9")]
    [InlineData("ef:9")]
    public void AnAxisValueIdWithASpaceOrColonIsInvalid(string id)
    {
        using var listener = new Docfx.Tests.Common.TestListenerScope();

        var axis = TargetFrameworkAxis.Create([new() { Name = "EF", Values = [new() { Id = id, Files = ["**"] }] }], ["."]);

        axis.Should().BeNull();
        listener.GetItemsByLogLevel(Docfx.Common.LogLevel.Error).Should().ContainSingle(x => x.Code == Docfx.Common.ErrorCodes.Metadata.InvalidAxis);
    }

    [Fact]
    public void AnAxisWithANullValueOrFileIsInvalid()
    {
        using var listener = new Docfx.Tests.Common.TestListenerScope();

        // what `"values": [null]` and `"files": [null]` read as; the schema forbids both, docfx does not check it
        TargetFrameworkAxis.Create([new() { Name = "EF", Values = [null] }], ["."]).Should().BeNull();
        TargetFrameworkAxis.Create([new() { Name = "EF", Values = [new() { Id = "ef9", Files = [null] }] }], ["."]).Should().BeNull();
        TargetFrameworkAxis.Create([null], ["."]).Should().BeNull();

        listener.GetItemsByLogLevel(Docfx.Common.LogLevel.Error).Should().HaveCount(3).And.OnlyContain(x => x.Code == Docfx.Common.ErrorCodes.Metadata.InvalidAxis);
    }

    [Theory]
    [InlineData("net8.0-windows", "net8.0-windows10.0.19041.0", true)]
    [InlineData("net8.0-windows10.0.19041.0", "net8.0-windows", true)]
    [InlineData("NET8.0", "net8.0", true)]
    [InlineData("net8.0-windows10.0.19041.0", "net8.0-windows10.0.22621.0", false)]
    [InlineData("net8.0", "net8.0-windows", false)]
    public void IsSameFramework(string left, string right, bool expected)
    {
        TargetFrameworkHelper.IsSameFramework(left, right).Should().Be(expected);
    }

    [Theory]
    [InlineData("net10.0", "net8.0", true)]
    [InlineData("net10.0", "netstandard2.0", true)]
    [InlineData("net472", "net462", true)]
    [InlineData("net462", "netstandard2.0", true)]
    [InlineData("NET8.0", "net8.0", true)]
    [InlineData("net8.0-windows", "net8.0", true)]
    [InlineData("net8.0", "net10.0", false)]
    [InlineData("net462", "net8.0", false)]
    [InlineData("net462", "netstandard2.1", false)]
    [InlineData("net8.0", "net8.0-windows", false)]
    [InlineData("unknown", "net8.0", false)]
    public void CanStandIn(string target, string framework, bool expected)
    {
        TargetFrameworkHelper.CanStandIn(target, framework).Should().Be(expected);
    }

    private static ProjectFrameworks NotEvaluated() => new((path, tfm) => Task.FromResult<ProjectFramework>(null));

    private static (Solution, List<Project>) CreateProjects(params string[] names)
    {
        var workspace = new AdhocWorkspace();
        var projects = names
            .Select(name => workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, name, "Lib", LanguageNames.CSharp, filePath: LibPath)))
            .ToList();
        return (workspace.CurrentSolution, projects);
    }
}
