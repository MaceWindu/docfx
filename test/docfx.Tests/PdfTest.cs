// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Docfx.Common;
using Docfx.Pdf;
using Docfx.Tests.Common;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Actions;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Outline;
using UglyToad.PdfPig.Outline.Destinations;

namespace Docfx.Tests;

[Collection("docfx STA")]
public class PdfTest : TestBase
{
    [Fact]
    public async Task APageThatDoesNotAnswerIsGivenUpOn()
    {
        // as a page whose own script keeps it busy answers nothing, which Playwright waits for without a timeout
        var never = new TaskCompletionSource<string>();
        var (answered, answer) = await PdfBuilder.WaitOrGiveUp(never.Task, TimeSpan.FromMilliseconds(10), CancellationToken.None);
        Assert.False(answered);
        Assert.Null(answer);
        // failing once given up on, as when the page is closed, says nothing
        never.SetException(new InvalidOperationException("closed"));

        Assert.Equal((true, "state"), await PdfBuilder.WaitOrGiveUp(Task.FromResult("state"), TimeSpan.FromMilliseconds(10), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => PdfBuilder.WaitOrGiveUp(Task.FromException<string>(new InvalidOperationException()), TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task HeaderFooterOnCoverAndToc(bool? onCover, bool? onToc)
    {
        using var pdf = await BuildPdf(onCover, onToc, customTemplates: true);

        AssertPages(pdf);
        foreach (var page in pdf.GetPages())
        {
            var showHeaderFooter = page.Number switch
            {
                1 => onCover is true,
                2 or 3 => onToc is true,
                _ => true,
            };

            var header = string.Concat(page.Letters.Where(l => l.BoundingBox.Bottom > page.Height - 50).Select(l => l.Value));
            var footer = string.Concat(page.Letters.Where(l => l.BoundingBox.Top < 50).Select(l => l.Value));
            if (showHeaderFooter)
            {
                Assert.Equal($"HEADER-{page.Number}-OF-5", header);
                Assert.Equal($"FOOTER-{page.Number}-OF-5", footer);
            }
            else
            {
                Assert.Empty(header);
                Assert.Empty(footer);
                Assert.DoesNotContain("HEADER-", page.Text);
                Assert.DoesNotContain("FOOTER-", page.Text);
            }
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DefaultFooterOnCoverAndToc(bool? onCover, bool? onToc)
    {
        using var pdf = await BuildPdf(onCover, onToc, customTemplates: false);

        AssertPages(pdf);
        foreach (var page in pdf.GetPages())
        {
            var showFooter = page.Number switch
            {
                1 => onCover is true,
                2 or 3 => onToc is true,
                _ => true,
            };

            var header = string.Concat(page.Letters.Where(l => l.BoundingBox.Bottom > page.Height - 50).Select(l => l.Value));
            var footer = string.Concat(page.Letters.Where(l => l.BoundingBox.Top < 50).Select(l => l.Value));
            Assert.Empty(header);
            Assert.Equal(showFooter ? $"{page.Number} / 5" : "", footer);
        }
    }

    [Theory]
    [InlineData("A4 landscape", 842, 595, true)]
    [InlineData("A3 portrait", 842, 1191, true)]
    [InlineData("160mm 230mm", 454, 652, true)]
    [InlineData("230mm 160mm", 652, 454, true)]
    [InlineData("A4 landscape", 842, 595, false)]
    public async Task StaticHeaderFooterOnDifferentPageSizes(string coverPageSize, double coverWidth, double coverHeight, bool onCover)
    {
        using var pdf = await BuildPdf(onCover, true, customTemplates: true, coverPageSize, staticTemplates: true);

        AssertPages(pdf);
        Assert.InRange(pdf.GetPage(1).Width, coverWidth - 1, coverWidth + 1);
        Assert.InRange(pdf.GetPage(1).Height, coverHeight - 1, coverHeight + 1);

        foreach (var page in pdf.GetPages())
        {
            if (page.Number > 1)
            {
                Assert.InRange(page.Width, 594, 596);
                Assert.InRange(page.Height, 841, 843);
            }

            var showHeaderFooter = page.Number > 1 || onCover;
            AssertMargin(page, showHeaderFooter ? "HEADER-STATIC" : "", header: true);
            AssertMargin(page, showHeaderFooter ? "FOOTER-STATIC" : "", header: false);
        }

        static void AssertMargin(Page page, string expected, bool header)
        {
            var letters = page.Letters.Where(l => header
                ? l.BoundingBox.Bottom > page.Height - 50
                : l.BoundingBox.Top < 50).ToArray();

            Assert.Equal(expected, string.Concat(letters.Select(l => l.Value)));
            if (expected.Length == 0)
                return;

            Assert.All(letters, letter =>
            {
                Assert.InRange(letter.BoundingBox.Left, 0, page.Width);
                Assert.InRange(letter.BoundingBox.Right, 0, page.Width);
                Assert.InRange(letter.BoundingBox.Bottom, header ? page.Height - 50 : 0, header ? page.Height : 50);
                Assert.InRange(letter.BoundingBox.Top, header ? page.Height - 50 : 0, header ? page.Height : 50);
            });
            Assert.InRange(letters.Max(l => l.BoundingBox.Right), page.Width - 50, page.Width);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BookmarksNavigateToTopOfPage(bool groupChapters)
    {
        using var pdf = await BuildPdf(false, false, customTemplates: false,
            firstPageSize: "Letter", secondPageSize: "A4 landscape", groupChapters: groupChapters);

        Assert.True(pdf.TryGetBookmarks(out var bookmarks));
        var chapters = bookmarks.Roots;
        if (groupChapters)
        {
            var group = Assert.IsType<DocumentBookmarkNode>(Assert.Single(chapters));
            AssertPageDestination(pdf, group.Destination, 4);
            chapters = group.Children;
        }

        Assert.Collection(chapters,
            bookmark => AssertPageDestination(pdf, Assert.IsType<DocumentBookmarkNode>(bookmark).Destination, 4),
            bookmark => AssertPageDestination(pdf, Assert.IsType<DocumentBookmarkNode>(bookmark).Destination, 5));
    }

    [Fact]
    public async Task TocLinksNavigateToTopOfPage()
    {
        using var pdf = await BuildPdf(false, false, customTemplates: false,
            firstPageSize: "Letter", secondPageSize: "A4 landscape");

        for (var pageNumber = 2; pageNumber <= 3; pageNumber++)
        {
            var links = pdf.GetPage(pageNumber).GetAnnotations()
                .Select(annotation => annotation.Action).OfType<GoToAction>().ToArray();

            Assert.NotEmpty(links);
            Assert.All(links, link => AssertPageDestination(pdf, link.Destination, pageNumber + 2));
        }
    }

    [Fact]
    public void OnlyATableOfContentsOfAPdfIsReadWhole()
    {
        using var listener = new TestListenerScope();

        // an option of the wrong type, written by hand, where there is no PDF to apply it to
        Assert.Null(PdfBuilder.ReadPdfOutline("""{ "items": [ { "name": "A", "href": "a.html" } ], "pdfTocPage": "maybe" }"""u8.ToArray(), "toc.json"));
        Assert.Null(PdfBuilder.ReadPdfOutline("""{ "pdf": false, "pdfTocHeadingDepth": "deep" }"""u8.ToArray(), "toc.json"));
        Assert.Null(PdfBuilder.ReadPdfOutline("""[ "not", "an", "outline" ]"""u8.ToArray(), "toc.json"));
        Assert.Empty(listener.GetItemsByLogLevel(Docfx.Common.LogLevel.Error));
        Assert.Empty(listener.GetItemsByLogLevel(Docfx.Common.LogLevel.Warning));

        // only true is a PDF, as the boolean the outline reads; a value of another kind is none, and says so
        Assert.Null(PdfBuilder.ReadPdfOutline("""{ "pdf": "false", "items": [] }"""u8.ToArray(), "text/toc.json"));
        Assert.Null(PdfBuilder.ReadPdfOutline("""{ "pdf": 0, "items": [] }"""u8.ToArray(), "number/toc.json"));
        Assert.Empty(listener.GetItemsByLogLevel(Docfx.Common.LogLevel.Error));
        Assert.Equal(
            ["'pdf' of table of contents 'text/toc.json' is not true or false, so it has no PDF.", "'pdf' of table of contents 'number/toc.json' is not true or false, so it has no PDF."],
            listener.GetItemsByLogLevel(Docfx.Common.LogLevel.Warning).Select(w => w.Message));

        var outline = PdfBuilder.ReadPdfOutline("""{ "pdf": true, "pdfTocPage": true, "items": [ { "name": "A", "href": "a.html" } ] }"""u8.ToArray(), "guide/toc.json");
        Assert.True(outline?.pdfTocPage);
        Assert.Equal("A", outline.items?.Single().name);
        Assert.Equal("guide/toc.json", outline.Label);

        // the PDF of a table of contents that cannot be read says which one, and does not stop the others
        Assert.Null(PdfBuilder.ReadPdfOutline("""{ "pdf": true, "pdfTocPage": "maybe" }"""u8.ToArray(), "wrong/toc.json"));
        Assert.Contains(listener.GetItemsByLogLevel(Docfx.Common.LogLevel.Error), x => x.Message.Contains("'wrong/toc.json'"));
        Assert.Null(PdfBuilder.ReadPdfOutline("""{ "pdf": true, """u8.ToArray(), "broken/toc.json"));
        Assert.Contains(listener.GetItemsByLogLevel(Docfx.Common.LogLevel.Error), x => x.Message.Contains("'broken/toc.json'"));
        // the error count is global, and later tests check it
        Logger.ResetCount();
    }

    [Fact]
    public void AKeyWrittenTwiceHasItsLastValue()
    {
        using var listener = new TestListenerScope();

        Assert.NotNull(PdfBuilder.ReadPdfOutline("""{ "pdf": false, "pdf": true, "items": [] }"""u8.ToArray(), "toc.json"));
        Assert.Null(PdfBuilder.ReadPdfOutline("""{ "pdf": true, "pdf": false, "items": [] }"""u8.ToArray(), "toc.json"));
        Assert.Equal("B", PdfBuilder.ReadPdfOutline("""{ "pdf": true, "items": [ { "name": "A", "name": "B" } ] }"""u8.ToArray(), "toc.json")?.items?.Single().name);
        Assert.Empty(listener.GetItemsByLogLevel(Docfx.Common.LogLevel.Error));
        Assert.Empty(listener.GetItemsByLogLevel(Docfx.Common.LogLevel.Warning));
    }

    [Theory]
    [InlineData("api.pdf", "v2/toc.json", "api.pdf (v2/toc.json)")]
    [InlineData(null, "v2/toc.json", "v2/toc.json")]
    [InlineData("api.pdf", null, "api.pdf")]
    public void APdfIsNamedByItsFileNameAndTableOfContents(string fileName, string tocPath, string label)
    {
        // as warnings about a page name it, which several PDFs can print
        Assert.Equal(label, new PdfBuilder.Outline { name = "API", pdfFileName = fileName, tocPath = tocPath }.Label);
    }

    [Fact]
    public async Task ATableOfContentsThatCannotBeReadStopsOnlyItsOwnPdf()
    {
        var directory = GetRandomFolder();
        var configPath = CreateFile("docfx.json",
            """
            {
                "build": {
                    "content": [{ "files": [ "*.md", "*/*.md", "**/toc.yml" ] }],
                    "template": ["default"],
                    "dest": "_site"
                }
            }
            """, directory);
        CreateFile("good/toc.yml",
            """
            pdf: true
            items:
            - name: Good
              href: good.md
            """, directory);
        CreateFile("good/good.md", "# Good", directory);
        CreateFile("wrong/toc.yml",
            """
            pdf: true
            pdfTocPage: maybe
            items:
            - name: Wrong
              href: wrong.md
            """, directory);
        CreateFile("wrong/wrong.md", "# Wrong", directory);

        await Docset.Build(configPath);
        // Docset.Pdf disposes every listener when it ends, which a TestListenerScope cannot outlive
        var errors = new ErrorCollector();
        Logger.RegisterListener(errors);
        await Docset.Pdf(configPath);

        Assert.True(File.Exists(Path.Combine(directory, "_site", "good", "toc.pdf")));
        Assert.False(File.Exists(Path.Combine(directory, "_site", "wrong", "toc.pdf")));
        Assert.Contains(errors.Messages, message => message.Contains("'wrong/toc.json'"));
        Logger.ResetCount();
    }

    private sealed class ErrorCollector : ILoggerListener
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public List<string> Messages => [.. _messages];

        public void WriteLine(ILogItem item)
        {
            if (item.LogLevel == Docfx.Common.LogLevel.Error)
            {
                _messages.Enqueue(item.Message);
            }
        }

        public void Flush() { }

        public void Dispose() { }
    }

    private static void AssertPageDestination(PdfDocument pdf, ExplicitDestination destination, int pageNumber)
    {
        Assert.Equal(pageNumber, destination.PageNumber);
        Assert.Equal(ExplicitDestinationType.FitHorizontally, destination.Type);
        Assert.Equal(pdf.GetPage(pageNumber).MediaBox.Bounds.Top, destination.Coordinates.Top);
    }

    private async Task<PdfDocument> BuildPdf(
        bool? onCover, bool? onToc, bool customTemplates, string coverPageSize = null, bool staticTemplates = false,
        string firstPageSize = null, string secondPageSize = null, bool groupChapters = false)
    {
        var directory = GetRandomFolder();
        var configPath = CreateFile("docfx.json",
            """
            {
                "build": {
                    "content": [{ "files": [ "toc.yml" ] }],
                    "resource": [{ "files": [ "*.html", "public/*.css" ] }],
                    "template": ["default"],
                    "dest": "_site"
                }
            }
            """, directory);

        var chapters =
            """
            - name: ChapterOne
              href: first.html
            - name: ChapterTwo
              href: second.html
            """;
        if (groupChapters)
            chapters = "- name: Chapters\n  items:\n  " + chapters.Replace("\n", "\n  ");

        var toc =
            """
            pdf: true
            pdfCoverPage: cover.html
            pdfTocPage: true
            items:
            """ + "\n" + chapters;
        if (onCover.HasValue)
            toc += $"\npdfHeaderFooterOnCover: {onCover.Value.ToString().ToLowerInvariant()}";
        if (onToc.HasValue)
            toc += $"\npdfHeaderFooterOnToc: {onToc.Value.ToString().ToLowerInvariant()}";
        if (customTemplates)
        {
            var header = staticTemplates
                ? "HEADER-STATIC"
                : """HEADER-<span class="pageNumber"></span>-OF-<span class="totalPages"></span>""";
            var footer = staticTemplates
                ? "FOOTER-STATIC"
                : """FOOTER-<span class='pageNumber'></span>-OF-<span class='totalPages'></span>""";
            toc += $"""

                pdfHeaderTemplate: '{Template(header)}'
                pdfFooterTemplate: footer.html
                """;
            CreateFile("footer.html", Template(footer), directory);
        }

        CreateFile("toc.yml", toc, directory);
        CreateFile("public/main.css",
            """
            @page { size: A4; margin: 1in; }
            body { font-family: Arial, sans-serif; font-size: 16px; }
            .pdftoc li + li { break-before: page; }
            """, directory);
        CreateFile("public/docfx.min.css", "", directory);
        CreateFile("cover.html", Html("Cover body", coverPageSize), directory);
        CreateFile("first.html", Html("First article body", firstPageSize), directory);
        CreateFile("second.html", Html("Second article body", secondPageSize), directory);

        await Docset.Build(configPath);
        await Docset.Pdf(configPath);

        return PdfDocument.Open(Path.Combine(directory, "_site", "toc.pdf"));

        static string Html(string body, string pageSize = null) =>
            $$"""<!DOCTYPE html><html><head><link rel="stylesheet" href="public/main.css"><style>@page { size: {{pageSize ?? "A4"}}; }</style></head><body>{{body}}</body></html>""";

        static string Template(string text) =>
            $"""<div style="width: 100%; font-size: 12px;"><div style="float: right; padding: 0 2em;">{text}</div></div>""";
    }

    private static void AssertPages(PdfDocument pdf)
    {
        Assert.Equal(5, pdf.NumberOfPages);
        Assert.Contains("Cover body", pdf.GetPage(1).Text);
        Assert.Contains("Table of Contents", pdf.GetPage(2).Text);
        Assert.Matches(@"ChapterOne\s*4", pdf.GetPage(2).Text);
        Assert.Matches(@"ChapterTwo\s*5", pdf.GetPage(3).Text);
        Assert.Contains("First article body", pdf.GetPage(4).Text);
        Assert.Contains("Second article body", pdf.GetPage(5).Text);

        Assert.True(pdf.TryGetBookmarks(out var bookmarks));
        Assert.Collection(bookmarks.Roots,
            bookmark => Assert.Equal(4, Assert.IsType<DocumentBookmarkNode>(bookmark).Destination.PageNumber),
            bookmark => Assert.Equal(5, Assert.IsType<DocumentBookmarkNode>(bookmark).Destination.PageNumber));
    }
}
