// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Docfx.Common;
using Docfx.DataContracts.Common;
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

    public override void Dispose()
    {
        // Some cases log errors on purpose, and the command run by a later test reports any error logged.
        Logger.ResetCount();
        base.Dispose();
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
    public async Task DefaultFrameworkProvidesTheContentAndIsListedFirst()
    {
        var project = CreateMultiTargetedProject();

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(project), TargetFrameworks = ["netstandard2.0", "net8.0"], DefaultFramework = "net8.0" });

        AssertWarnings(listener);
        Assert.Equal(["net8.0", "netstandard2.0"], AppliesTo("Lib.Shared.yml"));
        var item = Page("Lib.Shared.yml").Items[0];
        Assert.Equal("net8.0", item.Summary.Trim());
        Assert.Equal("netstandard2.0", Assert.Single(item.Variants).Summary.Trim());
    }

    [Fact]
    public async Task ADefaultFrameworkNotDocumentedWarnsAndTheNearestProvidesTheContent()
    {
        var project = CreateMultiTargetedProject();

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(project), TargetFrameworks = ["netstandard2.0", "net8.0"], DefaultFramework = "net10.0" });

        var warning = AssertWarnings(listener, WarningCodes.Metadata.TargetFrameworkNotFound);
        Assert.Contains("'net10.0'", warning.Message);
        Assert.Equal("net8.0", Page("Lib.Shared.yml").Items[0].Summary.Trim());
    }

    [Fact]
    public async Task ADefaultFrameworkOneProjectDoesNotTargetFallsBackToTheNearestOneItDoes()
    {
        var (v1, v2) = CreateTwoVersionsOfOneAssembly(v1Frameworks: "net462;netstandard2.0");

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(v1, v2), Axes = [Axis(defaultValue: "v2")], DefaultFramework = "net8.0" });

        // v2 targets the default framework, so there is no warning about it.
        AssertWarnings(listener);

        // A page only v1 has is documented from netstandard2.0, which net8.0 can reference, not net462.
        var onlyV1 = Page("Lib.OnlyV1.yml").Items[0];
        Assert.Equal(["netstandard2.0", "net462"], Assert.Single(onlyV1.AppliesTo).Frameworks);
        Assert.Equal("v1 netstandard2.0", onlyV1.Summary.Trim());
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

    [Fact]
    public async Task TargetFrameworksOnASolutionReportsProjectsMissingAFrameworkAsInformation()
    {
        var solution = CreateSolution(
            CreateMultiTargetedProject(),
            CreateProject("Analyzer", "<TargetFramework>netstandard2.0</TargetFramework>", "namespace Analyzer { public class Rule { } }"));

        using var listener = new TestListenerScope(LogLevel.Info);

        await Exec(new() { Src = Src(solution), TargetFrameworks = ["net8.0", "netstandard2.0"] });

        AssertWarnings(listener);
        Assert.Contains(listener.Items, x => x.LogLevel == LogLevel.Info && x.Message.Contains("does not target 'net8.0'"));
        Assert.Equal(["netstandard2.0"], AppliesTo("Analyzer.Rule.yml"));
    }

    [Fact]
    public async Task TargetFrameworksWithSourceFilesIsAnError()
    {
        // Source files and assemblies have no target framework to say an API applies to.
        var project = CreateMultiTargetedProject();
        var source = Path.Combine(_projectFolder, "Extra.cs");
        File.WriteAllText(source, "namespace Extra { public class Tool { } }");

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(project, source), TargetFrameworks = ["*"] });

        Assert.Contains(listener.GetItemsByLogLevel(LogLevel.Error), x => x.Code == ErrorCodes.Metadata.TargetFrameworksNotSupported);
    }

    [Fact]
    public async Task TargetFrameworkWithAnAxisDocumentsThatFrameworkForEachValue()
    {
        var (v1, v2) = CreateTwoVersionsOfOneAssembly(v1Frameworks: "net8.0;netstandard2.0");

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(v1, v2), Axes = [Axis(defaultValue: "v2")], TargetFramework = "net8.0" });

        AssertWarnings(listener);
        var shared = Page("Lib.Shared.yml").Items[0];
        Assert.Equal(["v1", "v2"], shared.AppliesTo.Select(a => a.Version));
        Assert.All(shared.AppliesTo, a => Assert.Equal(["net8.0"], a.Frameworks));
    }

    [Fact]
    public async Task AnAxisMergesProjectsThatBuildTheSameAssembly()
    {
        var (v1, v2) = CreateTwoVersionsOfOneAssembly();

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(v1, v2), Axes = [Axis(defaultValue: "v2")] });

        AssertWarnings(listener);

        var shared = Page("Lib.Shared.yml").Items[0];
        Assert.Equal(["v1", "v2"], shared.AppliesTo.Select(a => a.Version));
        Assert.Equal(["Version 1", "Version 2"], shared.AppliesTo.Select(a => a.Label));
        Assert.All(shared.AppliesTo, a => Assert.Equal(["net8.0"], a.Frameworks));

        // The default value provides the content, the other one is a variant.
        Assert.Equal("v2", shared.Summary.Trim());
        var variant = Assert.Single(shared.Variants);
        Assert.Equal("v1", Assert.Single(variant.AppliesTo).Version);
        Assert.Equal("v1", variant.Summary.Trim());

        Assert.Equal("v1", Assert.Single(Page("Lib.OnlyV1.yml").Items[0].AppliesTo).Version);
        Assert.Equal("v2", Assert.Single(Page("Lib.OnlyV2.yml").Items[0].AppliesTo).Version);

        // The table of contents names the axis once, for a picker: pages carry only its value ids and labels.
        var toc = YamlUtility.Deserialize<TocItemViewModel>(Path.Combine(_outputFolder, "toc.yml"));
        Assert.Equal(
            """{"name":"Version","values":[{"id":"v1","label":"Version 1"},{"id":"v2","label":"Version 2"}],"default":"v2"}""",
            JsonUtility.Serialize(toc.Metadata["axis"]));
    }

    [Fact]
    public async Task WithoutAnAxisTheTableOfContentsNamesNone()
    {
        var (v1, _) = CreateTwoVersionsOfOneAssembly();

        await Exec(new() { Src = Src(v1), TargetFrameworks = ["*"] });

        var toc = YamlUtility.Deserialize<TocItemViewModel>(Path.Combine(_outputFolder, "toc.yml"));
        Assert.False(toc.Metadata.ContainsKey("axis"));
    }

    [Fact]
    public async Task AProjectThatMatchesNoValueOfTheAxisIsAnError()
    {
        var (v1, v2) = CreateTwoVersionsOfOneAssembly();
        var axis = Axis(defaultValue: "v1");
        axis.Values.RemoveAt(1);

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(v1, v2), Axes = [axis] });

        var error = Assert.Single(listener.GetItemsByLogLevel(LogLevel.Error));
        Assert.Equal(ErrorCodes.Metadata.InvalidAxis, error.Code);
        Assert.Contains("V2.csproj", error.Message);
        Assert.True(File.Exists(Path.Combine(_outputFolder, "Lib.OnlyV1.yml")));
        Assert.False(File.Exists(Path.Combine(_outputFolder, "Lib.OnlyV2.yml")));
    }

    [Fact]
    public async Task AnInvalidAxisIsAnError()
    {
        var (v1, v2) = CreateTwoVersionsOfOneAssembly();

        using var listener = new TestListenerScope();

        await Exec(new() { Src = Src(v1, v2), Axes = [Axis(defaultValue: "v3")] });

        Assert.Contains(listener.GetItemsByLogLevel(LogLevel.Error), x => x.Code == ErrorCodes.Metadata.InvalidAxis && x.Message.Contains("'v3'"));
    }

    private static MetadataAxisConfig Axis(string defaultValue) => new()
    {
        Name = "Version",
        Values =
        [
            new() { Id = "v1", Label = "Version 1", Files = ["**/V1/V1.csproj"] },
            new() { Id = "v2", Label = "Version 2", Files = ["**/V2/V2.csproj"] },
        ],
        Default = defaultValue,
    };

    /// <summary>
    /// Two projects that both build an assembly named <c>Lib</c>, the way one library is built against
    /// two versions of a dependency.
    /// </summary>
    private (string V1, string V2) CreateTwoVersionsOfOneAssembly(string v1Frameworks = null)
    {
        string Create(string name, string frameworks) => CreateProject(
            name,
            frameworks is null
                ? "<TargetFramework>net8.0</TargetFramework><AssemblyName>Lib</AssemblyName>"
                : $"<TargetFrameworks>{frameworks}</TargetFrameworks><AssemblyName>Lib</AssemblyName>",
            $$"""
            namespace Lib
            {
                /// <summary>{{name.ToLowerInvariant()}}</summary>
                public class Shared { }

            #if NETSTANDARD2_0
                /// <summary>{{name.ToLowerInvariant()}} netstandard2.0</summary>
            #else
                /// <summary>{{name.ToLowerInvariant()}}</summary>
            #endif
                public class Only{{name}} { }
            }
            """);

        return (Create("V1", v1Frameworks), Create("V2", null));
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

    private static FileMapping Src(params string[] projects) => new(new FileMappingItem(projects)) { Expanded = true };

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
