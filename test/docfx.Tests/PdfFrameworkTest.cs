// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Docfx.Common;
using Docfx.Pdf;
using Docfx.Tests.Common;

namespace Docfx.Tests;

/// <summary>
/// A PDF of one framework or axis value: which pages it leaves out, and what its pages are told to show.
/// </summary>
[Collection("docfx STA")]
public class PdfFrameworkTest
{
    // A PDF table of contents over API pages, as `toc.json` carries it after a build with `targetFrameworks`.
    private const string Toc = """
        {
          "pdf": true,
          "pdfFramework": "{framework}",
          "pdfAxisValue": "{value}",
          "items": [
            { "name": "Getting started", "href": "index.html" },
            { "name": "Lib", "href": "api/Lib.html", "appliesTo": [ { "frameworks": [ "net8.0", "net462" ] } ], "items": [
              { "name": "Everywhere", "href": "api/Lib.Everywhere.html", "appliesTo": [ { "frameworks": [ "net8.0", "net462" ] } ] },
              { "name": "OnlyNet8", "href": "api/Lib.OnlyNet8.html", "appliesTo": [ { "frameworks": [ "net8.0" ] } ] },
              { "name": "Standard", "href": "api/Lib.Standard.html", "appliesTo": [ { "frameworks": [ "netstandard2.0" ] } ] }
            ] },
            { "name": "Lib.EF", "href": "api/Lib.EF.html", "appliesTo": [ { "version": "ef3", "frameworks": [ "netstandard2.0" ] }, { "version": "ef8", "frameworks": [ "net8.0" ] } ], "items": [
              { "name": "FromEF8", "href": "api/Lib.EF.FromEF8.html", "appliesTo": [ { "version": "ef8", "frameworks": [ "net8.0" ] } ] }
            ] }
          ]
        }
        """;

    [Theory]
    [InlineData(null, null, "Getting started, Lib, Everywhere, OnlyNet8, Standard, Lib.EF, FromEF8")]
    // .NET Standard 2.0 stands in for .NET 10, .NET Framework 4.7.2 is served by 4.6.2 and .NET Standard 2.0
    [InlineData("net10.0", null, "Getting started, Lib, Everywhere, OnlyNet8, Standard, Lib.EF, FromEF8")]
    [InlineData("net472", null, "Getting started, Lib, Everywhere, Standard, Lib.EF")]
    [InlineData("net462", null, "Getting started, Lib, Everywhere, Standard, Lib.EF")]
    // an axis value leaves out what other values have, and says nothing about APIs without an axis
    [InlineData(null, "ef3", "Getting started, Lib, Everywhere, OnlyNet8, Standard, Lib.EF")]
    [InlineData("net8.0", "ef3", "Getting started, Lib, Everywhere, OnlyNet8, Standard, Lib.EF")]
    [InlineData("net462", "ef8", "Getting started, Lib, Everywhere, Standard")]
    public void APdfOfOneFrameworkLeavesOutWhatDoesNotExistInIt(string framework, string value, string expected)
    {
        var outline = Parse(framework, value);

        PdfBuilder.LeaveOutUnavailable(outline);

        Assert.Equal(expected, string.Join(", ", Names(outline.items)));
    }

    [Fact]
    public void ThePagesOfAPdfOfOneFrameworkAreToldWhichOne()
    {
        Assert.Null(PdfBuilder.GetAppliesToScript(Parse(null, null)));
        Assert.Equal("""window.docfxAppliesTo = {"framework":"net462","value":null};""", PdfBuilder.GetAppliesToScript(Parse("net462", null)));
        Assert.Equal("""window.docfxAppliesTo = {"framework":null,"value":"ef9"};""", PdfBuilder.GetAppliesToScript(Parse(null, "ef9")));
    }

    [Fact]
    public void AnEmptyFrameworkOrValueSelectsNothing()
    {
        var outline = Parse("", " ");

        PdfBuilder.LeaveOutUnavailable(outline);

        Assert.Equal("Getting started, Lib, Everywhere, OnlyNet8, Standard, Lib.EF, FromEF8", string.Join(", ", Names(outline.items)));
        Assert.Null(PdfBuilder.GetAppliesToScript(outline));
    }

    [Theory]
    [InlineData("net8", null, "'pdfFramework'")]
    [InlineData(null, "EF8", "'pdfAxisValue'")]
    public void AFrameworkOrValueNothingAppliesToWarns(string framework, string value, string option)
    {
        using var listener = new TestListenerScope();

        PdfBuilder.LeaveOutUnavailable(Parse(framework, value));

        Assert.Contains(listener.GetItemsByLogLevel(LogLevel.Warning), x => x.Message.Contains(option));
    }

    [Fact]
    public void AHeadingWhoseItemsAreAllLeftOutGoesToo()
    {
        var outline = JsonSerializer.Deserialize<PdfBuilder.Outline>("""
            {
              "pdf": true,
              "pdfFramework": "net462",
              "items": [
                { "name": "Kept", "items": [ { "name": "Everywhere", "href": "a.html", "appliesTo": [ { "frameworks": [ "net8.0", "net462" ] } ] } ] },
                { "name": "Emptied", "items": [ { "name": "OnlyNet8", "href": "b.html", "appliesTo": [ { "frameworks": [ "net8.0" ] } ] } ] },
                { "name": "Empty from the start", "items": [] }
              ]
            }
            """);

        PdfBuilder.LeaveOutUnavailable(outline);

        Assert.Equal("Kept, Everywhere, Empty from the start", string.Join(", ", Names(outline.items)));
    }

    private static PdfBuilder.Outline Parse(string framework, string value)
    {
        var json = Toc
            .Replace("\"{framework}\"", framework is null ? "null" : $"\"{framework}\"")
            .Replace("\"{value}\"", value is null ? "null" : $"\"{value}\"");
        return JsonSerializer.Deserialize<PdfBuilder.Outline>(json);
    }

    private static IEnumerable<string> Names(IEnumerable<PdfBuilder.Outline> items) =>
        items?.SelectMany(i => Names(i.items).Prepend(i.name)) ?? [];
}
