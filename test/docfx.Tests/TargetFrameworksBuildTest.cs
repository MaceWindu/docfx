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
                <GenerateDocumentationFile>true</GenerateDocumentationFile>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(folder, "Lib.cs"), """
            namespace Lib
            {
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
                    public const int Limit = 8;
                    public int Size { get; set; }
            #else
                    public const int Limit = 2;
                    public long Size { get; set; }
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

    [Fact]
    [Trait("Related", "docfx")]
    public async Task OnlyMembersThatDifferFromTheirTypeAreMarked()
    {
        var site = await Build("\"default\", \"modern\"", targetFrameworks: "\"*\"");
        var page = await Page(site, "Lib.Shared.html");

        Assert.Contains("data-uid=\"Lib.Shared.Everywhere\" data-applies-to=\"net8.0 netstandard2.0\"", page);
        Assert.Contains("data-uid=\"Lib.Shared.OnlyNet8Member\" data-applies-to=\"net8.0\"", page);
        Assert.Contains(string.Format(Badge, Net8), page);
        Assert.DoesNotContain(string.Format(Badge, Both), page);
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
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData("\"default\"")]
    [InlineData("\"default\", \"modern\"")]
    public async Task DisableApiAppliesToRemovesItFromThePage(string template)
    {
        var site = await Build(template, targetFrameworks: "\"*\"", extraBuild: "\"globalMetadata\": { \"_disableApiAppliesTo\": true },");
        var page = await Page(site, "Lib.Shared.html");

        Assert.DoesNotContain("Applies to", page);
        Assert.DoesNotContain("data-applies-to", page);
        Assert.DoesNotContain("class=\"applies-to", page);

        var differs = await Page(site, "Lib.Differs.html");
        Assert.DoesNotContain("api-variant", differs);
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
        var copy = Regex.Match(page, "<div class=\"api-content\" data-applies-to=\"netstandard2.0\" hidden>(?:(?!<div class=\"api-content)[\\s\\S])*?public long Size(?:(?!<div class=\"api-content|api-variants-heading)[\\s\\S])*").Value;
        Assert.NotEmpty(copy);
        Assert.Contains("Property Value", copy);
        Assert.DoesNotContain("Returns", copy);

        // the API's own content applies to the frameworks no variant does
        Assert.Contains("<div class=\"api-content\" data-applies-to=\"net8.0\">", page);
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData(false, "<strong>Version 1</strong> (.NET 8), <strong>Version 2</strong> (.NET 8)", "<strong>Version 1</strong> (.NET 8)")]
    [InlineData(true, "<strong>Version 1</strong>, <strong>Version 2</strong>", "<strong>Version 1</strong>")]
    public async Task AnAxisIsShownWithTheLabelsOfItsValues(bool hideFrameworks, string appliesTo, string variantLabel)
    {
        foreach (var version in new[] { "V1", "V2" })
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
                    public class Widget { }
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
                  "dest": "api"
                }
              ],
              "build": {
                "content": [ { "files": [ "api/**.yml" ] } ],
                "template": [ "default", "modern" ],
                "globalMetadata": { "_disableApiAppliesToFrameworks": {{(hideFrameworks ? "true" : "false")}} },
                "dest": "_site"
              }
            }
            """, TestContext.Current.CancellationToken);

        await DotnetApiCatalog.GenerateManagedReferenceYamlFiles(configPath);
        await Docset.Build(configPath);

        var page = await Page(Path.Combine(_projectFolder, "_site", "api"), "Lib.Widget.html");
        Assert.Contains($"<dt>Applies to</dt><dd>{appliesTo}</dd>", page);
        Assert.Contains("Summary of V2.", page);
        Assert.Contains("class=\"api-variant\" data-applies-to=\"v1:net8.0\"", page);
        Assert.Contains($"<h6 class=\"api-variant-label\">{variantLabel}</h6>", page);
    }

    /// <returns>The page as a reader sees it, without the content kept hidden for the picker and PDFs.</returns>
    private static async Task<string> Page(string site, string name) => WithoutHiddenContent(await RawPage(site, name));

    private static Task<string> RawPage(string site, string name) => File.ReadAllTextAsync(Path.Combine(site, name));

    private static string WithoutHiddenContent(string page)
    {
        var hidden = new Regex("<div class=\"api-content\" data-applies-to=\"[^\"]*\" hidden>");
        for (var match = hidden.Match(page); match.Success; match = hidden.Match(page))
        {
            // the copy ends at the </div> that balances its <div>
            var depth = 1;
            var end = match.Index + match.Length;
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
            page = page[..match.Index] + page[end..];
        }

        return page;
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
