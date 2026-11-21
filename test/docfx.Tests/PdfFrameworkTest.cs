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
    public void AValueOfNoAxisOfTheTableOfContentsLeavesNothingOut()
    {
        using var listener = new TestListenerScope();
        // an entry without an axis, written by hand with the value of an axis, next to the API of an axis
        var outline = JsonSerializer.Deserialize<PdfBuilder.Outline>(TwoAxesToc
            .Replace("{ \"name\": \"EF\", ", "{ \"name\": \"Old\", \"href\": \"old.html\", \"appliesTo\": [ { \"version\": \"ef3\", \"frameworks\": [ \"net8.0\" ] } ] }, { \"name\": \"EF\", ")
            .Replace("{value}", "ef9"));
        Assert.Equal("Old", outline.items[0].name);

        PdfBuilder.LeaveOutUnavailable(outline);

        // as on its pages, which are told the values of no axis
        Assert.Equal("Old, EF, Everywhere, FromEF8, Drivers, OnlyD1, OnlyD2", string.Join(", ", Names(outline.items)));
        Assert.Contains("\"axisValues\":[]", PdfBuilder.GetAppliesToScript(outline));
    }

    [Fact]
    public void AValueSaysNothingAboutAnEntryOfOtherValuesWithoutAnAxis()
    {
        // an entry without an axis, written by hand with values that are no axis's, next to the API of an axis
        var outline = JsonSerializer.Deserialize<PdfBuilder.Outline>(TwoAxesToc
            .Replace("{ \"name\": \"EF\", ", "{ \"name\": \"Old\", \"href\": \"old.html\", \"appliesTo\": [ { \"version\": \"old1\", \"frameworks\": [ \"net8.0\" ] } ] }, { \"name\": \"EF\", ")
            .Replace("{value}", "ef3"));

        PdfBuilder.LeaveOutUnavailable(outline);

        // kept, as its page prints whole: it is told the values of the axis of `ef3`, which `old1` is not one of
        Assert.Equal("Old, EF, Everywhere, Drivers, OnlyD1, OnlyD2", string.Join(", ", Names(outline.items)));
        Assert.Contains("\"axisValues\":[\"ef3\",\"ef8\"]", PdfBuilder.GetAppliesToScript(outline));
    }

    [Fact]
    public void AHeadingWithAnEmptyHrefGoesWithItsItems()
    {
        var outline = JsonSerializer.Deserialize<PdfBuilder.Outline>("""
            { "pdf": true, "pdfFramework": "net462", "items": [
              { "name": "Group", "href": "", "items": [ { "name": "OnlyNet8", "href": "a.html", "appliesTo": [ { "frameworks": [ "net8.0" ] } ] } ] },
              { "name": "Everywhere", "href": "b.html", "appliesTo": [ { "frameworks": [ "net8.0", "net462" ] } ] }
            ] }
            """);

        PdfBuilder.LeaveOutUnavailable(outline);

        Assert.Equal("Everywhere", string.Join(", ", Names(outline.items)));
    }

    [Theory]
    [InlineData("""[ { "name": "A", "href": "a.html", "appliesTo": [ null ] } ]""")]
    [InlineData("""[ { "name": "A", "href": "a.html", "appliesTo": [ { "frameworks": [ null ] } ] } ]""")]
    [InlineData("""[ { "name": "A", "axis": { "name": "EF Core", "values": [ null ] }, "items": [ { "name": "B", "href": "b.html" } ] } ]""")]
    [InlineData("""[ { "name": "A", "items": [ null ] } ]""")]
    public void AnEmptyEntryWhereTheSelectionReadsSkipsOnlyThatPdf(string items)
    {
        using var listener = new TestListenerScope();

        Assert.Null(PdfBuilder.ReadPdfOutline(Encoding.UTF8.GetBytes($$"""{ "pdf": true, "pdfFramework": "net8.0", "items": {{items}} }"""), "api/toc.json"));
        Assert.Contains(listener.GetItemsByLogLevel(LogLevel.Error), x => x.Message.Contains("'api/toc.json'") && x.Message.Contains("empty entry"));

        // a PDF that selects nothing does not read them
        if (!items.Contains("\"items\": [ null ]", StringComparison.Ordinal))
        {
            Assert.NotNull(PdfBuilder.ReadPdfOutline(Encoding.UTF8.GetBytes($$"""{ "pdf": true, "items": {{items}} }"""), "toc.json"));
        }
        // the error count is global, and later tests check it
        Logger.ResetCount();
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
    [InlineData("/api/Lib.html", true)]
    [InlineData("/API/Lib%20Two.html", true)]
    [InlineData("/api/", true)]
    [InlineData("/guide/", false)]
    [InlineData("/static.html", false)]
    public void APageDocfxBuiltIsKnownByItsUrl(string pagePath, bool built)
    {
        var builtPages = new HashSet<string>(["/api/Lib.html", "/api/Lib Two.html", "/api/index.html"], StringComparer.OrdinalIgnoreCase);

        Assert.Equal(built, PdfBuilder.IsBuiltPage(builtPages, pagePath));
    }

    [Fact]
    public void AKeyWrittenTwiceHasItsLastValue()
    {
        using var listener = new TestListenerScope();

        // at the root or in an entry, as when the outline was read without the options, and whether it has a PDF or not
        Assert.NotNull(PdfBuilder.ReadPdfOutline("""{ "pdf": false, "pdf": true, "items": [] }"""u8.ToArray(), "twice/toc.json"));
        Assert.Null(PdfBuilder.ReadPdfOutline("""{ "pdf": true, "pdf": false, "items": [] }"""u8.ToArray(), "twice/toc.json"));
        var outline = PdfBuilder.ReadPdfOutline("""{ "pdf": true, "pdfFramework": "net8.0", "items": [ { "name": "A", "name": "B", "appliesTo": [], "appliesTo": [ { "frameworks": [ "net8.0" ] } ] } ] }"""u8.ToArray(), "twice-entry/toc.json");
        Assert.Equal("B", outline?.items?[0].name);
        Assert.Equal("net8.0", outline.items[0].appliesTo?.Single().frameworks?.Single());
        Assert.Empty(listener.GetItemsByLogLevel(LogLevel.Error));
        Assert.Empty(listener.GetItemsByLogLevel(LogLevel.Warning));
    }

    [Theory]
    [InlineData("unavailable", false, "Unavailable")]
    [InlineData("error:broken page", false, "Failed")]
    [InlineData("unapplied", false, "NotApplied")]
    // a page that timed out may not have run the script of its template yet, which says nothing of the template
    [InlineData("unapplied", true, "TimedOut")]
    // a page without the script of the modern template, such as of another template, which a timeout does not change
    [InlineData("untemplated", false, "NotApplied")]
    [InlineData("untemplated", true, "NotApplied")]
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

        // so is true or false, which YAML reads unquoted as well
        Assert.Equal("true", PdfBuilder.ReadPdfOutline("""{ "pdf": true, "pdfAxisValue": true }"""u8.ToArray(), "bool/toc.json")?.pdfAxisValue);
        Assert.Equal("false", PdfBuilder.ReadPdfOutline("""{ "pdf": true, "pdfFramework": false }"""u8.ToArray(), "bool/toc.json")?.pdfFramework);
        Assert.Contains(listener.GetItemsByLogLevel(LogLevel.Warning), w => w.Code == WarningCodes.Build.UnquotedTocValue && w.Message.StartsWith("'pdfAxisValue' of table of contents 'bool/toc.json' is true, which is used as 'true'"));

        // anything else is ignored, and says so
        var other = PdfBuilder.ReadPdfOutline("""{ "pdf": true, "pdfFramework": { "name": "net8.0" }, "pdfAxisValue": [ "ef8" ], "items": [ { "name": "A", "appliesTo": "net8.0" } ] }"""u8.ToArray(), "api/toc.json");
        Assert.Equal("A", string.Join(", ", Names(other?.items)));
        Assert.Null(PdfBuilder.GetAppliesToScript(other));
        var warnings = listener.GetItemsByLogLevel(LogLevel.Warning).Select(w => w.Message).ToList();
        Assert.Contains(warnings, w => w.Contains("'pdfFramework' of table of contents 'api/toc.json'"));
        Assert.Contains(warnings, w => w.Contains("'pdfAxisValue' of table of contents 'api/toc.json'"));
        Assert.Empty(listener.GetItemsByLogLevel(LogLevel.Error));

        // the options of an item below the root, which have no effect, are read without a warning
        var count = listener.GetItemsByLogLevel(LogLevel.Warning).Count();
        var nested = PdfBuilder.ReadPdfOutline("""{ "pdf": true, "items": [ { "name": "A", "pdfAxisValue": 8, "pdfFramework": [ "net8.0" ] } ] }"""u8.ToArray(), "nested/toc.json");
        Assert.Equal("A", string.Join(", ", Names(nested?.items)));
        Assert.Equal(count, listener.GetItemsByLogLevel(LogLevel.Warning).Count());
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

        // a file name is relative to the folder of its table of contents, which tells two of the same name apart
        var named = Json("net8", null).Replace("\"pdf\": true,", "\"pdf\": true, \"pdfFileName\": \"api.pdf\",");
        PdfBuilder.LeaveOutUnavailable(PdfBuilder.ReadPdfOutline(Encoding.UTF8.GetBytes(named), "v2/toc.json"));

        Assert.Contains(listener.GetItemsByLogLevel(LogLevel.Warning), x => x.Message.Contains("'pdfFramework' of 'api.pdf (v2/toc.json)'"));
    }

    [Fact]
    public void AGroupThatNamesNoFrameworkSaysNothingItsApiExistsIn()
    {
        // written by hand: on the site, an entry whose `appliesTo` names no framework is shown for no selection
        var outline = JsonSerializer.Deserialize<PdfBuilder.Outline>("""
            { "pdf": true, "pdfAxisValue": "ef8", "items": [
              { "name": "NoFrameworks", "href": "a.html", "appliesTo": [ { "version": "ef8" } ] },
              { "name": "EmptyFrameworks", "href": "b.html", "appliesTo": [ { "version": "ef8", "frameworks": [] } ] },
              { "name": "FromEF8", "href": "c.html", "appliesTo": [ { "version": "ef8", "frameworks": [ "net8.0" ] } ] }
            ] }
            """);

        PdfBuilder.LeaveOutUnavailable(outline);

        Assert.Equal("FromEF8", string.Join(", ", Names(outline.items)));
    }

    [Fact]
    public void AHeadingWhoseKeptItemsHaveNoNameGoesToo()
    {
        // written by hand: the site's table of contents shows no entry without a name, so no heading of only such entries
        var outline = JsonSerializer.Deserialize<PdfBuilder.Outline>("""
            { "pdf": true, "pdfFramework": "net8.0", "items": [
              { "name": "Unnamed", "items": [ { "href": "a.html", "appliesTo": [ { "frameworks": [ "net8.0" ] } ] } ] },
              { "name": "Named", "items": [ { "name": "C", "href": "c.html", "appliesTo": [ { "frameworks": [ "net8.0" ] } ] } ] }
            ] }
            """);

        PdfBuilder.LeaveOutUnavailable(outline);

        Assert.Equal("Named, C", string.Join(", ", Names(outline.items)));
    }

    private static PdfBuilder.Outline Parse(string framework, string value) =>
        JsonSerializer.Deserialize<PdfBuilder.Outline>(Json(framework, value));

    private static string Json(string framework, string value) => Toc
        .Replace("\"{framework}\"", framework is null ? "null" : $"\"{framework}\"")
        .Replace("\"{value}\"", value is null ? "null" : $"\"{value}\"");

    private static IEnumerable<string> Names(IEnumerable<PdfBuilder.Outline> items) =>
        items?.SelectMany(i => Names(i.items).Prepend(i.name)) ?? [];
}
