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
    public async Task TargetFrameworkAndTheTargetFrameworkPropertyTogetherWarn()
    {
        var project = CreateMultiTargetedProject();

        using var listener = new TestListenerScope();

        await Exec(new()
        {
            Src = Src(project),
            TargetFramework = "net8.0",
            Properties = new() { ["targetframework"] = "net8.0" },
        });

        Assert.Contains(listener.GetItemsByLogLevel(LogLevel.Warning), x => x.Code == WarningCodes.Metadata.ConflictingTargetFramework);
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

    private string CreateSolution(params string[] projects)
    {
        var solution = Path.Combine(_projectFolder, "All.sln");
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

    private string CreateProject(string name, string targetFrameworks, string source, string items = "")
    {
        var directory = Path.Combine(_projectFolder, name);
        Directory.CreateDirectory(directory);

        var project = Path.Combine(directory, $"{name}.csproj");
        File.WriteAllText(project, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                {targetFrameworks}
                <GenerateDocumentationFile>true</GenerateDocumentationFile>
              </PropertyGroup>
              {items}
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, $"{name}.cs"), source);
        return project;
    }
}
