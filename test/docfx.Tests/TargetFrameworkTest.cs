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
