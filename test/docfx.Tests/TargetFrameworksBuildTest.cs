// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Docfx.Dotnet;
using Docfx.Tests.Common;
using Newtonsoft.Json.Linq;

namespace Docfx.Tests;

/// <summary>
/// Builds a site from a multi-targeted project. `appliesTo` can be written to the `.yml` faithfully and
/// still render nothing, so what it does is asserted on the HTML and the other files of the site.
/// </summary>
[Collection("docfx STA")]
public class TargetFrameworksBuildTest : TestBase
{
    private const string Both = ".NET 8, .NET Standard 2.0";
    private const string Net8 = ".NET 8";
    private const string Badge = "<span class=\"applies-to ms-2 fs-6 fw-normal text-body-secondary\">{0}</span>";

    private readonly string _projectFolder;

    public TargetFrameworksBuildTest()
    {
        _projectFolder = GetRandomFolder();

        File.WriteAllText(Path.Combine(_projectFolder, "Directory.Build.props"), "<Project></Project>");
        File.WriteAllText(Path.Combine(_projectFolder, "Directory.Packages.props"), "<Project></Project>");

        var folder = Path.Combine(_projectFolder, "Lib");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Lib.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net8.0;netstandard2.0</TargetFrameworks>
                <AssemblyName Condition="'$(TargetFramework)' == 'netstandard2.0'">Lib.Legacy</AssemblyName>
                <GenerateDocumentationFile>true</GenerateDocumentationFile>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(folder, "Lib.cs"), """
            namespace Lib
            {
                public class Base
                {
                    public void Everywhere() { }
            #if NET8_0
                    public void OnlyNet8Base() { }
            #endif
                }

                public class Child : Base { }

                public interface ISource { void Open(); }

                public class AlwaysSource : ISource { public void Open() { } }

                public class SometimesSource
            #if NET8_0
                    : ISource
            #endif
                {
                    public void Open() { }
                }

            #if NET8_0
                public class Switches : Base { }
            #else
                public class Switches { }
            #endif

                public class Shared
                {
                    public int Everywhere { get; set; }
            #if NET8_0
                    public int OnlyNet8Member { get; set; }
                    public void Do(int value) { }
            #else
                    public void Do(string value) { }
            #endif

            #if NET8_0
                    /// <exception cref="System.ArgumentException">Thrown on net8.</exception>
            #else
                    /// <exception cref="System.InvalidOperationException">Thrown on older frameworks.</exception>
            #endif
                    public void Check() { }

            #if NET8_0
                    /// <param name="time">In milliseconds.</param>
            #else
                    /// <param name="time">In seconds.</param>
            #endif
                    public void Wait(int time) { }

            #if NET8_0
                    public const int Limit = 8;
                    public int Size { get; set; }
                    public static readonly int Start = 0;
            #else
                    public const int Limit = 2;
                    public long Size { get; set; }
                    public static readonly long Start = 0;
            #endif
                }
            #if NET8_0
                public class OnlyNet8 { }

                /// <summary>Described for net8.</summary>
                public class Differs
                {
                    public int Value() => 0;
                }
            #else
                /// <summary>Described for older, see <see cref="Shared"/>.</summary>
                public class Differs
                {
                    public long Value() => 0;
                }
            #endif

                public class Described
                {
            #if NET8_0
                    public override string ToString() => "";
            #else
                    public new virtual string ToString() => "";
            #endif
                }

                public enum Mode
                {
                    /// <summary>Plain everywhere.</summary>
                    Plain,
            #if NET8_0
                    /// <summary>Fast on net8 only.</summary>
                    Fast,
                    /// <summary>Strict on net8.</summary>
                    Strict,
            #else
                    /// <summary>Strict on older frameworks.</summary>
                    Strict,
            #endif
                }

            #if NET8_0
                /// <summary>Levels on net8.</summary>
            #else
                /// <summary>Levels on older frameworks.</summary>
            #endif
                public enum Level
                {
                    /// <summary>Low everywhere.</summary>
                    Low,
                    /// <summary>High everywhere.</summary>
                    High,
                }
            }
            """);
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData("\"default\"", "<strong>Applies to</strong>: {0}</h6>")]
    [InlineData("\"default\", \"modern\"", "<dt>Applies to</dt><dd>{0}</dd>")]
    public async Task EachTypeStatesTheFrameworksItExistsIn(string template, string expected)
    {
        var site = await Build(template, targetFrameworks: "\"*\"");

        Assert.Contains(string.Format(expected, Both), await Page(site, "Lib.Shared.html"));
        Assert.Contains(string.Format(expected, Net8), await Page(site, "Lib.OnlyNet8.html"));
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData("\"default\"", "<span class=\"applies-to\">{0}</span>")]
    [InlineData("\"default\", \"modern\"", Badge)]
    public async Task OnlyMembersThatDifferFromTheirTypeAreMarked(string template, string badge)
    {
        var site = await Build(template, targetFrameworks: "\"*\"");
        var page = await Page(site, "Lib.Shared.html");

        Assert.Contains("data-uid=\"Lib.Shared.Everywhere\" data-applies-to=\"net8.0 netstandard2.0\"", page);
        Assert.Contains("data-uid=\"Lib.Shared.OnlyNet8Member\" data-applies-to=\"net8.0\"", page);
        Assert.Contains(string.Format(badge, Net8), page);
        Assert.DoesNotContain(string.Format(badge, Both), page);
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData("\"default\"")]
    [InlineData("\"default\", \"modern\"")]
    public async Task AMemberInheritedInSomeFrameworksIsMarkedWithThem(string template)
    {
        var site = await Build(template, targetFrameworks: "\"*\"");
        var page = await Page(site, "Lib.Child.html");

        Assert.Matches("Base\\.OnlyNet8Base\\(\\)</a>\\s*<span class=\"applies-to[^\"]*\">\\.NET 8</span>\\s*</div>", page);
        Assert.DoesNotMatch("Base\\.Everywhere\\(\\)</a>\\s*<span", page);
        Assert.DoesNotMatch("object\\.ToString\\(\\)</a>\\s*<span", page);
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData("\"default\"")]
    [InlineData("\"default\", \"modern\"")]
    public async Task AClassDerivedInSomeFrameworksIsMarkedWithThem(string template)
    {
        var site = await Build(template, targetFrameworks: "\"*\"");
        var page = await Page(site, "Lib.Base.html");

        Assert.Matches("Switches</a>\\s*<span class=\"applies-to[^\"]*\">\\.NET 8</span>\\s*</div>", page);
        Assert.Matches("Child</a>\\s*</div>", page);
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData("\"default\"")]
    [InlineData("\"default\", \"modern\"")]
    public async Task AnInterfaceListsTheTypesThatImplementItMarkingThoseThatDoInSomeFrameworks(string template)
    {
        var site = await Build(template, targetFrameworks: "\"*\"");
        var page = await Page(site, "Lib.ISource.html");

        Assert.Matches("SometimesSource</a>\\s*<span class=\"applies-to[^\"]*\">\\.NET 8</span>\\s*</div>", page);
        Assert.Matches("AlwaysSource</a>\\s*</div>", page);
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData("\"default\"", true)]
    [InlineData("\"default\", \"modern\"", false)]
    public async Task AVariantThatOverridesNothingSaysSoWhereTheTemplateShowsOverrides(string template, bool showsOverrides)
    {
        var site = await Build(template, targetFrameworks: "\"*\"");
        var page = await Page(site, "Lib.Described.html");

        // the modern template shows no member a member overrides, so a variant shows none either
        Assert.Equal(showsOverrides, Regex.IsMatch(page, "class=\"api-variant\" data-applies-to=\"netstandard2.0\">[\\s\\S]*?<div class=\"api-variant-none\">&mdash;</div>"));
        Assert.Equal(showsOverrides, page.Contains("<h6>Overrides</h6>"));
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData("\"default\"")]
    [InlineData("\"default\", \"modern\"")]
    public async Task AnInterfaceImplementedInSomeFrameworksIsMarkedWithThem(string template)
    {
        var site = await Build(template, targetFrameworks: "\"*\"");

        Assert.Matches("ISource</a>\\s*<span class=\"applies-to[^\"]*\">\\.NET 8</span>\\s*</div>", await Page(site, "Lib.SometimesSource.html"));
        Assert.Matches("ISource</a>\\s*</div>", await Page(site, "Lib.AlwaysSource.html"));
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData("\"default\"")]
    [InlineData("\"default\", \"modern\"")]
    public async Task ATypeBuiltIntoAssembliesOfDifferentNamesListsEach(string template)
    {
        var site = await Build(template, targetFrameworks: "\"*\"");

        Assert.Contains("Lib.dll, Lib.Legacy.dll", await Page(site, "Lib.Shared.html"));
        Assert.DoesNotContain("Lib.Legacy.dll", await Page(site, "Lib.OnlyNet8.html"));
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData("\"default\"")]
    [InlineData("\"default\", \"modern\"")]
    public async Task ListsOfAPageOfItsOwnMarkWhatDiffersFromTheirPage(string template)
    {
        var site = await Build(template, targetFrameworks: "\"*\"", memberLayout: "separatePages");
        const string Marked = "data-applies-to=\"net8\\.0\">\\s*(<dt>)?<a [^>]*>{0}</a> <span class=\"applies-to[^\"]*\">\\.NET 8</span>";

        var type = await Page(site, "Lib.Shared.html");
        Assert.Matches(string.Format(Marked, "OnlyNet8Member"), type);
        Assert.DoesNotMatch("Everywhere</a> <span", type);

        var ns = await Page(site, "Lib.html");
        Assert.Matches(string.Format(Marked, "OnlyNet8"), ns);
        Assert.DoesNotMatch("Shared</a> <span", ns);
    }

    [Fact]
    [Trait("Related", "docfx")]
    public async Task AnOverloadPageAppliesWhereverAnyOverloadDoes()
    {
        var site = await Build("\"default\", \"modern\"", targetFrameworks: "\"*\"", memberLayout: "separatePages");
        var page = await Page(site, "Lib.Shared.Do.html");

        Assert.Contains($"<dt>Applies to</dt><dd>{Both}</dd>", page);
        Assert.Contains(string.Format(Badge, Net8), page);
    }

    [Fact]
    [Trait("Related", "docfx")]
    public async Task AMemberPageOfTheDefaultTemplateMarksAnInterfaceMemberImplementedInSomeFrameworks()
    {
        // The default template lists what a member implements on the page of a method or property group only.
        var site = await Build("\"default\"", targetFrameworks: "\"*\"", memberLayout: "separatePages");

        Assert.Matches("ISource\\.Open\\(\\)</a> <span class=\"applies-to\">\\.NET 8</span></div>", await Page(site, "Lib.SometimesSource.Open.html"));
        Assert.Matches("ISource\\.Open\\(\\)</a></div>", await Page(site, "Lib.AlwaysSource.Open.html"));
    }

    [Fact]
    [Trait("Related", "docfx")]
    public async Task AnOverloadPageOfTheDefaultTemplateMarksItsOverloadsAndShowsTheirVariants()
    {
        // The default template renders the page of a method or property group, unlike that of a field, with
        // `partials/collection`, which shares the member markup of a type page.
        var site = await Build("\"default\"", targetFrameworks: "\"*\"", memberLayout: "separatePages");

        var overloads = await Page(site, "Lib.Shared.Do.html");
        Assert.Contains($"<strong>Applies to</strong>: {Both}</h6>", overloads);
        Assert.Matches("<h4 id=\"[^\"]+\" data-uid=\"Lib\\.Shared\\.Do\\(System\\.Int32\\)\" data-applies-to=\"net8\\.0\">[^<]+ <span class=\"applies-to\">\\.NET 8</span></h4>", overloads);

        var property = await Page(site, "Lib.Shared.Size.html");
        Assert.Contains("class=\"api-variant\" data-applies-to=\"netstandard2.0\"", property);
        Assert.Contains("<h6 class=\"api-variant-label\">.NET Standard 2.0</h6>", property);
        Assert.DoesNotContain("<h5 class=\"implements\">", property);
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData("samePage", "Lib.OnlyNet8")]
    [InlineData("separatePages", "Lib.Shared.OnlyNet8Member*")]
    public async Task TheTableOfContentsCarriesTheFrameworks(string memberLayout, string uid)
    {
        var site = await Build("\"default\", \"modern\"", targetFrameworks: "\"*\"", memberLayout: memberLayout);

        var toc = JObject.Parse(await File.ReadAllTextAsync(Path.Combine(site, "toc.json"), TestContext.Current.CancellationToken));
        var node = toc.Descendants().OfType<JObject>().First(n => (string)n["topicUid"] == uid);

        Assert.Equal(["net8.0"], node["appliesTo"][0]["frameworks"].Values<string>());
    }

    [Fact]
    [Trait("Related", "docfx")]
    public async Task ReferencesCarryTheFrameworksOfTheirTarget()
    {
        var site = await Build("\"default\"", targetFrameworks: "\"*\"", extraBuild: "\"exportRawModel\": true,");

        var raw = JObject.Parse(await File.ReadAllTextAsync(Path.Combine(site, "Lib.raw.json"), TestContext.Current.CancellationToken));
        var child = raw["children"].Single(c => (string)c["uid"] == "Lib.OnlyNet8");

        Assert.Equal(["net8.0"], child["appliesTo"][0]["frameworks"].Values<string>());
    }

    [Fact]
    [Trait("Related", "docfx")]
    public async Task TheSystemKeysListTheKeysAMergedPageAdds()
    {
        var site = await Build("\"default\"", targetFrameworks: "\"*\"", extraBuild: "\"exportRawModel\": true,");

        foreach (var (page, key) in new[] { ("Lib.Child.raw.json", "inheritedMembersAppliesTo"), ("Lib.Base.raw.json", "derivedClassesAppliesTo"), ("Lib.SometimesSource.raw.json", "implementsAppliesTo") })
        {
            var raw = JObject.Parse(await File.ReadAllTextAsync(Path.Combine(site, page), TestContext.Current.CancellationToken));
            Assert.NotNull(raw[key]);
            Assert.Contains(key, raw["_systemKeys"].Values<string>());
        }
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData("\"default\"")]
    [InlineData("\"default\", \"modern\"")]
    public async Task WhatDiffersInAnotherFrameworkIsShownWithItsLabel(string template)
    {
        var site = await Build(template, targetFrameworks: "\"*\"");
        var page = await Page(site, "Lib.Differs.html");

        Assert.Contains("Described for net8.", page);
        Assert.Contains("class=\"api-variant\" data-applies-to=\"netstandard2.0\"", page);
        Assert.Contains("<h6 class=\"api-variant-label\">.NET Standard 2.0</h6>", page);
        Assert.Contains("Described for older", page);

        // The summary went through the same markup and link resolution as the item's own.
        Assert.Contains("href=\"Lib.Shared.html\"", page);

        // A member shows its own difference, and not the one of its type.
        Assert.Contains("public long Value()", page);
        Assert.Equal(1, Regex.Count(page, "Described for older"));

        // What applies elsewhere follows what the page documents, under a heading of its own.
        Assert.Contains("In other frameworks", page);
        Assert.True(page.IndexOf("public int Value()", StringComparison.Ordinal) < page.IndexOf("public long Value()", StringComparison.Ordinal));
        Assert.True(page.IndexOf("Described for net8.", StringComparison.Ordinal) < page.IndexOf("Described for older", StringComparison.Ordinal));
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData("\"default\"")]
    [InlineData("\"default\", \"modern\"")]
    public async Task AVariantShowsWhateverDiffers(string template)
    {
        var site = await Build(template, targetFrameworks: "\"*\"");
        var page = await Page(site, "Lib.Shared.html");

        // Check differs only in the exception it documents, which must not leave an empty variant.
        var variant = Regex.Match(page, "<div class=\"api-variant\" data-applies-to=\"netstandard2.0\">[\\s\\S]*?Thrown on older frameworks\\.").Value;
        Assert.NotEmpty(variant);
        Assert.Contains("InvalidOperationException", variant);
        Assert.Equal(1, Regex.Count(page, "Thrown on net8\\."));
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData("\"default\"")]
    [InlineData("\"default\", \"modern\"")]
    public async Task AnEnumMarksItsValuesAndShowsWhatDiffers(string template)
    {
        var site = await Build(template, targetFrameworks: "\"*\"");
        var page = await Page(site, "Lib.Mode.html");

        // Fast exists on net8 only, so it is marked; Plain is everywhere, so it is not.
        Assert.Matches("id=\"Lib_Mode_Fast\" data-applies-to=\"net8.0\"[^\\n]*>\\.NET 8</span>", page);
        Assert.DoesNotMatch("id=\"Lib_Mode_Plain\"[^\\n]*</span>", page);

        // Strict has another value and summary on netstandard2.0.
        Assert.Contains("class=\"api-variant\" data-applies-to=\"netstandard2.0\"", page);
        Assert.Contains("Strict on older frameworks.", page);
        Assert.Equal(1, Regex.Count(page, "class=\"api-variant\" "));
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData("\"default\"")]
    [InlineData("\"default\", \"modern\"")]
    public async Task AVariantShowsTheValueOfAFieldOrPropertyAsItsOwnSectionDoes(string template)
    {
        var site = await Build(template, targetFrameworks: "\"*\"");
        var page = await Page(site, "Lib.Shared.html");

        // Limit differs in its constant only, so its type is the same everywhere and is not repeated.
        var limit = VariantOf(page, "public const int Limit = 2");
        Assert.DoesNotContain("Field Value", limit);
        Assert.DoesNotContain("Returns", limit);

        var size = VariantOf(page, "public long Size");
        Assert.Contains("Property Value", size);
        Assert.DoesNotContain("Returns", size);

        var start = VariantOf(page, "public static readonly long Start");
        Assert.Contains("Field Value", start);
        Assert.DoesNotContain("Returns", start);
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData("\"default\"")]
    [InlineData("\"default\", \"modern\"")]
    public async Task AVariantOnAPageOfItsOwnNamesTheValueAsThePageDoes(string template)
    {
        var site = await Build(template, targetFrameworks: "\"*\"", memberLayout: "separatePages");
        var page = await Page(site, "Lib.Shared.Start.html");

        // A field with a page of its own shows its value as a return value, as docfx always has, and so does
        // what differs in other frameworks.
        var start = VariantOf(page, "public static readonly long Start");
        Assert.Contains("Returns", start);
        Assert.DoesNotContain("Field Value", page);
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData("\"default\"")]
    [InlineData("\"default\", \"modern\"")]
    public async Task AVariantDoesNotRepeatWhatIsTheSame(string template)
    {
        var site = await Build(template, targetFrameworks: "\"*\"");
        var page = await Page(site, "Lib.Differs.html");

        var variant = Regex.Match(page, "<div class=\"api-variant\"[\\s\\S]*?Described for older[\\s\\S]*?</div>\\s*</div>").Value;
        Assert.DoesNotContain("Described for net8.", variant);

        // Wait differs in the description of its parameter only, so its declaration is not repeated.
        var shared = await Page(site, "Lib.Shared.html");
        var wait = Regex.Match(shared, "<div class=\"api-variant\" data-applies-to=\"netstandard2.0\">(?:(?!<div class=\"api-variant\")[\\s\\S])*?In seconds\\.").Value;
        Assert.NotEmpty(wait);
        Assert.DoesNotContain("public void Wait(int time)", wait);
        Assert.Equal(1, Regex.Count(shared, "In milliseconds\\."));
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData("\"default\"")]
    [InlineData("\"default\", \"modern\"")]
    public async Task NothingIsRenderedWithoutTheOption(string template)
    {
        var site = await Build(template, targetFrameworks: null);
        var page = await Page(site, "Lib.Shared.html");

        Assert.DoesNotContain("Applies to", page);
        Assert.DoesNotContain("data-applies-to", page);
        Assert.DoesNotContain("api-variant", page);

        // an interface lists the types that implement it only with the frameworks they do in
        var interfacePage = await Page(site, "Lib.ISource.html");
        Assert.DoesNotContain("Derived", interfacePage);
        Assert.DoesNotContain("AlwaysSource", interfacePage);
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData("\"default\"")]
    [InlineData("\"default\", \"modern\"")]
    public async Task DisableApiAppliesToHidesOnlyTheLabels(string template)
    {
        var site = await Build(template, targetFrameworks: "\"*\"", extraBuild: "\"globalMetadata\": { \"_disableApiAppliesTo\": true },");
        var page = await Page(site, "Lib.Shared.html");

        Assert.DoesNotContain("Applies to", page);
        Assert.DoesNotContain("class=\"applies-to", page);

        // what a framework picker and a framework PDF read stays, and so does what differs
        Assert.Contains("data-uid=\"Lib.Shared.OnlyNet8Member\" data-applies-to=\"net8.0\"", page);
        var differs = await Page(site, "Lib.Differs.html");
        Assert.Contains("class=\"api-variant\" data-applies-to=\"netstandard2.0\"", differs);

        // an interface still lists the types that implement it, without their marks
        var source = await Page(site, "Lib.ISource.html");
        Assert.Matches("SometimesSource</a>\\s*</div>", source);
        Assert.Matches("AlwaysSource</a>\\s*</div>", source);
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData("\"default\"")]
    [InlineData("\"default\", \"modern\"")]
    public async Task FrameworkNamesMatchAFrameworkInAnyCase(string template)
    {
        var site = await Build(template, targetFrameworks: "\"*\"", extraBuild: "\"globalMetadata\": { \"_frameworkNames\": { \"NetStandard2.0\": \"Any .NET\" } },");

        Assert.Contains($"{Net8}, Any .NET", await Page(site, "Lib.Shared.html"));
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData("\"default\"")]
    [InlineData("\"default\", \"modern\"")]
    public async Task FrameworkNamesReplaceTheLabelsOfFrameworks(string template)
    {
        var site = await Build(template, targetFrameworks: "\"*\"", extraBuild: "\"globalMetadata\": { \"_frameworkNames\": { \"netstandard2.0\": \"Any .NET\" } },");

        Assert.Contains($"{Net8}, Any .NET", await Page(site, "Lib.Shared.html"));
        Assert.Contains("Any .NET", await Page(site, "Lib.Differs.html"));
        Assert.DoesNotContain(".NET Standard 2.0", await Page(site, "Lib.Differs.html"));
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EachVariantIsRenderedAsAWholeForThePickerAndPdf(bool picker)
    {
        var options = "\"globalMetadata\": { \"_frameworkNames\": { \"net8.0\": \"Modern\" }" + (picker ? ", \"_enableApiAppliesToPicker\": true" : "") + " },";
        var site = await Build("\"default\", \"modern\"", targetFrameworks: "\"*\"", extraBuild: options);
        var page = await RawPage(site, "Lib.Shared.html");

        // the dropdowns come with the option only, and label frameworks as the page does
        Assert.Equal(picker, page.Contains("<meta name=\"docfx:appliestopicker\" content=\"true\">"));
        Assert.Equal(picker, Regex.IsMatch(page, "<meta name=\"docfx:frameworknames\" content=\"\\{&quot;net8.0&quot;:&quot;Modern&quot;\\}\">"));

        // a PDF of one framework needs the content of each variant whether the site has the picker or not
        Assert.Contains("<div class=\"api-contents\">", page);
        Assert.Matches("<h1 [^>]*data-applies-to=\"net8.0 netstandard2.0\"", page);

        // Size is a long on .NET Standard 2.0: the whole member as it is there, hidden until that is picked
        var copy = Regex.Match(page, "<div class=\"api-content\" data-applies-to=\"netstandard2.0\" data-search-ignore hidden>(?:(?!<div class=\"api-content)[\\s\\S])*?public long Size(?:(?!<div class=\"api-content|api-variants-heading)[\\s\\S])*").Value;
        Assert.NotEmpty(copy);
        Assert.Contains("Property Value", copy);
        Assert.DoesNotContain("Returns", copy);

        // the API's own content applies to the frameworks no variant does
        Assert.Contains("<div class=\"api-content\" data-applies-to=\"net8.0\">", page);

        // pages other than API pages label frameworks as API pages do, from the table of contents
        var toc = JObject.Parse(await File.ReadAllTextAsync(Path.Combine(site, "toc.json"), TestContext.Current.CancellationToken));
        Assert.Equal(picker ? "Modern" : null, (string)toc["frameworkNames"]?["net8.0"]);
    }

    [Fact]
    [Trait("Related", "docfx")]
    public async Task AnEnumValueHasItsDeclarationAndDescriptionForEachVariant()
    {
        var site = await Build("\"default\", \"modern\"", targetFrameworks: "\"*\"");
        var page = await RawPage(site, "Lib.Mode.html");

        // Strict is 2 on .NET 8 and 1 on .NET Standard 2.0, described differently: the picker swaps both
        Assert.Contains(
            "<span class=\"api-contents\"><span class=\"api-content\" data-applies-to=\"net8.0\"><code>Strict = 2</code></span>" +
            "<span class=\"api-content\" data-applies-to=\"netstandard2.0\" data-search-ignore hidden><code>Strict = 1</code></span></span>",
            page);
        Assert.Matches(
            "<dd><div class=\"api-contents\"><div class=\"api-content\" data-applies-to=\"net8.0\">[^<]*<p>Strict on net8\\.</p>\\s*</div>" +
            "<div class=\"api-content\" data-applies-to=\"netstandard2.0\" data-search-ignore hidden>[^<]*<p>Strict on older frameworks\\.</p>\\s*</div></div>",
            page);

        // a value without variants is as before
        Assert.Matches("<dt id=\"Lib_Mode_Plain\"[^>]*><code>Plain = 0</code></dt>\\s*<dd>[^<]*<p>Plain everywhere\\.</p>\\s*</dd>", page);
    }

    [Fact]
    [Trait("Related", "docfx")]
    public async Task TheCopyOfAnEnumListsItsValuesOnce()
    {
        var site = await Build("\"default\", \"modern\"", targetFrameworks: "\"*\"");
        var page = await RawPage(site, "Lib.Level.html");

        // the enum is described differently on .NET Standard 2.0, so the page has a copy of it for that framework
        var start = page.IndexOf("<div class=\"api-content\" data-applies-to=\"netstandard2.0\" data-search-ignore hidden>", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var copy = page[start..ElementEnd(page, start)];
        Assert.Contains("Levels on older frameworks.", copy);

        // which lists the values as the page does, once each, with ids of its own
        Assert.Equal(1, Regex.Count(copy, "<h2 "));
        Assert.Equal(1, Regex.Count(copy, "<dt id=\"Lib_Level_Low_variant1\""));
        Assert.Equal(1, Regex.Count(copy, "<dt id=\"Lib_Level_High_variant1\""));
        Assert.Equal(1, Regex.Count(page, "id=\"Lib_Level_Low\""));

        // without the variants of the enum under each value
        Assert.Equal(1, Regex.Count(page, "api-variants-heading"));
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData(false, "<strong>Version 1</strong> (.NET 8), <strong>Version 2</strong> (.NET 8)", "<strong>Version 1</strong> (.NET 8)")]
    [InlineData(true, "<strong>Version 1</strong>, <strong>Version 2</strong>", "<strong>Version 1</strong>")]
    public async Task AnAxisIsShownWithTheLabelsOfItsValues(bool hideFrameworks, string appliesTo, string variantLabel)
    {
        var site = await BuildWithAxis(v1Members: "", v2Members: "", hideFrameworks);

        // a table of contents that includes the API one drops the axis of its root, and keeps that of its entries
        var toc = JObject.Parse(await File.ReadAllTextAsync(Path.Combine(_projectFolder, "_site", "toc.json"), TestContext.Current.CancellationToken));
        var api = toc["items"].Single(i => (string)i["name"] == "API");
        Assert.Equal("Version", (string)api["items"].Single()["axis"]["name"]);
        Assert.Equal(["v1", "v2"], api["items"].Single()["axis"]["values"].Select(v => (string)v["id"]));

        var page = await Page(site, "Lib.Widget.html");
        Assert.Contains($"<dt>Applies to</dt><dd>{appliesTo}</dd>", page);
        Assert.Contains("Summary of V2.", page);
        Assert.Contains("class=\"api-variant\" data-applies-to=\"v1:net8.0\"", page);
        Assert.Contains($"<h6 class=\"api-variant-label\">{variantLabel}</h6>", page);
    }

    [Fact]
    [Trait("Related", "docfx")]
    public async Task AnOverloadPageListsTheValuesOfTheAxisInItsOrder()
    {
        // The default value v2 goes first, and its first overload exists in v2 only.
        var site = await BuildWithAxis(
            v1Members: "public void Do(string s) { }",
            v2Members: "public void Do(int i) { } public void Do(string s) { }",
            memberLayout: "separatePages");

        const string Expected = "<dt>Applies to</dt><dd><strong>Version 1</strong> (.NET 8), <strong>Version 2</strong> (.NET 8)</dd>";
        Assert.Contains(Expected, await Page(site, "Lib.Widget.html"));
        Assert.Contains(Expected, await Page(site, "Lib.Widget.Do.html"));
    }

    [Fact]
    [Trait("Related", "docfx")]
    public async Task AVariantShowsTheSeeAlsoLinksItDiffersIn()
    {
        var site = await BuildWithAxis(
            v1Members: "\n/// <seealso cref=\"Widget\"/>\npublic void Related() { }\n",
            v2Members: "\n/// <seealso cref=\"System.Object\"/>\npublic void Related() { }\n");
        var page = await Page(site, "Lib.Widget.html");

        var seeAlso = Regex.Match(page, "data-applies-to=\"v1:net8.0\">(?:(?!class=\"api-variant\")[\\s\\S])*?<h6>See Also</h6>\\s*<div>(.*?)</div>");
        Assert.True(seeAlso.Success, "no See Also in the v1 variant");
        Assert.Contains("Widget", seeAlso.Groups[1].Value);
    }

    [Fact]
    [Trait("Related", "docfx")]
    public async Task AVariantOfAPageDoesNotDescribeAPartWithTheSummaryOfThePage()
    {
        // The delegate is a page of its own, and its undocumented parameter differs in type.
        var site = await BuildWithAxis(
            v1Members: "\n/// <summary>Handles it.</summary>\npublic delegate void Handler(int value);\n",
            v2Members: "\n/// <summary>Handles it.</summary>\npublic delegate void Handler(long value);\n");
        var page = await Page(site, "Lib.Widget.Handler.html");

        var variant = Regex.Match(page, "<div class=\"api-variant\" data-applies-to=\"v1:net8.0\">[\\s\\S]*?</dl>").Value;
        Assert.Contains("int value", variant);
        Assert.DoesNotContain("Handles it.", variant);
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData("\"default\"")]
    [InlineData("\"default\", \"modern\"")]
    public async Task APageOfSeveralAssembliesWithoutFrameworksNamesTheFirstAsBefore(string template)
    {
        // as `docfx merge` writes it, or a hand-written file
        var api = Path.Combine(_projectFolder, "api");
        Directory.CreateDirectory(api);
        await File.WriteAllTextAsync(Path.Combine(api, "Lib.Widget.yml"), """
            ### YamlMime:ManagedReference
            items:
            - uid: Lib.Widget
              id: Widget
              name: Widget
              fullName: Lib.Widget
              type: Class
              assemblies:
              - Lib
              - Lib.Extra
              namespace: Lib
              langs:
              - csharp
            """, TestContext.Current.CancellationToken);
        var configPath = Path.Combine(_projectFolder, "docfx.json");
        await File.WriteAllTextAsync(configPath, $$"""
            {
              "build": {
                "content": [ { "files": [ "api/**.yml" ] } ],
                "template": [ {{template}} ],
                "dest": "_site"
              }
            }
            """, TestContext.Current.CancellationToken);

        await Docset.Build(configPath);

        var page = await Page(Path.Combine(_projectFolder, "_site", "api"), "Lib.Widget.html");
        Assert.Contains("Lib.dll", page);
        Assert.DoesNotContain("Lib.Extra.dll", page);
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData("\"default\"")]
    [InlineData("\"default\", \"modern\"")]
    public async Task AFrameworkOfAPlatformIsNamedWithItsPlatform(string template)
    {
        var api = Path.Combine(_projectFolder, "api");
        Directory.CreateDirectory(api);
        await File.WriteAllTextAsync(Path.Combine(api, "Lib.Widget.yml"), """
            ### YamlMime:ManagedReference
            items:
            - uid: Lib.Widget
              id: Widget
              name: Widget
              fullName: Lib.Widget
              type: Class
              namespace: Lib
              langs:
              - csharp
              appliesTo:
              - frameworks:
                - net8.0-windows10.0.19041.0
                - net8.0-windows
                - net8.0-android34.0
                - net9.0-browser
                - net8.0-ios
                - net8.0-maccatalyst
                - net10.0-futureos2.0
                - net8.0-tizen
            """, TestContext.Current.CancellationToken);
        var configPath = Path.Combine(_projectFolder, "docfx.json");
        await File.WriteAllTextAsync(configPath, $$"""
            {
              "build": {
                "content": [ { "files": [ "api/**.yml" ] } ],
                "template": [ {{template}} ],
                "globalMetadata": { "_frameworkNames": { "net8.0-tizen": "Tizen TV" } },
                "dest": "_site"
              }
            }
            """, TestContext.Current.CancellationToken);

        await Docset.Build(configPath);

        var page = await Page(Path.Combine(_projectFolder, "_site", "api"), "Lib.Widget.html");
        Assert.Contains(
            ".NET 8 for Windows 10.0.19041.0, .NET 8 for Windows, .NET 8 for Android 34.0, .NET 9 for Browser, .NET 8 for iOS, "
            + ".NET 8 for Mac Catalyst, .NET 10 for Futureos 2.0, Tizen TV",
            page);
    }

    /// <returns>The folder of the generated API pages of a <c>Widget</c> built against two versions.</returns>
    private async Task<string> BuildWithAxis(string v1Members, string v2Members, bool hideFrameworks = false, string memberLayout = "samePage")
    {
        foreach (var (version, members) in new[] { ("V1", v1Members), ("V2", v2Members) })
        {
            var folder = Path.Combine(_projectFolder, version);
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, $"{version}.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <AssemblyName>Lib</AssemblyName>
                    <GenerateDocumentationFile>true</GenerateDocumentationFile>
                  </PropertyGroup>
                </Project>
                """, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(folder, "Lib.cs"), $$"""
                namespace Lib
                {
                    /// <summary>Summary of {{version}}.</summary>
                    public class Widget { {{members}} }
                }
                """, TestContext.Current.CancellationToken);
        }

        var configPath = Path.Combine(_projectFolder, "docfx.json");
        await File.WriteAllTextAsync(configPath, $$"""
            {
              "metadata": [
                {
                  "src": [ { "files": [ "V1/V1.csproj", "V2/V2.csproj" ] } ],
                  "axes": [
                    {
                      "name": "Version",
                      "values": [
                        { "id": "v1", "label": "Version 1", "files": [ "V1/*.csproj" ] },
                        { "id": "v2", "label": "Version 2", "files": [ "V2/*.csproj" ] }
                      ],
                      "default": "v2"
                    }
                  ],
                  "memberLayout": "{{memberLayout}}",
                  "dest": "api"
                }
              ],
              "build": {
                "content": [ { "files": [ "api/**.yml", "toc.yml" ] } ],
                "template": [ "default", "modern" ],
                "globalMetadata": { "_disableApiAppliesToFrameworks": {{(hideFrameworks ? "true" : "false")}} },
                "dest": "_site"
              }
            }
            """, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_projectFolder, "toc.yml"), """
            - name: API
              href: api/toc.yml
            """, TestContext.Current.CancellationToken);

        await DotnetApiCatalog.GenerateManagedReferenceYamlFiles(configPath);
        await Docset.Build(configPath);

        return Path.Combine(_projectFolder, "_site", "api");
    }

    /// <returns>The page as a reader sees it, without the content kept hidden for the picker and PDFs.</returns>
    private static async Task<string> Page(string site, string name) => WithoutHiddenContent(await RawPage(site, name));

    private static Task<string> RawPage(string site, string name) => File.ReadAllTextAsync(Path.Combine(site, name));

    private static string WithoutHiddenContent(string page)
    {
        var hidden = new Regex("<div class=\"api-content\" data-applies-to=\"[^\"]*\" data-search-ignore hidden>");
        for (var match = hidden.Match(page); match.Success; match = hidden.Match(page))
        {
            page = page[..match.Index] + page[ElementEnd(page, match.Index)..];
        }

        return page;
    }

    /// <returns>The end of the &lt;div&gt; at <paramref name="start"/>: after the &lt;/div&gt; that balances it.</returns>
    private static int ElementEnd(string page, int start)
    {
        var depth = 1;
        var end = start + "<div".Length;
        while (depth > 0)
        {
            var open = page.IndexOf("<div", end, StringComparison.Ordinal);
            var close = page.IndexOf("</div>", end, StringComparison.Ordinal);
            if (open >= 0 && open < close)
            {
                depth++;
                end = open + "<div".Length;
            }
            else
            {
                depth--;
                end = close + "</div>".Length;
            }
        }

        return end;
    }

    /// <returns>The variant with this declaration, up to the heading of the next member.</returns>
    private static string VariantOf(string page, string declaration)
    {
        var start = page.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no {declaration}");
        var label = page.LastIndexOf("<div class=\"api-variant\"", start, StringComparison.Ordinal);
        Assert.True(label >= 0, $"{declaration} is not in a variant");
        var next = Regex.Match(page[start..], "<h[1-4][\\s>]");
        return page[label..(next.Success ? start + next.Index : page.Length)];
    }

    /// <returns>The folder of the generated API pages.</returns>
    private async Task<string> Build(string template, string targetFrameworks, string memberLayout = "samePage", string extraBuild = "")
    {
        var option = targetFrameworks is null ? "" : $"\"targetFrameworks\": {targetFrameworks},";
        var configPath = Path.Combine(_projectFolder, "docfx.json");
        await File.WriteAllTextAsync(configPath, $$"""
            {
              "metadata": [
                {
                  "src": [ { "files": [ "Lib/Lib.csproj" ] } ],
                  {{option}}
                  "memberLayout": "{{memberLayout}}",
                  "dest": "api"
                }
              ],
              "build": {
                "content": [ { "files": [ "api/**.yml" ] } ],
                "template": [ {{template}} ],
                {{extraBuild}}
                "dest": "_site"
              }
            }
            """);

        await DotnetApiCatalog.GenerateManagedReferenceYamlFiles(configPath);
        await Docset.Build(configPath);

        return Path.Combine(_projectFolder, "_site", "api");
    }
}
