// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Docfx.Common;
using Docfx.DataContracts.ManagedReference;
using Docfx.Dotnet;
using Docfx.Tests.Common;

namespace Docfx.Tests;

/// <summary>
/// End to end coverage of the <c>targetFramework</c> metadata option over real multi-targeted projects.
/// </summary>
[Collection("docfx STA")]
public class TargetFrameworkTest : TestBase
{
    private const string ReferenceWithoutMetadataWarning = "Found project reference without a matching metadata reference";
    private const string UnresolvedReferenceRemovedWarning = "Unresolved metadata reference removed from project";

    private readonly string _outputFolder;
    private readonly string _projectFolder;

    public TargetFrameworkTest()
    {
        _outputFolder = GetRandomFolder();
        _projectFolder = GetRandomFolder();

        // Keep the projects independent of the repository's own build settings, including its central
        // package management, so that only warnings caused by docfx are logged.
        File.WriteAllText(Path.Combine(_projectFolder, "Directory.Build.props"), "<Project></Project>");
        File.WriteAllText(Path.Combine(_projectFolder, "Directory.Packages.props"), "<Project></Project>");
    }

    [Fact]
    public async Task WithoutTargetFrameworkTheFirstFrameworkIsDocumented()
    {
        var project = CreateMultiTargetedProject();

        await Exec(new() { Src = Src(project) });

        Assert.True(File.Exists(Path.Combine(_outputFolder, "Lib.OnlyNet8.yml")));
        Assert.False(File.Exists(Path.Combine(_outputFolder, "Lib.OnlyNetStandard.yml")));
    }

    [Fact]
    public async Task TargetFrameworkSelectsTheFrameworkToDocument()
    {
        var project = CreateMultiTargetedProject();

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(project), TargetFramework = "netstandard2.0" });

        Assert.True(File.Exists(Path.Combine(_outputFolder, "Lib.OnlyNetStandard.yml")));
        Assert.False(File.Exists(Path.Combine(_outputFolder, "Lib.OnlyNet8.yml")));
        AssertWarnings(listener);

        // The summary comes from the selected framework's side of the `#if`.
        var shared = YamlUtility.Deserialize<PageViewModel>(Path.Combine(_outputFolder, "Lib.Shared.yml"));
        Assert.Equal("netstandard2.0", shared.Items[0].Summary.Trim());
    }

    [Fact]
    public async Task TargetFrameworkReportsTheAnalyzersOfTheFrameworkItSelects()
    {
        // The analyzer is referenced by the selected framework only, so a warning logged for the first one would miss it.
        var project = CreateProject("Lib", "<TargetFrameworks>net8.0;netstandard2.0</TargetFrameworks>", "namespace Lib { public class C { } }", """
            <ItemGroup Condition="'$(TargetFramework)' == 'netstandard2.0'">
              <Analyzer Include="Missing.Generator.dll" />
            </ItemGroup>
            """);

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(project), TargetFramework = "netstandard2.0" });

        var warnings = listener.GetItemsByLogLevel(LogLevel.Warning).ToList();
        Assert.True(
            warnings.Count == 1 && warnings[0].Code is WarningCodes.Metadata.FailedToResolveAnalyzer or WarningCodes.Metadata.FailedToLoadAnalyzer,
            $"Expected one analyzer warning, got:{Environment.NewLine}" + string.Join(Environment.NewLine, warnings.Select(w => $"  {w.Code}: {w.Message}")));
        Assert.Contains("Missing.Generator", warnings[0].Message);
    }

    [Fact]
    public async Task TargetFrameworkPropertyRetargetsReferencedProjects()
    {
        var project = CreateProjectReferencingNetStandardLibrary();

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(project), Properties = new() { ["TargetFramework"] = "net8.0" } });

        // The global property also loads the netstandard2.0 library as net8.0, so its output no longer
        // matches the netstandard2.0 reference MSBuild resolves for it, and that reference is dropped.
        // This is dotnet/docfx#10772.
        var warnings = listener.GetItemsByLogLevel(LogLevel.Warning).ToList();
        Assert.Contains(warnings, x => x.Message.Contains(ReferenceWithoutMetadataWarning));
        Assert.Contains(warnings, x => x.Message.Contains(UnresolvedReferenceRemovedWarning));
    }

    [Fact]
    public async Task WithoutTargetFrameworkReferencedProjectsResolve()
    {
        var project = CreateProjectReferencingNetStandardLibrary();

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(project) });

        AssertWarnings(listener);
    }

    [Fact]
    public async Task TargetFrameworkLeavesReferencedProjectsAlone()
    {
        var project = CreateProjectReferencingNetStandardLibrary();

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(project), TargetFramework = "net8.0" });

        AssertWarnings(listener);

        var derived = YamlUtility.Deserialize<PageViewModel>(Path.Combine(_outputFolder, "App.Derived.yml"));
        Assert.Contains("Dep.Base", derived.Items[0].Inheritance);
    }

    [Fact]
    public async Task TargetFrameworkTheProjectDoesNotTargetFallsBackToItsDefault()
    {
        var project = CreateMultiTargetedProject();

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(project), TargetFramework = "net9.0" });

        var warning = AssertWarnings(listener, WarningCodes.Metadata.TargetFrameworkNotFound);
        Assert.Contains("'net8.0', 'netstandard2.0'", warning.Message);
        Assert.True(File.Exists(Path.Combine(_outputFolder, "Lib.OnlyNet8.yml")));
    }

    [Theory]
    [InlineData("NETSTANDARD2.0", "Lib.OnlyNetStandard.yml")]
    [InlineData("Net8.0", "Lib.OnlyNet8.yml")]
    public async Task TargetFrameworkOfAMultiTargetedProjectIsCaseInsensitive(string targetFramework, string expectedFile)
    {
        var project = CreateMultiTargetedProject();

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(project), TargetFramework = targetFramework });

        AssertWarnings(listener);
        Assert.True(File.Exists(Path.Combine(_outputFolder, expectedFile)));
    }

    [Theory]
    // net8.0-windows is net8.0-windows7.0, MSBuild says
    [InlineData("net8.0-windows", "Platform.OnlyWindows.yml")]
    [InlineData("net8.0-windows7.0", "Platform.OnlyWindows.yml")]
    [InlineData("net8.0", "Platform.OnlyPortable.yml")]
    public async Task TargetFrameworkWithAPlatformOfAMultiTargetedProject(string targetFramework, string expectedFile)
    {
        var project = CreatePlatformProject("<TargetFrameworks>net8.0;net8.0-windows</TargetFrameworks>");

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(project), TargetFramework = targetFramework });

        AssertWarnings(listener);
        Assert.True(File.Exists(Path.Combine(_outputFolder, expectedFile)));
    }

    [Theory]
    [InlineData("net8.0-windows", false)]
    [InlineData("net8.0-windows7.0", false)]
    [InlineData("net8.0", true)]
    public async Task TargetFrameworkWithAPlatformOfASingleTargetProject(string targetFramework, bool expectWarning)
    {
        var project = CreatePlatformProject("<TargetFramework>net8.0-windows</TargetFramework>");

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(project), TargetFramework = targetFramework });

        if (expectWarning)
        {
            AssertWarnings(listener, WarningCodes.Metadata.TargetFrameworkNotFound);
        }
        else
        {
            AssertWarnings(listener);
        }
        Assert.True(File.Exists(Path.Combine(_outputFolder, "Platform.OnlyWindows.yml")));
    }

    [Fact]
    public async Task TargetFrameworkWithAPlatformVersionAMultiTargetedProjectDoesNotTargetWarns()
    {
        // net8.0-windows is net8.0-windows7.0
        var project = CreatePlatformProject("<TargetFrameworks>net8.0;net8.0-windows</TargetFrameworks>");

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(project), TargetFramework = "net8.0-windows10.0.19041.0" });

        var warning = AssertWarnings(listener, WarningCodes.Metadata.TargetFrameworkNotFound);
        Assert.Contains("does not target 'net8.0-windows10.0.19041.0'", warning.Message);
    }

    [Theory]
    // MSBuild says which framework a project targets, whatever its symbols and attributes say
    [InlineData("net8.0", false)]
    [InlineData("net9.0", true)]
    public async Task TargetFrameworkOnAProjectWithoutFrameworkSymbolsAndAttributes(string targetFramework, bool expectWarning)
    {
        var project = CreateProject(
            "Legacy",
            "<TargetFramework>net8.0</TargetFramework><DisableImplicitFrameworkDefines>true</DisableImplicitFrameworkDefines><GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>",
            "namespace Legacy { public class Widget { } }");

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(project), TargetFramework = targetFramework });

        if (expectWarning)
        {
            var warning = AssertWarnings(listener, WarningCodes.Metadata.TargetFrameworkNotFound);
            Assert.Contains($"does not target '{targetFramework}'", warning.Message);
        }
        else
        {
            AssertWarnings(listener);
        }
        Assert.True(File.Exists(Path.Combine(_outputFolder, "Legacy.Widget.yml")));
    }

    [Theory]
    // without `GenerateAssemblyInfo` the SDK generates no TargetPlatformAttribute, and with
    // `DisableImplicitFrameworkDefines` no symbols; windows8.0 needs no Windows SDK package
    [InlineData("net8.0-windows8.0", false)]
    [InlineData("net8.0-windows", false)]
    [InlineData("net8.0", true)]
    public async Task TargetFrameworkWithAPlatformOfASingleTargetProjectWithoutAttributesAndSymbols(string targetFramework, bool expectWarning)
    {
        var project = CreatePlatformProject(
            "<TargetFramework>net8.0-windows8.0</TargetFramework><GenerateAssemblyInfo>false</GenerateAssemblyInfo><DisableImplicitFrameworkDefines>true</DisableImplicitFrameworkDefines>");

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(project), TargetFramework = targetFramework });

        if (expectWarning)
        {
            AssertWarnings(listener, WarningCodes.Metadata.TargetFrameworkNotFound);
        }
        else
        {
            AssertWarnings(listener);
        }
        // no `WINDOWS` symbol either
        Assert.True(File.Exists(Path.Combine(_outputFolder, "Platform.OnlyPortable.yml")));
    }

    [Fact]
    public async Task TargetFrameworkWhosePlatformVersionIsNotAVersionIsAnError()
    {
        var project = CreatePlatformProject("<TargetFramework>net8.0-windows8.0</TargetFramework>");

        using var listener = new TestListenerScope();

        // a letter O for the zero, which would otherwise select every version of the platform
        await Exec(new() { Src = Src(project), TargetFramework = "net8.0-windows8.O" });

        Assert.Contains(listener.GetItemsByLogLevel(LogLevel.Error), x => x.Code == ErrorCodes.Metadata.InvalidTargetFramework);
        Assert.False(File.Exists(Path.Combine(_outputFolder, "Platform.OnlyWindows.yml")));
    }

    [Theory]
    [InlineData("net8.0", false)]
    [InlineData("NET8.0", false)]
    [InlineData("netstandard2.0", true)]
    public async Task TargetFrameworkOnASingleTargetProject(string targetFramework, bool expectWarning)
    {
        var project = CreateProject("Single", "<TargetFramework>net8.0</TargetFramework>", "namespace Single { public class Widget { } }");

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(project), TargetFramework = targetFramework });

        if (expectWarning)
        {
            AssertWarnings(listener, WarningCodes.Metadata.TargetFrameworkNotFound);
        }
        else
        {
            AssertWarnings(listener);
        }
        Assert.True(File.Exists(Path.Combine(_outputFolder, "Single.Widget.yml")));
    }

    [Theory]
    [InlineData("net8.0", false)]
    // `SQLITE` and `SQLITE3` have the form of a platform and its version, which the project does not target
    [InlineData("net8.0-sqlite3.0", true)]
    [InlineData("net8.0-sqlite", true)]
    public async Task TargetFrameworkOnASingleTargetProjectWithOwnSymbolsShapedLikeAPlatform(string targetFramework, bool expectWarning)
    {
        var project = CreateProject(
            "Single",
            "<TargetFramework>net8.0</TargetFramework><DefineConstants>$(DefineConstants);SQLITE;SQLITE3</DefineConstants>",
            "namespace Single { public class Widget { } }");

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(project), TargetFramework = targetFramework });

        if (expectWarning)
        {
            var warning = AssertWarnings(listener, WarningCodes.Metadata.TargetFrameworkNotFound);
            Assert.Contains("documenting 'net8.0' instead", warning.Message);
        }
        else
        {
            AssertWarnings(listener);
        }
        Assert.True(File.Exists(Path.Combine(_outputFolder, "Single.Widget.yml")));
    }

    [Fact]
    public async Task TargetFrameworkOnASolutionFilter()
    {
        var library = CreateMultiTargetedProject();
        var solution = CreateSolution(
            library,
            CreateProject("Analyzer", "<TargetFramework>netstandard2.0</TargetFramework>", "namespace Analyzer { public class Rule { } }"));
        var filter = Path.Combine(_projectFolder, "Lib.slnf");
        File.WriteAllText(filter, $$"""
            { "solution": { "path": "{{Path.GetFileName(solution)}}", "projects": [ "{{Path.GetRelativePath(_projectFolder, library).Replace("\\", "\\\\")}}" ] } }
            """);

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(filter), TargetFramework = "netstandard2.0" });

        AssertWarnings(listener);
        Assert.True(File.Exists(Path.Combine(_outputFolder, "Lib.OnlyNetStandard.yml")));
        Assert.False(File.Exists(Path.Combine(_outputFolder, "Lib.OnlyNet8.yml")));
        // the filter leaves the analyzer out
        Assert.False(File.Exists(Path.Combine(_outputFolder, "Analyzer.Rule.yml")));
    }

    [Fact]
    public async Task TargetFrameworkSelectsTheFrameworkOfAListedProjectAProjectOfASolutionReferenced()
    {
        // Loading the solution's App loads every framework of Lib, which `src` then lists itself.
        var library = CreateMultiTargetedProject();
        var app = CreateProject(
            "App",
            "<TargetFrameworks>net8.0;netstandard2.0</TargetFrameworks>",
            "namespace App { public class Widget { } }",
            $"""<ItemGroup><ProjectReference Include="{Path.GetFullPath(library)}" /></ItemGroup>""");
        var solution = CreateSolution(app);

        using var listener = new TestListenerScope();

        await Exec(new() { Src = new(new FileMappingItem(solution, library)) { Expanded = true }, TargetFramework = "netstandard2.0" });

        AssertWarnings(listener);
        Assert.True(File.Exists(Path.Combine(_outputFolder, "Lib.OnlyNetStandard.yml")));
        Assert.False(File.Exists(Path.Combine(_outputFolder, "Lib.OnlyNet8.yml")));
    }

    [Fact]
    public async Task TargetFrameworkSelectsTheFrameworkOfAProjectAnEarlierOneReferenced()
    {
        // App references Lib, so loading App loads every framework of Lib too, and Lib is then taken from
        // the solution rather than opened, which must not skip the selection.
        var library = CreateMultiTargetedProject();
        var app = CreateProject(
            "App",
            "<TargetFrameworks>net8.0;netstandard2.0</TargetFrameworks>",
            "namespace App { public class Widget { } }",
            $"""<ItemGroup><ProjectReference Include="{Path.GetFullPath(library)}" /></ItemGroup>""");

        using var listener = new TestListenerScope();

        await Exec(new() { Src = new(new FileMappingItem(app, library)) { Expanded = true }, TargetFramework = "netstandard2.0" });

        AssertWarnings(listener);
        Assert.True(File.Exists(Path.Combine(_outputFolder, "Lib.OnlyNetStandard.yml")));
        Assert.False(File.Exists(Path.Combine(_outputFolder, "Lib.OnlyNet8.yml")));
    }

    [Fact]
    public async Task TargetFrameworkOnASolutionReportsOtherProjectsAsInformation()
    {
        // An analyzer that targets only netstandard2.0 is the common case of a solution project that does
        // not target the framework the site documents.
        var solution = CreateSolution(
            CreateMultiTargetedProject(),
            CreateProject("Analyzer", "<TargetFramework>netstandard2.0</TargetFramework>", "namespace Analyzer { public class Rule { } }"));

        using var listener = new TestListenerScope(LogLevel.Info);

        await Exec(new() { Src = Src(solution), TargetFramework = "net8.0" });

        AssertWarnings(listener);
        Assert.Contains(listener.Items, x => x.LogLevel == LogLevel.Info && x.Message.Contains("does not target 'net8.0'"));
        Assert.True(File.Exists(Path.Combine(_outputFolder, "Lib.OnlyNet8.yml")));
        Assert.True(File.Exists(Path.Combine(_outputFolder, "Analyzer.Rule.yml")));
    }

    [Fact]
    public async Task TargetFrameworkNoProjectOfASolutionTargetsWarnsOnce()
    {
        var solution = CreateSolution(
            CreateMultiTargetedProject(),
            CreateProject("Analyzer", "<TargetFramework>netstandard2.0</TargetFramework>", "namespace Analyzer { public class Rule { } }"));

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(solution), TargetFramework = "net9.0" });

        AssertWarnings(listener, WarningCodes.Metadata.TargetFrameworkNotFound);
    }

    [Fact]
    public async Task TargetFrameworkWarnsForEachSolutionNoProjectOfWhichTargetsIt()
    {
        var matching = CreateNamedSolution("Matching", CreateMultiTargetedProject());
        var other = CreateNamedSolution(
            "Other",
            CreateProject("Analyzer", "<TargetFramework>netstandard2.0</TargetFramework>", "namespace Analyzer { public class Rule { } }"));

        using var listener = new TestListenerScope();

        await Exec(new() { Src = new(new FileMappingItem(matching, other)) { Expanded = true }, TargetFramework = "net8.0" });

        var warning = AssertWarnings(listener, WarningCodes.Metadata.TargetFrameworkNotFound);
        Assert.Contains("Other.sln", warning.Message);
    }

    [Fact]
    public async Task TargetFrameworkWarnsOnceForSolutionsThatShareTheirProjects()
    {
        var library = CreateMultiTargetedProject();
        var analyzer = CreateProject("Analyzer", "<TargetFramework>netstandard2.0</TargetFramework>", "namespace Analyzer { public class Rule { } }");
        var all = CreateNamedSolution("All", library, analyzer);
        var core = CreateNamedSolution("Core", library);

        using var listener = new TestListenerScope();

        await Exec(new() { Src = new(new FileMappingItem(all, core)) { Expanded = true }, TargetFramework = "net9.0" });

        // one cause, one warning: the second solution holds no project the first did not report
        var warning = AssertWarnings(listener, WarningCodes.Metadata.TargetFrameworkNotFound);
        Assert.Contains("All.sln", warning.Message);
    }

    [Fact]
    public async Task TargetFrameworkSelectsTheFrameworkOfEveryProjectOfASolutionThatReferencesAnother()
    {
        // Both target net8.0;netstandard2.0, and the solution lists App, which references Lib, first.
        var library = CreateMultiTargetedProject();
        var app = CreateProject(
            "App",
            "<TargetFrameworks>net8.0;netstandard2.0</TargetFrameworks>",
            """
            namespace App
            {
            #if NET8_0
                public class OnlyNet8 : Lib.Shared { }
            #else
                public class OnlyNetStandard : Lib.Shared { }
            #endif
            }
            """,
            $"""<ItemGroup><ProjectReference Include="{Path.GetFullPath(library)}" /></ItemGroup>""");

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(CreateSolution(app, library)), TargetFramework = "netstandard2.0" });

        AssertWarnings(listener);
        Assert.True(File.Exists(Path.Combine(_outputFolder, "App.OnlyNetStandard.yml")));
        Assert.True(File.Exists(Path.Combine(_outputFolder, "Lib.OnlyNetStandard.yml")));
        Assert.False(File.Exists(Path.Combine(_outputFolder, "App.OnlyNet8.yml")));
        Assert.False(File.Exists(Path.Combine(_outputFolder, "Lib.OnlyNet8.yml")));
    }

    [Fact]
    public async Task TargetFrameworkSelectsTheFrameworkOfSolutionAndProjectInputAlike()
    {
        var solution = CreateSolution(
            CreateMultiTargetedProject(),
            CreateProject("Analyzer", "<TargetFramework>netstandard2.0</TargetFramework>", "namespace Analyzer { public class Rule { } }"));
        var single = CreateProject("Single", "<TargetFramework>net8.0</TargetFramework>", "namespace Single { public class Widget { } }");

        using var listener = new TestListenerScope();

        await Exec(new() { Src = new(new FileMappingItem(solution, single)) { Expanded = true }, TargetFramework = "net8.0" });

        AssertWarnings(listener);
        Assert.True(File.Exists(Path.Combine(_outputFolder, "Lib.OnlyNet8.yml")));
        Assert.False(File.Exists(Path.Combine(_outputFolder, "Lib.OnlyNetStandard.yml")));
        Assert.True(File.Exists(Path.Combine(_outputFolder, "Analyzer.Rule.yml")));
        Assert.True(File.Exists(Path.Combine(_outputFolder, "Single.Widget.yml")));
    }

    [Fact]
    public async Task TargetFrameworkReportsAProjectListedAndInAListedSolutionOnce()
    {
        var project = CreateMultiTargetedProject();
        var solution = CreateSolution(project);

        using var listener = new TestListenerScope(LogLevel.Info);

        await Exec(new() { Src = new(new FileMappingItem(solution, project)) { Expanded = true }, TargetFramework = "net9.0" });

        // as a warning, as `src` lists it, and the solution, which holds no other project, does not warn too
        var reports = listener.Items.Where(x => x.Message.Contains("Lib.csproj' does not target")).ToList();
        Assert.Equal(LogLevel.Warning, Assert.Single(reports).LogLevel);
        AssertWarnings(listener, WarningCodes.Metadata.TargetFrameworkNotFound);
    }

    [Fact]
    public async Task TargetFrameworkCountsAMatchingProjectListedAndInAListedSolution()
    {
        var project = CreateMultiTargetedProject();
        var solution = CreateSolution(
            project,
            CreateProject("Analyzer", "<TargetFramework>netstandard2.0</TargetFramework>", "namespace Analyzer { public class Rule { } }"));

        using var listener = new TestListenerScope();

        await Exec(new() { Src = new(new FileMappingItem(solution, project)) { Expanded = true }, TargetFramework = "net8.0" });

        // Lib targets net8.0, so the solution does, though `src` lists Lib too
        AssertWarnings(listener);
        Assert.True(File.Exists(Path.Combine(_outputFolder, "Lib.OnlyNet8.yml")));
    }

    [Theory]
    [InlineData("targetframework")]
    [InlineData("TargetFrameworks")]
    public async Task TargetFrameworkAndATargetFrameworkPropertyTogetherWarn(string property)
    {
        var project = CreateMultiTargetedProject();

        using var listener = new TestListenerScope();

        await Exec(new()
        {
            Src = Src(project),
            TargetFramework = "net8.0",
            Properties = new() { [property] = "net8.0" },
        });

        AssertWarnings(listener, WarningCodes.Metadata.ConflictingTargetFramework);
    }

    [Fact]
    public async Task TargetFrameworkWithoutProjectInputIsInformation()
    {
        var directory = Path.Combine(_projectFolder, "Source");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "Widget.cs");
        File.WriteAllText(source, "namespace Source { public class Widget { } }");

        using var listener = new TestListenerScope(LogLevel.Info);

        // nor does a `TargetFramework` property conflict with it, as no project is loaded
        await Exec(new()
        {
            Src = Src(source),
            TargetFramework = "net8.0",
            Properties = new() { ["TargetFramework"] = "net8.0" },
        });

        AssertWarnings(listener);
        Assert.Contains(listener.Items, x => x.LogLevel == LogLevel.Info && x.Message.Contains("applies to project and solution input only"));
        Assert.True(File.Exists(Path.Combine(_outputFolder, "Source.Widget.yml")));
    }

    [Theory]
    [InlineData("net8.0", false)]
    [InlineData("netstandard2.0", true)]
    public async Task TargetFrameworkOnASingleTargetVisualBasicProject(string targetFramework, bool expectWarning)
    {
        // A Visual Basic project yields no API outside Windows, as in MetadataCommandTest.TestMetadataCommandFromVBProject.
        if (!OperatingSystem.IsWindows())
            return;

        var project = CreateProject(
            "VbLib",
            "<TargetFramework>net8.0</TargetFramework><RootNamespace></RootNamespace>",
            """
            Namespace VbLib
                Public Class Widget
                End Class
            End Namespace
            """,
            language: "vb");

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(project), TargetFramework = targetFramework });

        if (expectWarning)
        {
            AssertWarnings(listener, WarningCodes.Metadata.TargetFrameworkNotFound);
        }
        else
        {
            AssertWarnings(listener);
        }
        Assert.True(File.Exists(Path.Combine(_outputFolder, "VbLib.Widget.yml")));
    }

    [Theory]
    // windows8.0 needs no Windows SDK package
    [InlineData("net8.0-windows8.0", false)]
    [InlineData("net8.0-windows8", false)]
    [InlineData("net8.0-windows", false)]
    [InlineData("net8.0-windows7.0", true)]
    public async Task TargetFrameworkWithAPlatformVersionOfASingleTargetProject(string targetFramework, bool expectWarning)
    {
        var project = CreatePlatformProject("<TargetFramework>net8.0-windows8.0</TargetFramework>");

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(project), TargetFramework = targetFramework });

        if (expectWarning)
        {
            var warning = AssertWarnings(listener, WarningCodes.Metadata.TargetFrameworkNotFound);
            Assert.Contains($"does not target '{targetFramework}'", warning.Message);
        }
        else
        {
            AssertWarnings(listener);
        }
        Assert.True(File.Exists(Path.Combine(_outputFolder, "Platform.OnlyWindows.yml")));
    }

    [Fact]
    public async Task TargetFrameworksMergesEveryFrameworkOfAProject()
    {
        var project = CreateMultiTargetedProject();

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(project), TargetFrameworks = ["*"] });

        AssertWarnings(listener);
        Assert.Equal(["net8.0"], AppliesTo("Lib.OnlyNet8.yml"));
        Assert.Equal(["netstandard2.0"], AppliesTo("Lib.OnlyNetStandard.yml"));
        Assert.Equal(["net8.0", "netstandard2.0"], AppliesTo("Lib.Shared.yml"));

        // "*" keeps the framework the project is documented at by default as the one providing the content.
        Assert.Equal("net8.0", Page("Lib.Shared.yml").Items[0].Summary.Trim());
    }

    [Fact]
    public async Task TargetFrameworksFirstListedFrameworkProvidesTheContent()
    {
        var project = CreateMultiTargetedProject();

        await Exec(new() { Src = Src(project), TargetFrameworks = ["netstandard2.0", "net8.0"] });

        Assert.Equal(["netstandard2.0", "net8.0"], AppliesTo("Lib.Shared.yml"));
        Assert.Equal("netstandard2.0", Page("Lib.Shared.yml").Items[0].Summary.Trim());
    }

    [Fact]
    public async Task TargetFrameworksWithOneFrameworkIsPlainSelection()
    {
        var project = CreateMultiTargetedProject();

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(project), TargetFrameworks = ["netstandard2.0"] });

        AssertWarnings(listener);
        Assert.False(File.Exists(Path.Combine(_outputFolder, "Lib.OnlyNet8.yml")));
        Assert.Null(Page("Lib.Shared.yml").Items[0].AppliesTo);
    }

    [Fact]
    public async Task TargetFrameworksAProjectDoesNotTargetWarnsAndTheOthersAreDocumented()
    {
        var project = CreateMultiTargetedProject();

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(project), TargetFrameworks = ["net9.0", "net8.0", "netstandard2.0"] });

        var warning = AssertWarnings(listener, WarningCodes.Metadata.TargetFrameworkNotFound);
        Assert.Contains("'net9.0'", warning.Message);
        Assert.Equal(["net8.0", "netstandard2.0"], AppliesTo("Lib.Shared.yml"));
    }

    [Fact]
    public async Task TargetFrameworksTakesAProjectOnlyAtListedFrameworks()
    {
        var project = CreateMultiTargetedProject();

        await Exec(new() { Src = Src(project), TargetFrameworks = ["net9.0"] });

        Assert.False(File.Exists(Path.Combine(_outputFolder, "Lib.Shared.yml")));
    }

    [Theory]
    [InlineData("net8.0", "Dep.OnlyNet8")]
    [InlineData("netstandard2.0", "Dep.OnlyNetStandard")]
    public async Task TargetFrameworksResolvesAMultiTargetedReferenceAtTheSameFramework(string primary, string expectedBase)
    {
        var project = CreateProjectReferencingMultiTargetedLibrary();
        var other = primary == "net8.0" ? "netstandard2.0" : "net8.0";

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(project), TargetFrameworks = [primary, other] });

        AssertWarnings(listener);
        Assert.Equal([primary, other], AppliesTo("App.Derived.yml"));

        var page = Page("App.Derived.yml");
        Assert.Contains(expectedBase, page.Items[0].Inheritance);

        // The base type of the other framework is a difference, and the page keeps a reference to link it.
        var otherBase = primary == "net8.0" ? "Dep.OnlyNetStandard" : "Dep.OnlyNet8";
        var variant = Assert.Single(page.Items[0].Variants);
        Assert.Equal([other], variant.AppliesTo.Single().Frameworks);
        Assert.Contains(otherBase, variant.Inheritance);
        Assert.Contains(page.References, r => r.Uid == otherBase);
    }

    [Fact]
    public async Task VariantIgnoreIsReadFromTheConfig()
    {
        var project = CreateMultiTargetedProject();

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(project), TargetFrameworks = ["net8.0", "netstandard2.0"], VariantIgnore = ["documentation"] });

        AssertWarnings(listener);
        Assert.Null(Page("Lib.Shared.yml").Items[0].Variants);
    }

    [Fact]
    public async Task VariantIgnoreWarnsAboutAnUnknownValue()
    {
        var project = CreateMultiTargetedProject();

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(project), TargetFrameworks = ["net8.0", "netstandard2.0"], VariantIgnore = ["nullability"] });

        var warning = AssertWarnings(listener, WarningCodes.Metadata.InvalidVariantIgnore);
        Assert.Contains("'nullability'", warning.Message);
    }

    [Fact]
    public async Task TargetFrameworksRecordsOnlyWhatDiffersAsAVariant()
    {
        var project = CreateMultiTargetedProject();

        await Exec(new() { Src = Src(project), TargetFrameworks = ["net8.0", "netstandard2.0"] });

        var item = Page("Lib.Shared.yml").Items[0];
        Assert.Equal("net8.0", item.Summary.Trim());

        var variant = Assert.Single(item.Variants);
        Assert.Equal(["netstandard2.0"], variant.AppliesTo.Single().Frameworks);
        Assert.Equal("netstandard2.0", variant.Summary.Trim());
        Assert.Null(variant.Syntax);
        Assert.Null(variant.Inheritance);
    }

    [Fact]
    public async Task TargetFrameworksFindsExtensionMethodsWithinEachFramework()
    {
        var project = CreateProject("Ext", "<TargetFrameworks>net8.0;netstandard2.0</TargetFrameworks>", """
            namespace Ext
            {
                public class Target { }

                public static class Extensions
                {
            #if NET8_0
                    public static void OnlyNet8(this Target target) { }
            #else
                    public static void OnlyNetStandard(this Target target) { }
            #endif
                }
            }
            """);

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(project), TargetFrameworks = ["net8.0", "netstandard2.0"] });

        AssertWarnings(listener);
        var extensions = Page("Ext.Target.yml").Items[0].ExtensionMethods;
        Assert.Contains(extensions, x => x.Contains("OnlyNet8"));
        Assert.Contains(extensions, x => x.Contains("OnlyNetStandard"));
        Assert.Equal(["netstandard2.0"], AppliesTo("Ext.Extensions.yml", "Ext.Extensions.OnlyNetStandard(Ext.Target)"));
    }

    [Fact]
    public async Task TargetFrameworksAndTargetFrameworkTogetherWarnAndTheSingleOneWins()
    {
        var project = CreateMultiTargetedProject();

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(project), TargetFramework = "netstandard2.0", TargetFrameworks = ["*"] });

        AssertWarnings(listener, WarningCodes.Metadata.ConflictingTargetFramework);
        Assert.False(File.Exists(Path.Combine(_outputFolder, "Lib.OnlyNet8.yml")));
        Assert.Null(Page("Lib.Shared.yml").Items[0].AppliesTo);
    }

    [Theory]
    [InlineData(nameof(MetadataOutputFormat.Markdown))]
    [InlineData(nameof(MetadataOutputFormat.ApiPage))]
    public async Task TargetFrameworksIsAnErrorForOutputFormatsThatCannotMerge(string outputFormat)
    {
        var project = CreateMultiTargetedProject();

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(project), TargetFrameworks = ["*"], OutputFormat = Enum.Parse<MetadataOutputFormat>(outputFormat) });

        Assert.Contains(listener.GetItemsByLogLevel(LogLevel.Error), x => x.Code == ErrorCodes.Metadata.TargetFrameworksNotSupported);
    }

    private PageViewModel Page(string fileName)
    {
        return YamlUtility.Deserialize<PageViewModel>(Path.Combine(_outputFolder, fileName));
    }

    private List<string> AppliesTo(string fileName, string uid = null)
    {
        var page = Page(fileName);
        return (uid is null ? page.Items[0] : page.Items.Single(i => i.Uid == uid)).AppliesTo.Single().Frameworks;
    }

    private Task Exec(MetadataJsonItemConfig config)
    {
        config.Dest = _outputFolder;
        return DotnetApiCatalog.Exec(new(config), new(), Directory.GetCurrentDirectory(), cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Asserts the warnings logged are exactly one per code given, and returns the first, reporting the
    /// full text of every warning on failure.
    /// </summary>
    private static ILogItem AssertWarnings(TestListenerScope listener, params string[] codes)
    {
        var warnings = listener.GetItemsByLogLevel(LogLevel.Warning).ToList();
        Assert.True(
            warnings.Select(w => w.Code).SequenceEqual(codes),
            $"Expected warnings [{string.Join(", ", codes)}], got:{Environment.NewLine}"
            + string.Join(Environment.NewLine, warnings.Select(w => $"  {w.Code}: {w.Message}")));
        return warnings.FirstOrDefault();
    }

    private static FileMapping Src(string project) => new(new FileMappingItem(project)) { Expanded = true };

    /// <summary>
    /// A project targeting <c>net8.0;netstandard2.0</c> with one type per framework, and a type whose
    /// documentation differs between them.
    /// </summary>
    private string CreateMultiTargetedProject()
    {
        return CreateProject("Lib", "<TargetFrameworks>net8.0;netstandard2.0</TargetFrameworks>", """
            namespace Lib
            {
            #if NET8_0
                public class OnlyNet8 { }

                /// <summary>net8.0</summary>
                public class Shared { }
            #else
                public class OnlyNetStandard { }

                /// <summary>netstandard2.0</summary>
                public class Shared { }
            #endif
            }
            """);
    }

    /// <summary>
    /// A multi-targeted project referencing a library that targets only <c>netstandard2.0</c>, which is
    /// the shape of a project referencing an analyzer or a shared contracts library.
    /// </summary>
    private string CreateProjectReferencingNetStandardLibrary()
    {
        // Roslyn only pairs a project reference with its metadata reference when the referenced project
        // has a reference assembly path, which `netstandard2.0` projects do not produce by default.
        // Without one the reference warning below is logged however the framework is chosen.
        var library = CreateProject(
            "Dep",
            "<TargetFramework>netstandard2.0</TargetFramework><ProduceReferenceAssembly>true</ProduceReferenceAssembly>",
            "namespace Dep { public class Base { } }");

        return CreateProject(
            "App",
            "<TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks>",
            "namespace App { public class Derived : Dep.Base { } }",
            $"""<ItemGroup><ProjectReference Include="{Path.GetFullPath(library)}" /></ItemGroup>""");
    }

    /// <summary>
    /// A project with one type for Windows and one for any other platform. Windows targeting is enabled so
    /// that it also builds on Linux and macOS.
    /// </summary>
    private string CreatePlatformProject(string targetFrameworks)
    {
        return CreateProject("Platform", $"{targetFrameworks}<EnableWindowsTargeting>true</EnableWindowsTargeting>", """
            namespace Platform
            {
            #if WINDOWS
                public class OnlyWindows { }
            #else
                public class OnlyPortable { }
            #endif
            }
            """);
    }

    private string CreateSolution(params string[] projects) => CreateNamedSolution("All", projects);

    private string CreateNamedSolution(string name, params string[] projects)
    {
        var solution = Path.Combine(_projectFolder, $"{name}.sln");
        var entries = string.Concat(projects.Select(project => $$"""
            Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "{{Path.GetFileNameWithoutExtension(project)}}", "{{Path.GetRelativePath(_projectFolder, project)}}", "{{{Guid.NewGuid().ToString().ToUpperInvariant()}}}"
            EndProject

            """));
        File.WriteAllText(solution, $"""
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            {entries}Global
            EndGlobal
            """);
        return solution;
    }

    /// <summary>
    /// A multi-targeted project deriving from a type that exists only in the matching framework of a
    /// multi-targeted library, so it compiles only if each framework resolves the reference to its own.
    /// </summary>
    private string CreateProjectReferencingMultiTargetedLibrary()
    {
        var library = CreateProject(
            "Dep",
            "<TargetFrameworks>net8.0;netstandard2.0</TargetFrameworks><ProduceReferenceAssembly>true</ProduceReferenceAssembly>",
            """
            namespace Dep
            {
            #if NET8_0
                public class OnlyNet8 { }
            #else
                public class OnlyNetStandard { }
            #endif
            }
            """);

        return CreateProject(
            "App",
            "<TargetFrameworks>net8.0;netstandard2.0</TargetFrameworks>",
            """
            namespace App
            {
            #if NET8_0
                public class Derived : Dep.OnlyNet8 { }
            #else
                public class Derived : Dep.OnlyNetStandard { }
            #endif
            }
            """,
            $"""<ItemGroup><ProjectReference Include="{Path.GetFullPath(library)}" /></ItemGroup>""");
    }

    private string CreateProject(string name, string targetFrameworks, string source, string items = "", string language = "cs")
    {
        var directory = Path.Combine(_projectFolder, name);
        Directory.CreateDirectory(directory);

        var project = Path.Combine(directory, $"{name}.{language}proj");
        File.WriteAllText(project, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                {targetFrameworks}
                <GenerateDocumentationFile>true</GenerateDocumentationFile>
              </PropertyGroup>
              {items}
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, $"{name}.{language}"), source);
        return project;
    }
}
