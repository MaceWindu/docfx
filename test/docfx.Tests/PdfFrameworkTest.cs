// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
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
        Assert.Equal("""window.docfxAppliesTo = {"framework":"net462","value":null,"axisValues":null,"otherAxisPages":null};""", PdfBuilder.GetAppliesToScript(Parse("net462", null)));
        Assert.Equal("""window.docfxAppliesTo = {"framework":null,"value":"ef9","axisValues":null,"otherAxisPages":null};""", PdfBuilder.GetAppliesToScript(Parse(null, "ef9")));

        // pages carry value ids without their axis, so they are told the values of the axis picked from, and which
        // pages are of another axis, which the value says nothing about, as their own values may have the same ids
        Assert.Equal(
            """window.docfxAppliesTo = {"framework":null,"value":"d1","axisValues":["d1","d2"],"otherAxisPages":["/api/ef/Everywhere.html","/api/ef/FromEF8.html"]};""",
            PdfBuilder.GetAppliesToScript(TwoAxes("d1"), "api/toc.json"));
    }

    // The API of two metadata items, each with an axis of its own, which `docfx metadata` writes to their entries.
    private const string TwoAxesToc = """
        {
          "pdf": true,
          "pdfAxisValue": "{value}",
          "items": [
            { "name": "EF", "axis": { "name": "EF Core", "values": [ { "id": "ef3" }, { "id": "ef8" } ] }, "items": [
              { "name": "Everywhere", "href": "ef/Everywhere.html", "appliesTo": [ { "version": "ef3", "frameworks": [ "netstandard2.0" ] }, { "version": "ef8", "frameworks": [ "net8.0" ] } ] },
              { "name": "FromEF8", "href": "ef/FromEF8.html", "appliesTo": [ { "version": "ef8", "frameworks": [ "net8.0" ] } ] }
            ] },
            { "name": "Drivers", "axis": { "name": "Driver", "values": [ { "id": "d1" }, { "id": "d2" } ] }, "items": [
              { "name": "OnlyD1", "href": "drivers/OnlyD1.html", "appliesTo": [ { "version": "d1", "frameworks": [ "net8.0" ] } ] },
              { "name": "OnlyD2", "href": "drivers/OnlyD2.html", "appliesTo": [ { "version": "d2", "frameworks": [ "net8.0" ] } ] }
            ] }
          ]
        }
        """;

    [Theory]
    // a value of one axis says nothing about the API of the other
    [InlineData("ef3", "EF, Everywhere, Drivers, OnlyD1, OnlyD2")]
    [InlineData("d2", "EF, Everywhere, FromEF8, Drivers, OnlyD2")]
    public void AnAxisValueLeavesOutOnlyWhatItsOwnAxisDoesNotHave(string value, string expected)
    {
        using var listener = new TestListenerScope();
        var outline = TwoAxes(value);

        PdfBuilder.LeaveOutUnavailable(outline);

        Assert.Equal(expected, string.Join(", ", Names(outline.items)));
        Assert.Empty(listener.GetItemsByLogLevel(LogLevel.Warning));
    }

    [Fact]
    public void AValueOfNoAxisOfTheTableOfContentsWarns()
    {
        using var listener = new TestListenerScope();

        PdfBuilder.LeaveOutUnavailable(TwoAxes("ef9"));

        Assert.Contains(listener.GetItemsByLogLevel(LogLevel.Warning), x => x.Message.Contains("'pdfAxisValue'"));
    }

    [Fact]
    public void AValueOfTwoAxesLeavesOutWhatEachLacksAndWarns()
    {
        using var listener = new TestListenerScope();
        // the drivers have a value `ef8` too
        var outline = JsonSerializer.Deserialize<PdfBuilder.Outline>(TwoAxesToc
            .Replace("{ \"id\": \"d1\" }", "{ \"id\": \"ef8\" }")
            .Replace("\"version\": \"d1\"", "\"version\": \"ef8\"")
            .Replace("{value}", "ef8"));

        PdfBuilder.LeaveOutUnavailable(outline);

        Assert.Equal("EF, Everywhere, FromEF8, Drivers, OnlyD1", string.Join(", ", Names(outline.items)));
        var warning = Assert.Single(listener.GetItemsByLogLevel(LogLevel.Warning));
        Assert.Contains("'EF Core', 'Driver'", warning.Message);
    }

    private static PdfBuilder.Outline TwoAxes(string value) =>
        JsonSerializer.Deserialize<PdfBuilder.Outline>(TwoAxesToc.Replace("{value}", value));

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
    public void AFrameworkAndValueThatNeverMeetWarn()
    {
        using var listener = new TestListenerScope();

        // .NET Standard 2.0 and EF Core 8 each exist, but EF Core 8 builds for .NET 8 only
        PdfBuilder.LeaveOutUnavailable(Parse("netstandard2.0", "ef8"));

        var warning = Assert.Single(listener.GetItemsByLogLevel(LogLevel.Warning));
        Assert.Contains("none of it exists in both", warning.Message);
    }

    [Fact]
    public void AFrameworkAndValueThatNeverMeetWarnForTheAxisOfTheValue()
    {
        using var listener = new TestListenerScope();
        var outline = JsonSerializer.Deserialize<PdfBuilder.Outline>(TwoAxesToc
            .Replace("\"pdf\": true,", "\"pdf\": true, \"pdfFramework\": \"netstandard2.0\",")
            .Replace("{value}", "ef8"));

        PdfBuilder.LeaveOutUnavailable(outline);

        // the drivers, built for .NET 8 only, have no EF Core 8 to leave out
        var warning = Assert.Single(listener.GetItemsByLogLevel(LogLevel.Warning));
        Assert.Contains("axis 'EF Core'", warning.Message);
    }

    [Fact]
    public void OnlyATableOfContentsOfAPdfIsReadWhole()
    {
        using var listener = new TestListenerScope();
        // `appliesTo` and `axis` written by hand for something else
        var handWritten = """{ "items": [ { "name": "A", "href": "a.html", "appliesTo": "net8.0", "axis": "x" } ] }"""u8.ToArray();

        Assert.Null(PdfBuilder.ReadPdfOutline(handWritten, "toc.json"));
        Assert.Null(PdfBuilder.ReadPdfOutline("""{ "pdf": false, "appliesTo": 1 }"""u8.ToArray(), "toc.json"));
        Assert.Empty(listener.GetItemsByLogLevel(LogLevel.Error));

        Assert.Equal("Lib", PdfBuilder.ReadPdfOutline(Encoding.UTF8.GetBytes(Json(null, null)), "toc.json")?.items?[1].name);

        // nor in that of a PDF that selects nothing, which does not read them
        var unselected = PdfBuilder.ReadPdfOutline("""{ "pdf": true, "pdfFramework": " ", "items": [ { "name": "A", "appliesTo": "net8.0", "axis": "x", "items": [ { "name": "B", "axis": 1 } ] } ] }"""u8.ToArray(), "toc.json");
        Assert.Equal("A, B", string.Join(", ", Names(unselected?.items)));
        Assert.Null(unselected.items[0].appliesTo);
        Assert.Empty(listener.GetItemsByLogLevel(LogLevel.Error));

        // in the table of contents of a PDF of one framework, it says which one cannot be read, and does not stop the others
        Assert.Null(PdfBuilder.ReadPdfOutline("""{ "pdf": true, "pdfFramework": "net8.0", "items": [ { "name": "A", "appliesTo": "net8.0" } ] }"""u8.ToArray(), "api/toc.json"));
        Assert.Contains(listener.GetItemsByLogLevel(LogLevel.Error), x => x.Message.Contains("'api/toc.json'"));

        // nor does one that is not JSON at all
        Assert.Null(PdfBuilder.ReadPdfOutline("""{ "pdf": true, """u8.ToArray(), "broken/toc.json"));
        Assert.Contains(listener.GetItemsByLogLevel(LogLevel.Error), x => x.Message.Contains("'broken/toc.json'"));
        // the error count is global, and later tests check it
        Logger.ResetCount();
    }

    [Theory]
    [InlineData("unavailable", false, "Unavailable")]
    [InlineData("error:broken page", false, "Failed")]
    [InlineData("unapplied", false, "NotApplied")]
    // a page that timed out may not have run the script of its template yet, which says nothing of the template
    [InlineData("unapplied", true, "TimedOut")]
    [InlineData("", false, "None")]
    [InlineData("", true, "None")]
    public void APageThatDidNotApplyTheSelectionWarnsWhy(string state, bool timedOut, string expected)
    {
        Assert.Equal(expected, PdfBuilder.GetPrintSelectionProblem(state, timedOut).ToString());
    }

    [Fact]
    public void AnOptionThatIsNotTextDoesNotStopThePdf()
    {
        using var listener = new TestListenerScope();

        // empty: selects nothing, even with `appliesTo` written by hand for something else
        var empty = PdfBuilder.ReadPdfOutline("""{ "pdf": true, "pdfFramework": null, "items": [ { "name": "A", "appliesTo": "net8.0" } ] }"""u8.ToArray(), "toc.json");
        Assert.Equal("A", string.Join(", ", Names(empty?.items)));
        Assert.Null(PdfBuilder.GetAppliesToScript(empty));

        Assert.Empty(listener.GetItemsByLogLevel(LogLevel.Warning));

        // a number, which the build would have made text, is the value it spells, and says so
        var number = PdfBuilder.ReadPdfOutline("""{ "pdf": true, "pdfAxisValue": 8, "items": [ { "name": "A", "appliesTo": [ { "version": "8", "frameworks": [ "net8.0" ] } ] } ] }"""u8.ToArray(), "toc.json");
        Assert.Equal("8", number?.pdfAxisValue);
        // as it is written in the table of contents, with its zeros
        Assert.Equal("1.10", PdfBuilder.ReadPdfOutline("""{ "pdf": true, "pdfAxisValue": 1.10 }"""u8.ToArray(), "toc.json")?.pdfAxisValue);
        var numbers = listener.GetItemsByLogLevel(LogLevel.Warning).ToList();
        Assert.Equal(2, numbers.Count);
        Assert.All(numbers, w => Assert.Equal(WarningCodes.Build.UnquotedTocValue, w.Code));
        Assert.Contains(numbers, w => w.Message.StartsWith("'pdfAxisValue' of table of contents 'toc.json' is the number 1.10"));

        // anything else is ignored, and says so
        var other = PdfBuilder.ReadPdfOutline("""{ "pdf": true, "pdfFramework": true, "pdfAxisValue": [ "ef8" ], "items": [ { "name": "A", "appliesTo": "net8.0" } ] }"""u8.ToArray(), "api/toc.json");
        Assert.Equal("A", string.Join(", ", Names(other?.items)));
        Assert.Null(PdfBuilder.GetAppliesToScript(other));
        var warnings = listener.GetItemsByLogLevel(LogLevel.Warning).Select(w => w.Message).ToList();
        Assert.Contains(warnings, w => w.Contains("'pdfFramework' of table of contents 'api/toc.json'"));
        Assert.Contains(warnings, w => w.Contains("'pdfAxisValue' of table of contents 'api/toc.json'"));
        Assert.Empty(listener.GetItemsByLogLevel(LogLevel.Error));
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

    [Fact]
    public void ANamespaceTheSelectionLeavesOutIsTheHeadingOfTheNamespacesInItThatItKeeps()
    {
        // `namespaceLayout: nested`: the types of Lib are on .NET 8 only, those of Lib.Data.Ef everywhere, and Lib.Data
        // has no types, so no frameworks, of its own
        var outline = JsonSerializer.Deserialize<PdfBuilder.Outline>("""
            {
              "pdf": true,
              "pdfFramework": "netstandard2.0",
              "items": [
                { "name": "Lib", "href": "Lib.html", "appliesTo": [ { "frameworks": [ "net8.0" ] } ], "items": [
                  { "name": "Span", "href": "Lib.Span.html", "appliesTo": [ { "frameworks": [ "net8.0" ] } ] },
                  { "name": "Data", "items": [
                    { "name": "Ef", "href": "Lib.Data.Ef.html", "appliesTo": [ { "frameworks": [ "net8.0", "netstandard2.0" ] } ] }
                  ] },
                  { "name": "Guide", "href": "guide.html" }
                ] },
                { "name": "Modern", "href": "Modern.html", "appliesTo": [ { "frameworks": [ "net8.0" ] } ], "items": [
                  { "name": "Guide", "href": "modern.html" }
                ] }
              ]
            }
            """);

        PdfBuilder.LeaveOutUnavailable(outline);

        // without its page; an item without frameworks of its own, such as an article, does not keep it
        Assert.Equal("Lib, Data, Ef, Guide", string.Join(", ", Names(outline.items)));
        Assert.Null(outline.items[0].href);
    }

    [Fact]
    public void AWarningNamesTheTableOfContentsOfAPdfWithoutAFileName()
    {
        using var listener = new TestListenerScope();

        PdfBuilder.LeaveOutUnavailable(PdfBuilder.ReadPdfOutline(Encoding.UTF8.GetBytes(Json("net8", null)), "api/toc.json"));

        Assert.Contains(listener.GetItemsByLogLevel(LogLevel.Warning), x => x.Message.Contains("'pdfFramework' of 'api/toc.json'"));
    }

    private static PdfBuilder.Outline Parse(string framework, string value) =>
        JsonSerializer.Deserialize<PdfBuilder.Outline>(Json(framework, value));

    private static string Json(string framework, string value) => Toc
        .Replace("\"{framework}\"", framework is null ? "null" : $"\"{framework}\"")
        .Replace("\"{value}\"", value is null ? "null" : $"\"{value}\"");

    private static IEnumerable<string> Names(IEnumerable<PdfBuilder.Outline> items) =>
        items?.SelectMany(i => Names(i.items).Prepend(i.name)) ?? [];
}
