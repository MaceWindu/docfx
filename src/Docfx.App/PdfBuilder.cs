// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Docfx.Build;
using Docfx.Common;
using Docfx.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Spectre.Console;

using UglyToad.PdfPig;
using UglyToad.PdfPig.Actions;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Graphics.Operations.SpecialGraphicsState;
using UglyToad.PdfPig.Outline;
using UglyToad.PdfPig.Outline.Destinations;
using UglyToad.PdfPig.Tokens;
using UglyToad.PdfPig.Writer;

using static Docfx.Build.HtmlTemplate;
using static UglyToad.PdfPig.Writer.PdfDocumentBuilder;

#nullable enable

namespace Docfx.Pdf;

static class PdfBuilder
{
    private static readonly SearchValues<char> InvalidPathChars = SearchValues.Create(Path.GetInvalidPathChars());

    class HeadingInfo
    {
        public string Text { get; init; } = "";
        public string Id { get; init; } = "";
        public int Level { get; init; }
        public Uri PageUrl { get; init; } = null!;
        public int PageNumber { get; set; }
    }

    // Read inherited page geometry without parsing content streams or loading fonts.
    class MediaBoxFactory : IPageFactory<MediaBox>
    {
        public MediaBox Create(int number, DictionaryToken dictionary, PageTreeMembers pageTreeMembers, NamedDestinations namedDestinations)
            => pageTreeMembers.MediaBox ?? MediaBox.Letter;
    }

    internal class Outline
    {
        public string name { get; init; } = "";

        // A namespace a PDF of one framework or axis value keeps only as the heading of the namespaces in it loses its
        // page.
        public string? href { get; set; }
        public Outline[]? items { get; set; }

        /// <summary>The frameworks and axis values the API of this item applies to, from `docfx metadata`.</summary>
        public AppliesTo[]? appliesTo { get; init; }

        /// <summary>The axis of the API below this item, which `docfx metadata` writes to its top-level items.</summary>
        public Axis? axis { get; init; }

        public string? pdfFramework { get; init; }
        public string? pdfAxisValue { get; init; }

        public bool pdf { get; init; }
        public string? pdfFileName { get; init; }
        public bool pdfTocPage { get; init; }
        public bool pdfPrintBackground { get; init; }
        public string? pdfCoverPage { get; init; }

        public string? pdfHeaderTemplate { get; init; }
        public string? pdfFooterTemplate { get; init; }
        public bool pdfHeaderFooterOnCover { get; init; }
        public bool pdfHeaderFooterOnToc { get; init; }

        public string? pdfTocSource { get; init; }
        public int pdfTocHeadingDepth { get; init; } = 3;

        /// <summary>The path of the table of contents in the site, which names it in warnings.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string? tocPath { get; set; }

        /// <summary>The PDF in a warning: its file name, or else the table of contents it is printed from.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string Label => pdfFileName ?? tocPath ?? name;
    }

    internal class AppliesTo
    {
        public string? version { get; init; }
        public string[]? frameworks { get; init; }
    }

    internal class Axis
    {
        public string? name { get; init; }
        public AxisValue[]? values { get; init; }
    }

    internal class AxisValue
    {
        public string? id { get; init; }
    }

    /// <summary>
    /// The outline of a table of contents, or <see langword="null"/> when it has no <c>pdf: true</c>. Only those are
    /// read whole, and their <c>appliesTo</c> and <c>axis</c>, which have a shape of their own, only for a PDF with
    /// <c>pdfFramework</c> or <c>pdfAxisValue</c>: a table of contents written by hand that uses either name for
    /// something else must not stop the PDFs of the others, nor its own PDF when it does not select anything.
    /// </summary>
    internal static Outline? ReadPdfOutline(byte[] json, string path)
    {
        // Whether it has a PDF is not known then, but it would stop the PDFs of the others.
        JsonNode? parsed;
        try
        {
            // A key twice, which a table of contents written by hand can have, would otherwise throw when it is read.
            parsed = JsonNode.Parse(json, documentOptions: new() { AllowDuplicateProperties = false });
        }
        catch (JsonException ex)
        {
            Logger.LogError($"Table of contents '{path}' cannot be read as JSON, so whether it has a PDF is not known, and none is printed of it: {ex.Message}");
            return null;
        }

        if (parsed is not JsonObject root ||
            !root.TryGetPropertyValue("pdf", out var pdf) || pdf is null || pdf.GetValueKind() is JsonValueKind.False)
        {
            return null;
        }

        // A number is the value it spells, with a warning: the build makes text of a number YAML read, as in
        // `pdfAxisValue: 8`, so only a table of contents written otherwise has one. An empty option, or one that is
        // not text, selects nothing, rather than stop the PDF.
        var ignored = new HashSet<string>();
        ReadOptions(root, warn: true);
        foreach (var name in ignored)
        {
            Logger.LogWarning($"'{name}' of table of contents '{path}' is not text, and is ignored.");
        }

        try
        {
            var selects = new[] { "pdfFramework", "pdfAxisValue" }.Any(name => Selected((string?)root[name]) is not null);
            if (!selects)
            {
                WithoutAppliesTo(root);
            }
            var outline = root.Deserialize<Outline>();
            if (outline is not null)
            {
                outline.tocPath = path;
            }
            // An empty entry, `appliesTo: [~]` written by hand, would stop every PDF where the selection reads it.
            if (selects && outline is not null && EmptyEntry(outline) is { } empty)
            {
                Logger.LogError($"Table of contents '{path}' cannot be read for its PDF: {empty} has an empty entry.");
                return null;
            }
            return outline;
        }
        catch (JsonException ex)
        {
            Logger.LogError($"Table of contents '{path}' cannot be read for its PDF: {ex.Message}");
            return null;
        }

        // Only the root's options are read; those of an item below it are made readable as text without a warning,
        // which would be about something that has no effect.
        void ReadOptions(JsonNode? node, bool warn)
        {
            switch (node)
            {
                case JsonObject item:
                    foreach (var name in new[] { "pdfFramework", "pdfAxisValue" })
                    {
                        switch (item[name]?.GetValueKind())
                        {
                            case JsonValueKind.Number:
                                var text = item[name]!.ToJsonString();
                                item[name] = text;
                                if (warn)
                                {
                                    Logger.LogWarning(
                                        $"'{name}' of table of contents '{path}' is the number {text}, which is used as '{text}': write it as text, quoted.",
                                        code: WarningCodes.Build.UnquotedTocValue);
                                }
                                break;
                            case JsonValueKind.String or null:
                                break;
                            default:
                                item.Remove(name);
                                if (warn)
                                {
                                    ignored.Add(name);
                                }
                                break;
                        }
                    }
                    foreach (var (_, value) in item)
                    {
                        ReadOptions(value, warn: false);
                    }
                    break;
                case JsonArray items:
                    foreach (var value in items)
                    {
                        ReadOptions(value, warn: false);
                    }
                    break;
            }
        }

        // Where the selection reads a list that has an empty entry: `items`, `appliesTo`, its `frameworks`, or the
        // `values` of an axis.
        static string? EmptyEntry(Outline node)
        {
            var name = node.name is { Length: > 0 } ? node.name : node.href ?? "the root";
            if (node.items?.Any(item => item is null) is true)
            {
                return $"'items' of '{name}'";
            }
            if (node.appliesTo?.Any(group => group is null || group.frameworks?.Any(f => f is null) is true) is true)
            {
                return $"'appliesTo' of '{name}'";
            }
            if (node.axis?.values?.Any(v => v is null) is true)
            {
                return $"'axis' of '{name}'";
            }
            return (node.items ?? []).Select(EmptyEntry).FirstOrDefault(found => found is not null);
        }

        static void WithoutAppliesTo(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject item:
                    item.Remove("appliesTo");
                    item.Remove("axis");
                    foreach (var (_, value) in item)
                    {
                        WithoutAppliesTo(value);
                    }
                    break;
                case JsonArray items:
                    foreach (var value in items)
                    {
                        WithoutAppliesTo(value);
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Leaves out of <paramref name="outline"/> the items whose API does not exist in its <c>pdfFramework</c> and
    /// <c>pdfAxisValue</c>, by the rule the picker of the modern template follows: a framework an API is not
    /// built for is stood in for by one it is, and an axis value says nothing about an API without that axis, or
    /// with another one.
    /// </summary>
    internal static void LeaveOutUnavailable(Outline outline)
    {
        var (framework, value) = (Selected(outline.pdfFramework), Selected(outline.pdfAxisValue));
        if (framework is null && value is null)
        {
            return;
        }

        // A selection that nothing in the table of contents applies to is most likely a typo, and would leave every
        // API out of the PDF without saying so.
        var groups = Descendants(outline).SelectMany(node => node.appliesTo ?? []).ToList();
        var axes = Descendants(outline).Prepend(outline).Select(node => node.axis).OfType<Axis>().ToList();
        var warned = false;
        var valueOnAnAxis = value is not null && axes.Any(axis => HasValue(axis, value));
        // The ids of the values of the axes that have the value, which its pages are told: the value says nothing about
        // a group of another id there, such as one of an entry written by hand without an axis.
        var axisValues = value is null || axes.Count == 0 ? null
            : axes.Where(axis => HasValue(axis, value)).SelectMany(axis => axis.values ?? []).Select(v => v.id).OfType<string>().ToHashSet();
        if (value is not null && (axes.Count > 0 ? !valueOnAnAxis : groups.Count > 0 && !groups.Any(group => group.version == value)))
        {
            Logger.LogWarning($"'pdfAxisValue' of '{outline.Label}' is '{value}', which is not a value of an axis of its API.");
            warned = true;
        }

        // Pages carry the ids of axis values, not their axis, so a value of several axes is picked in each.
        List<string> axesOfValue = value is null ? [] : axes.Where(axis => HasValue(axis, value)).Select(axis => axis.name ?? "").Distinct().ToList();
        if (axesOfValue.Count > 1)
        {
            Logger.LogWarning(
                $"'pdfAxisValue' '{value}' of '{outline.Label}' is a value of the axes {string.Join(", ", axesOfValue.Select(name => $"'{name}'"))}, " +
                "and leaves out what each of them lacks of it. Give the values of different axes different ids to print one only.");
        }
        if (groups.Count > 0 && framework is not null && !groups.Any(group => (group.frameworks ?? []).Any(f => Docfx.Dotnet.TargetFrameworkHelper.CanStandIn(framework, f))))
        {
            Logger.LogWarning($"'pdfFramework' of '{outline.Label}' is '{framework}', which none of its API applies to.");
            warned = true;
        }

        // Per axis that has the value, whether any of its API of that value is kept: a framework and a value that
        // each exist, but never together, would leave all of it out without saying so.
        var keptOfValue = new Dictionary<string, bool>();
        Visit(outline, outline.axis);
        foreach (var (axisName, anyKept) in keptOfValue)
        {
            if (!anyKept && !warned)
            {
                var api = axisName.Length > 0 ? $"the API of axis '{axisName}'" : "its API";
                Logger.LogWarning($"'pdfFramework' '{framework}' and 'pdfAxisValue' '{value}' of '{outline.Label}' leave out all of {api} that has '{value}': none of it exists in both.");
            }
        }

        void Visit(Outline node, Axis? axis)
        {
            if (node.items is null)
            {
                return;
            }

            var kept = new List<Outline>();
            foreach (var item in node.items)
            {
                var itemAxis = item.axis ?? axis;
                var available = IsAvailable(item, itemAxis);
                if (framework is not null && value is not null && ValueApplies(itemAxis) && item.appliesTo?.Any(group => group.version == value) is true)
                {
                    var key = itemAxis?.name ?? "";
                    keptOfValue[key] = keptOfValue.GetValueOrDefault(key) || available;
                }
                if (!available)
                {
                    // A namespace whose types exist in fewer frameworks than those of a namespace in it, which
                    // `namespaceLayout: nested` puts below it, stays as the heading of those, without its page.
                    if (HasAvailableApi(item.items, itemAxis))
                    {
                        item.href = null;
                        Visit(item, itemAxis);
                        if (item.items is { Length: > 0 })
                        {
                            kept.Add(item);
                        }
                    }
                    continue;
                }

                // A heading that only groups other items goes when they all do; an empty href is no page, as the
                // pages of the PDF read it.
                var groupsOthers = string.IsNullOrEmpty(item.href) && item.items is { Length: > 0 };
                Visit(item, itemAxis);
                if (!groupsOthers || item.items is { Length: > 0 })
                {
                    kept.Add(item);
                }
            }
            node.items = [.. kept];
        }

        // Whether an item among `items`, or below them, is an API that exists in the selection: an item without
        // `appliesTo`, such as an article, does not keep the item above it.
        bool HasAvailableApi(Outline[]? items, Axis? axis) => (items ?? []).Any(item =>
        {
            var itemAxis = item.axis ?? axis;
            return (item.appliesTo is { Length: > 0 } && IsAvailable(item, itemAxis)) || HasAvailableApi(item.items, itemAxis);
        });

        // A table of contents from before axes were written to it names none. A value of none of the axes it names
        // applies to nothing, as on its pages, which are told the values of no axis then.
        bool ValueApplies(Axis? axis) => value is not null && (axis is null ? axes.Count == 0 || valueOnAnAxis : HasValue(axis, value));

        bool IsAvailable(Outline node, Axis? axis)
        {
            var valueApplies = ValueApplies(axis);
            return node.appliesTo is not { Length: > 0 } || node.appliesTo.Any(group =>
                (!valueApplies || group.version is null || group.version == value || axisValues?.Contains(group.version) is false) &&
                (framework is null || (group.frameworks ?? []).Any(f => Docfx.Dotnet.TargetFrameworkHelper.CanStandIn(framework, f))));
        }
    }

    /// <summary>
    /// The script that makes the pages of <paramref name="outline"/> show its framework and axis value, as the
    /// picker would, or <see langword="null"/> when it sets neither. Pages carry the ids of axis values but not
    /// their axis, so the script names the values of the axes that have the value, when the outline names its axes,
    /// and the pages of the axes that do not have it, which the value says nothing about, as on the C# side: their
    /// own values may have the same ids as those of the axis of the value. <paramref name="tocPath"/> is the path of
    /// the table of contents in the site, which its links are relative to.
    /// </summary>
    internal static string? GetAppliesToScript(Outline outline, string tocPath = "toc.json")
    {
        var (framework, value) = (Selected(outline.pdfFramework), Selected(outline.pdfAxisValue));
        if (framework is null && value is null)
        {
            return null;
        }

        var axes = Descendants(outline).Prepend(outline).Select(node => node.axis).OfType<Axis>().ToList();
        var axisValues = value is null || axes.Count == 0 ? null
            : axes.Where(axis => HasValue(axis, value)).SelectMany(axis => axis.values!).Select(v => v.id).Distinct().ToArray();
        var otherAxisPages = value is null || axes.Count == 0 ? null
            : PagesWithAxis(outline, outline.axis)
                .Where(page => !HasValue(page.Axis, value))
                .Select(page => SitePath(tocPath, page.Href))
                .OfType<string>()
                .Distinct()
                .ToArray();
        return $"window.docfxAppliesTo = {JsonSerializer.Serialize(new { framework, value, axisValues, otherAxisPages = otherAxisPages is { Length: > 0 } ? otherAxisPages : null })};";

        static IEnumerable<(string Href, Axis Axis)> PagesWithAxis(Outline node, Axis? axis) =>
            (node.items ?? []).SelectMany(item =>
            {
                var itemAxis = item.axis ?? axis;
                IEnumerable<(string Href, Axis Axis)> own = !string.IsNullOrEmpty(item.href) && itemAxis is not null ? [(item.href, itemAxis)] : [];
                return own.Concat(PagesWithAxis(item, itemAxis));
            });
    }

    /// <summary>
    /// What a page of a PDF with <c>pdfFramework</c> or <c>pdfAxisValue</c> did with the selection, once loaded:
    /// <c>unapplied</c> when its template did not apply it, which only the <c>modern</c> template does and marks on
    /// <c>window.docfxAppliesTo</c>, and <c>untemplated</c> when the page has no script of the <c>modern</c> template
    /// at all, which sets <c>window.docfx</c> as it starts; <c>error:</c> and the message when the template failed to
    /// apply it; <c>unavailable</c> when its API does not exist in the selection; else empty.
    /// </summary>
    internal const string PrintSelectionStateScript =
        "() => window.docfxAppliesTo && window.docfxAppliesTo.error ? 'error:' + window.docfxAppliesTo.error" +
        " : window.docfxAppliesTo && window.docfxAppliesTo.applied" +
        " ? (document.body && document.body.hasAttribute('data-applies-to-unavailable') ? 'unavailable' : '')" +
        " : window.docfx ? 'unapplied' : 'untemplated'";

    internal enum PrintSelectionProblem { None, Unavailable, Failed, TimedOut, NotApplied }

    /// <summary>
    /// What to warn about a page by its <see cref="PrintSelectionStateScript"/> state. A page that timed out may not
    /// have run the script of its template yet, so not having applied the selection says nothing of the template then;
    /// a page without the <c>modern</c> template's script, which runs before the page is loaded, does not apply it either way.
    /// </summary>
    internal static PrintSelectionProblem GetPrintSelectionProblem(string state, bool timedOut) => state switch
    {
        "unavailable" => PrintSelectionProblem.Unavailable,
        _ when state.StartsWith("error:", StringComparison.Ordinal) => PrintSelectionProblem.Failed,
        "untemplated" => PrintSelectionProblem.NotApplied,
        "unapplied" => timedOut ? PrintSelectionProblem.TimedOut : PrintSelectionProblem.NotApplied,
        _ => PrintSelectionProblem.None,
    };

    /// <summary>The ids of what a page of a PDF of one framework or axis value leaves out, as the <c>modern</c> template marks it.</summary>
    internal const string LeftOutIdsScript =
        "() => Array.from(document.querySelectorAll('[data-applies-to-left-out][id], [data-applies-to-left-out] [id]'), e => e.id)";

    // The path of the page a link of the table of contents at `tocPath` goes to, as the page's `location.pathname`
    // has it; none for a link to another site.
    private static string? SitePath(string tocPath, string href)
    {
        var root = new Uri("http://localhost/");
        var url = new Uri(new Uri(root, tocPath.Replace('\\', '/')), href);
        return url.Scheme == root.Scheme && url.Authority == root.Authority ? url.AbsolutePath : null;
    }

    private static bool HasValue(Axis axis, string value) => (axis.values ?? []).Any(v => v.id == value);

    private static IEnumerable<Outline> Descendants(Outline node) => (node.items ?? []).SelectMany(item => Descendants(item).Prepend(item));

    // An empty pdfFramework or pdfAxisValue selects nothing, as the template reads it.
    private static string? Selected(string? option) => string.IsNullOrWhiteSpace(option) ? null : option.Trim();

    public static Task Run(BuildJsonConfig config, string configDirectory, string? outputDirectory = null, CancellationToken cancellationToken = default)
    {
        var outputFolder = Path.GetFullPath(Path.Combine(
            string.IsNullOrEmpty(outputDirectory) ? Path.Combine(configDirectory, config.Output ?? "") : outputDirectory,
            config.Dest ?? ""));

        Logger.LogInfo($"Searching for manifest in {outputFolder}");
        return CreatePdf(outputFolder, cancellationToken);
    }

    /// <summary>
    /// What <paramref name="task"/> gives, or <c>(false, null)</c> when it does not finish within
    /// <paramref name="timeout"/>, which it is given up on after.
    /// </summary>
    internal static async Task<(bool Answered, T? Answer)> WaitOrGiveUp<T>(Task<T> task, TimeSpan timeout, CancellationToken cancellationToken) where T : class
    {
        Observe(task);
        try
        {
            return (true, await task.WaitAsync(timeout, cancellationToken));
        }
        catch (TimeoutException)
        {
            return (false, null);
        }
    }

    // A call given up on can still fail once its page is closed, which says nothing of the PDF.
    private static void Observe(Task task)
    {
        _ = task.ContinueWith(
            static task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public static async Task CreatePdf(string outputFolder, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        // The pages docfx built, by their path in the site: a page the build copies as it is, such as a hand-written
        // HTML file, has no template to apply a selection.
        var builtPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pdfTocList = GetPdfTocs().ToList();
        var pdfTocs = pdfTocList.ToDictionary(p => p.url, p => p.toc);
        if (pdfTocs.Count == 0)
            return;

        PlaywrightHelper.EnsurePlaywrightNodeJsPath();

        Program.Main(["install", "chromium", "--only-shell"]);

        // Create linked CancellationToken with PosixSignalRegistration handler.
        // It's required because default `Ctrl+C` interruption is canceled when using WebApplication inside Spectre.Console command.
        using var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void onSignal(PosixSignalContext context)
        {
            context.Cancel = true;
            cancellationTokenSource.Cancel();
        }
        using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, onSignal);
        using var sigQuit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, onSignal);
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, onSignal);
        cancellationToken = cancellationTokenSource.Token;

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        Uri? baseUrl = null;
        var pdfPageNumbers = new ConcurrentDictionary<string, Dictionary<Outline, int>>();
        var pdfHeadings = new ConcurrentDictionary<string, List<HeadingInfo>>();

        using var app = builder.Build();
        app.UseServe(outputFolder);
        app.MapGet("/_pdftoc/{*url}", TocPage);
        await app.StartAsync(cancellationToken);

        baseUrl = new Uri(app.Urls.First());

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Args = GetBrowserArgs(baseUrl) });
        await using var context = await browser.NewContextAsync(new() { UserAgent = "docfx/pdf" });

        var timeout = Environment.GetEnvironmentVariable("DOCFX_PDF_TIMEOUT") is { } value
            ? int.Parse(value, CultureInfo.InvariantCulture)
            : (int?)null;
        if (timeout is { } defaultTimeout)
        {
            context.SetDefaultTimeout(defaultTimeout);
        }
        // as long as Playwright waits by default, for what it waits for without a timeout; 0 disables it, as for Playwright
        var scriptTimeout = (timeout ?? 30_000) is > 0 and var ms ? TimeSpan.FromMilliseconds(ms) : Timeout.InfiniteTimeSpan;

        using var pageLimiter = new SemaphoreSlim(Environment.ProcessorCount, Environment.ProcessorCount);
        var pagePool = new ConcurrentBag<IPage>();
        var openPages = 0;
        var checkedPages = new ConcurrentDictionary<(Outline, string), bool>();
        var unappliedOutlines = new ConcurrentDictionary<Outline, bool>();

        // A PDF for one framework or axis value gets pages of its own: the script that selects them runs on every
        // page of a context, and the pages of the shared context are reused across PDFs.
        var outlineContexts = new Dictionary<Outline, (IBrowserContext Context, ConcurrentBag<IPage> Pool)>();
        foreach (var (_, toc, script) in pdfTocList)
        {
            if (script is not null)
            {
                var outlineContext = await browser.NewContextAsync(new() { UserAgent = "docfx/pdf" });
                await outlineContext.AddInitScriptAsync(script);
                if (timeout is not null)
                {
                    outlineContext.SetDefaultTimeout(timeout.Value);
                }
                outlineContexts[toc] = (outlineContext, new());
            }
        }
        var headerFooterTemplateCache = new ConcurrentDictionary<string, string>();
        var headerFooterPageCache = new ConcurrentDictionary<(string Header, string Footer, PageSize Size, double Width, double Height), Task<byte[]>>();

        var pdfBuildTask = AnsiConsole.Progress().StartAsync(async progress =>
        {
            await Parallel.ForEachAsync(pdfTocs, new ParallelOptions { CancellationToken = cancellationToken }, async (item, _) =>
            {
                var (url, toc) = item;
                var outputName = Path.Combine(Path.GetDirectoryName(url) ?? "", toc.pdfFileName ?? Path.ChangeExtension(Path.GetFileName(url), ".pdf"));
                var task = progress.AddTask(outputName);
                var pdfOutputPath = Path.Combine(outputFolder, outputName);

                await CreatePdf(
                    PrintPdf, PrintHeaderFooter, task, new(baseUrl, url), toc, outputFolder, pdfOutputPath,
                    pageNumbers => pdfPageNumbers[url] = pageNumbers,
                    headings => pdfHeadings[url] = headings,
                    cancellationToken);

                task.Value = task.MaxValue;
                task.StopTask();

                // Its pages are not used by any other PDF.
                if (outlineContexts.TryGetValue(toc, out var own))
                {
                    while (own.Pool.TryTake(out var drained))
                    {
                        Interlocked.Decrement(ref openPages);
                    }
                    await own.Context.DisposeAsync();
                }
            });
        });

        try
        {
            await pdfBuildTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!pdfBuildTask.IsCompleted)
            {
                // If pdf generation task is not completed.
                // Manually close playwright context/browser to immediately shutdown remaining tasks.
                await context.CloseAsync();
                await browser.CloseAsync();
                try
                {
                    await pdfBuildTask; // Wait AnsiConsole.Progress operation completed to output logs.
                }
                catch
                {
                    Logger.LogError($"PDF file generation is canceled by user interaction.");
                    return;
                }
            }
        }

        Logger.LogVerbose($"PDF done in {stopwatch.Elapsed}");
        return;

        IEnumerable<(string url, Outline toc, string? script)> GetPdfTocs()
        {
            var manifestPath = Path.Combine(outputFolder, "manifest.json");
            var manifest = Newtonsoft.Json.JsonConvert.DeserializeObject<Manifest>(File.ReadAllText(manifestPath));
            if (manifest is null)
                yield break;

            foreach (var file in manifest.Files)
            {
                if (file.Type != "Resource" && file.Output.TryGetValue(".html", out var htmlOutput))
                {
                    builtPages.Add("/" + htmlOutput.RelativePath.Replace('\\', '/'));
                }

                if (file.Type != "Toc" || !file.Output.TryGetValue(".json", out var jsonOutput))
                    continue;

                var tocFile = Path.Combine(outputFolder, jsonOutput.RelativePath);
                if (!File.Exists(tocFile))
                    continue;

                var outline = ReadPdfOutline(File.ReadAllBytes(tocFile), jsonOutput.RelativePath);
                if (outline?.pdf is true)
                {
                    // the script names the axes of all of the table of contents, before what is left out
                    var script = GetAppliesToScript(outline, jsonOutput.RelativePath);
                    LeaveOutUnavailable(outline);
                    yield return (jsonOutput.RelativePath, outline, script);
                }
            }
        }

        IResult TocPage(string url)
        {
            var pageNumbers = pdfPageNumbers.GetValueOrDefault(url);
            var headings = pdfHeadings.GetValueOrDefault(url);
            return Results.Content(TocHtmlTemplate(new Uri(baseUrl!, url), pdfTocs[url], pageNumbers, headings).ToString(), "text/html", Encoding.UTF8);
        }

        // A page of the pool of a context, or a new one. The pages of a PDF of one framework or axis value are not
        // used by other PDFs, which run at the same time: an idle page of another pool is closed instead, so that no
        // more pages are open than can be used at once.
        async Task<IPage> RentPage(IBrowserContext pageContext, ConcurrentBag<IPage> pool)
        {
            if (pool.TryTake(out var pooled))
            {
                return pooled;
            }

            if (Interlocked.Increment(ref openPages) > Environment.ProcessorCount)
            {
                foreach (var other in outlineContexts.Values.Select(c => c.Pool).Prepend(pagePool))
                {
                    if (other != pool && other.TryTake(out var idle))
                    {
                        Interlocked.Decrement(ref openPages);
                        try
                        {
                            await idle.CloseAsync();
                        }
                        catch (PlaywrightException)
                        {
                            // its context was closed when its PDF was done, which closed the page too
                        }
                        break;
                    }
                }
            }
            return await pageContext.NewPageAsync();
        }

        async Task<(byte[]? bytes, List<HeadingInfo> headings, string[] leftOut)> PrintPdf(Outline outline, Uri url, int headingDepth)
        {
            await pageLimiter.WaitAsync(cancellationToken);
            var (pageContext, pool) = outlineContexts.TryGetValue(outline, out var own) ? own : (context, pagePool);
            var page = await RentPage(pageContext, pool);
            var headings = new List<HeadingInfo>();
            string[] leftOut = [];
            var timedOut = false;
            // whether a page that timed out loading still answers what it is asked
            var responsive = true;

            try
            {
                Uri beforeUri = new(page.Url);
                var response = await page.GotoAsync(url.ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded });
                if (response?.Status is 404)
                    return (null, headings, leftOut);

                bool isSameUrlNavigation = response == null && beforeUri == url;
                bool isHashFragmentNavigation = response == null
                    && beforeUri.GetLeftPart(UriPartial.Path) == url.GetLeftPart(UriPartial.Path)
                    && beforeUri.Fragment != url.Fragment;

                if (isSameUrlNavigation)
                {
                    // Specified page content is already loaded.
                }
                else if (isHashFragmentNavigation)
                {
                    // Hash fragment navigation inside page. network request is not executed.
                    await page.WaitForURLAsync(url.ToString());
                }
                else if (response is null || !response.Ok)
                {
                    // Goto navigation failed.
                    throw new InvalidOperationException($"Failed to build PDF page [{response?.Status}]: {url}");
                }
                else
                {
                    // The page of a PDF of one framework or axis value gives the copies of its content ids of their
                    // own as it applies the selection, so their headings are linked once it is ready, or has timed out.
                    var anchored = false;
                    try
                    {
                        if (own.Context is null)
                        {
                            await page.AddScriptTagAsync(new() { Content = EnsureHeadingAnchorScript });
                            anchored = true;
                        }
                        await page.WaitForFunctionAsync("!window.docfx || window.docfx.ready");
                        if (own.Context is not null)
                        {
                            await page.AddScriptTagAsync(new() { Content = EnsureHeadingAnchorScript });
                            anchored = true;
                        }
                        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                    }
                    catch (TimeoutException)
                    {
                        timedOut = true;
                        Logger.LogWarning($"Timeout waiting for page to load, generated PDF page may be incomplete: {url}");
                        if (!anchored)
                        {
                            await Ask(() => page.AddScriptTagAsync(new() { Content = EnsureHeadingAnchorScript }));
                        }
                    }
                }

                // The page of an API the PDF's framework or axis value does not have, which the table of contents did
                // not leave out as its entry says nothing of the API, such as an entry with a `uid` or a hand-written one;
                // and a page docfx built with a template that does not apply the selection, which only the table of
                // contents follows then.
                var pagePath = url.AbsolutePath;
                if (own.Context is not null && !IsTocPage(url) && !IsCoverPage(url, outputFolder, outline.pdfCoverPage)
                    && checkedPages.TryAdd((outline, pagePath), true))
                {
                    // a page that does not answer is left out, with a warning of its own
                    var state = await Ask(() => page.EvaluateAsync<string>(PrintSelectionStateScript)) ?? "";
                    var problem = GetPrintSelectionProblem(state, timedOut);
                    // a hand-written page, which has no template, applies nothing, nor is it expected to
                    var built = builtPages.Contains(Uri.UnescapeDataString(pagePath));
                    if (problem is PrintSelectionProblem.Unavailable)
                    {
                        Logger.LogWarning(
                            $"'{pagePath}' in '{outline.Label}' documents an API that does not exist in its 'pdfFramework' or 'pdfAxisValue', " +
                            "but is printed, without its members: its entry in the table of contents does not say what the API applies to. Include the API's own table of contents to leave such pages out.");
                    }
                    else if (problem is PrintSelectionProblem.Failed)
                    {
                        Logger.LogWarning(
                            $"'{pagePath}' in '{outline.Label}' failed to apply its 'pdfFramework' or 'pdfAxisValue', so it may show what the selection lacks: {state["error:".Length..]}");
                    }
                    else if (problem is PrintSelectionProblem.TimedOut && built)
                    {
                        Logger.LogWarning(
                            $"'{pagePath}' in '{outline.Label}' timed out before it applied its 'pdfFramework' or 'pdfAxisValue', so it may show what the selection lacks.");
                    }
                    else if (problem is PrintSelectionProblem.NotApplied && built && unappliedOutlines.TryAdd(outline, true))
                    {
                        Logger.LogWarning(
                            $"'{pagePath}' in '{outline.Label}' does not apply its 'pdfFramework' or 'pdfAxisValue', which only the 'modern' template does: " +
                            "the table of contents leaves out what the selection lacks, but such pages are printed as the site shows them.");
                    }
                }

                // What the page leaves out for the selection, such as a member, which a link to stays text, as a link
                // to a page the PDF leaves out does.
                if (own.Context is not null && !IsTocPage(url) && !IsCoverPage(url, outputFolder, outline.pdfCoverPage))
                {
                    leftOut = await Ask(() => page.EvaluateAsync<string[]>(LeftOutIdsScript)) ?? [];
                }

                // Extract headings from the page if needed
                if (outline.pdfTocSource == "headings" && headingDepth > 0 && !IsTocPage(url) && !IsCoverPage(url, outputFolder, outline.pdfCoverPage))
                {
                    headings = await Ask(() => ExtractHeadingsFromPage(page, url, headingDepth)) ?? [];
                }

                var bytes = await Ask(() => page.PdfAsync(new PagePdfOptions
                {
                    PreferCSSPageSize = true,
                    PrintBackground = outline.pdfPrintBackground,
                }));

                if (!responsive)
                {
                    Logger.LogWarning($"'{url.AbsolutePath}' in '{outline.Label}' does not respond after it timed out loading, and is left out of the PDF.");
                    return (null, new List<HeadingInfo>(), []);
                }
                return (bytes, headings, leftOut);
            }
            finally
            {
                if (responsive)
                {
                    pool.Add(page);
                }
                else
                {
                    // asked anything more, it would not answer either, so it is not reused
                    Interlocked.Decrement(ref openPages);
                    Observe(page.CloseAsync());
                }
                pageLimiter.Release();
            }

            // After a page timed out loading, what it is asked next is given up on after the timeout too: Playwright
            // waits for a script tag, the result of a script and the PDF of a page without a timeout, and a page
            // whose own script keeps it busy would hold the PDF up for good. Given up on once, it is asked nothing more.
            async Task<T?> Ask<T>(Func<Task<T>> call) where T : class
            {
                if (!timedOut)
                {
                    return await call();
                }
                if (!responsive)
                {
                    return null;
                }
                var (answered, answer) = await WaitOrGiveUp(call(), scriptTimeout, cancellationToken);
                responsive = answered;
                return answer;
            }
        }

        async Task<List<HeadingInfo>> ExtractHeadingsFromPage(IPage page, Uri pageUrl, int maxDepth)
        {
            var headings = new List<HeadingInfo>();
            var selector = string.Join(",", Enumerable.Range(1, maxDepth).Select(i => $"article h{i}, .content h{i}"));

            try
            {
                // One call per page: a call per heading costs a round trip to the browser each, which adds up on
                // the thousands of headings of an API PDF. Content the page hides, such as a tab that is not
                // selected, the members a PDF of one framework leaves out, or the content of an API for another
                // framework, is not printed, so its headings are left out.
                var elements = await page.EvaluateAsync<string[][]>(
                    "selector => Array.from(document.querySelectorAll(selector)).filter(e => !e.closest('[hidden]')).map(e => [e.tagName, e.getAttribute('id') || '', e.innerText])",
                    selector);
                foreach (var element in elements)
                {
                    var level = int.Parse(element[0][1].ToString());
                    var id = element[1];
                    var text = element[2].Trim();

                    // Skip headings without id or text
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(text))
                        continue;

                    headings.Add(new HeadingInfo
                    {
                        Text = text.ReplaceLineEndings(" "),
                        Id = id,
                        Level = level,
                        PageUrl = pageUrl
                    });
                }
            }
            // a page given up on and closed meanwhile, which is warned about already
            catch (Exception ex) when (!page.IsClosed)
            {
                Logger.LogWarning($"Failed to extract headings from {pageUrl}: {ex.Message}");
            }

            return headings;
        }

        Task<byte[]> PrintHeaderFooter(Outline toc, int pageNumber, int totalPages, Page contentPage)
        {
            var headerTemplate = ExpandTemplate(GetHeaderFooter(toc.pdfHeaderTemplate), pageNumber, totalPages);
            var footerTemplate = ExpandTemplate(GetHeaderFooter(toc.pdfFooterTemplate) ?? DefaultFooterTemplate, pageNumber, totalPages);

            var cacheKey = (headerTemplate, footerTemplate, contentPage.Size, contentPage.Width, contentPage.Height);
            return headerFooterPageCache.GetOrAdd(cacheKey, _ => PrintHeaderFooterCore());

            async Task<byte[]> PrintHeaderFooterCore()
            {
                await pageLimiter.WaitAsync(cancellationToken);
                var page = await RentPage(context, pagePool);

                try
                {
                    await page.GotoAsync("about:blank");

                    var options = new PagePdfOptions
                    {
                        DisplayHeaderFooter = true,
                        HeaderTemplate = headerTemplate,
                        FooterTemplate = footerTemplate,
                    };

                    if (TryGetPlaywrightPageFormat(contentPage.Size, out var pageFormat))
                    {
                        options.Format = pageFormat;
                        options.Landscape = contentPage.Width > contentPage.Height;
                    }
                    else
                    {
                        var customPageSize = GetPageSizeSettings(contentPage);
                        options.Width = customPageSize.Width;
                        options.Height = customPageSize.Height;
                        options.Landscape = customPageSize.Landscape;
                    }

                    return await page.PdfAsync(options);
                }
                finally
                {
                    pagePool.Add(page);
                    pageLimiter.Release();
                }
            }

            static string ExpandTemplate(string? pdfTemplate, int pageNumber, int totalPages)
            {
                return (pdfTemplate ?? "")
                    .Replace("<span class='pageNumber'></span>", $"<span>{pageNumber}</span>")
                    .Replace("<span class=\"pageNumber\"></span>", $"<span>{pageNumber}</span>")
                    .Replace("<span class='totalPages'></span>", $"<span>{totalPages}</span>")
                    .Replace("<span class=\"totalPages\"></span>", $"<span>{totalPages}</span>");
            }

            string? GetHeaderFooter(string? template)
            {
                if (string.IsNullOrEmpty(template))
                    return template;

                // Check path chars. If it's contains HTML chars. Skip access to file content to optimmize performance
                if (template.AsSpan().ContainsAny(InvalidPathChars))
                    return template;

                return headerFooterTemplateCache.GetOrAdd(template, (_) =>
                {
                    // Note: This valueFactory might be called multiple times.
                    try
                    {
                        var path = Path.GetFullPath(Path.Combine(outputFolder, template));
                        if (!File.Exists(path))
                            return template;

                        var templateContent = File.ReadAllText(path);
                        return templateContent;
                    }
                    catch
                    {
                        return template;
                    }
                });
            }

        }
    }

    /// <summary>
    /// The arguments Chromium is launched with to print the pages <paramref name="baseUrl"/> serves.
    /// </summary>
    internal static string[] GetBrowserArgs(Uri baseUrl)
    {
        // The server listens on a port the system picks, from a range that starts as low as 1025 on some machines
        // (`netsh int ipv4 show dynamicport tcp` on Windows). Chromium refuses to load from the ports it restricts
        // in that range, such as 1723 or 6000 (`kRestrictedPorts` in net/base/port_util.cc), unless they are allowed.
        return [$"--explicitly-allowed-ports={baseUrl.Port}"];
    }

    static async Task CreatePdf(
        Func<Outline, Uri, int, Task<(byte[]? bytes, List<HeadingInfo> headings, string[] leftOut)>> printPdf, Func<Outline, int, int, Page, Task<byte[]>> printHeaderFooter, ProgressTask task,
        Uri outlineUrl, Outline outline, string outputFolder, string pdfOutputPath, Action<Dictionary<Outline, int>> updatePageNumbers, Action<List<HeadingInfo>> updateHeadings, CancellationToken cancellationToken)
    {
        var pages = GetPages(outline).ToArray();
        if (pages.Length == 0)
            return;

        var pageBytes = new Dictionary<Outline, byte[]>();
        var pageHeadings = new Dictionary<Outline, List<HeadingInfo>>();
        // the ids of what each page leaves out, by its URL
        var leftOutIds = new HashSet<(Uri Page, string Id)>();

        // Make progress at 99% before merge PDF
        task.MaxValue = pages.Length + (pages.Length / 99.0);

        await Parallel.ForEachAsync(pages, new ParallelOptions { CancellationToken = cancellationToken }, async (item, _) =>
        {
            var (url, node) = item;

            // Skip TOC pages - they depend on data from content pages (headings, page numbers)
            // and will be rendered later once that data is available.
            if (IsTocPage(url))
            {
                task.Increment(1);
                return;
            }

            var result = await printPdf(outline, url, outline.pdfTocHeadingDepth);
            if (result.bytes is { } bytes)
            {
                lock (pageBytes)
                    pageBytes[node] = bytes;
            }
            if (result.headings.Count > 0)
            {
                lock (pageHeadings)
                    pageHeadings[node] = result.headings;
            }
            lock (leftOutIds)
            {
                foreach (var id in result.leftOut)
                    leftOutIds.Add((CleanUrl(url), id));
            }
            task.Increment(1);
        });

        // Collect headings in document order:
        // - Page order: preserved by iterating `pages` array (parallel processing loses this)
        // - Within-page order: preserved by DOM order from QuerySelectorAllAsync
        var allHeadings = pages
            .Where(p => pageHeadings.ContainsKey(p.node))
            .SelectMany(p => pageHeadings[p.node])
            .ToList();

        // Update headings before page numbers are calculated
        updateHeadings(allHeadings);

        // Render the TOC page now that headings are available.
        // This is deferred from the parallel render because the TOC content depends on
        // data extracted from content pages (headings for heading-based TOC).
        // Rendering it here ensures the correct page count before calculating page numbers.
        foreach (var (tocUrl, tocNode) in pages)
        {
            if (!IsTocPage(tocUrl))
                continue;

            var result = await printPdf(outline, tocUrl, 0);
            if (result.bytes != null)
                pageBytes[tocNode] = result.bytes;
        }

        var pagesByNode = pages.ToDictionary(p => p.node);
        var pagesByUrl = new Dictionary<Uri, List<(Outline node, NamedDestinations namedDests)>>();
        var pageNumbers = new Dictionary<Outline, int>();
        var urlPageNumbers = new Dictionary<Uri, int>();
        var pageTops = new Dictionary<int, double>();
        var numberOfPages = 0;

        foreach (var (url, node) in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!pageBytes.TryGetValue(node, out var bytes))
                continue;

            using var document = PdfDocument.Open(bytes);
            if (document.NumberOfPages is 0)
                continue;

            var key = CleanUrl(url);
            if (!pagesByUrl.TryGetValue(key, out var dests))
                pagesByUrl[key] = dests = [];
            dests.Add((node, document.Structure.Catalog.GetNamedDestinations()));

            pageBytes[node] = bytes;
            pageNumbers[node] = numberOfPages + 1;
            urlPageNumbers[CleanUrl(url)] = numberOfPages + 1;
            document.AddPageFactory(new MediaBoxFactory());
            pageTops[numberOfPages + 1] = document.GetPage<MediaBox>(1).Bounds.Top;
            numberOfPages += document.NumberOfPages;
        }

        if (numberOfPages is 0)
            return;

        // Resolve actual page numbers for headings using named destinations.
        // Each heading ID corresponds to a named destination in the document's PDF,
        // so we can determine the exact page within the merged PDF.
        foreach (var heading in allHeadings)
        {
            var cleanUrl = CleanUrl(heading.PageUrl);
            if (pagesByUrl.TryGetValue(cleanUrl, out var dests))
            {
                // Chromium stores named destinations URL-encoded, same as link fragments in HandleUriAction.
                var destName = new UriBuilder(heading.PageUrl) { Fragment = heading.Id }.Uri.Fragment[1..];
                var resolved = false;
                foreach (var (node, namedDests) in dests)
                {
                    if (namedDests.TryGet(destName, out var dest) && dest is not null)
                    {
                        heading.PageNumber = pageNumbers[node] - 1 + dest.PageNumber;
                        resolved = true;
                        break;
                    }
                }
                // Fall back to document start page if heading ID wasn't found in named destinations
                if (!resolved && urlPageNumbers.TryGetValue(cleanUrl, out var startPage))
                {
                    heading.PageNumber = startPage;
                }
            }
        }

        var producer = $"docfx ({typeof(PdfBuilder).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version})";

        using var output = File.Create(pdfOutputPath);
        using var builder = new PdfDocumentBuilder(output);

        builder.DocumentInformation = new() { Producer = producer };
        builder.Bookmarks = CreateBookmarks(outline.items);

        await MergePdf();
        return;

        IEnumerable<(Uri url, Outline node)> GetPages(Outline outline)
        {
            if (!string.IsNullOrEmpty(outline.pdfCoverPage))
            {
                var href = $"/{outline.pdfCoverPage}";
                yield return (new(outlineUrl, href), new() { href = href, pdfPrintBackground = outline.pdfPrintBackground });
            }

            if (outline.pdfTocPage)
            {
                var href = $"/_pdftoc{outlineUrl.AbsolutePath}";
                yield return (new(outlineUrl, href), new() { href = href, pdfPrintBackground = outline.pdfPrintBackground });
            }

            if (!string.IsNullOrEmpty(outline.href))
            {
                var url = new Uri(outlineUrl, outline.href);
                if (url.Host == outlineUrl.Host)
                    yield return (url, outline);
            }

            if (outline.items != null)
            {
                foreach (var item in outline.items)
                    foreach (var url in GetPages(item))
                        yield return url;
            }
        }

        async Task MergePdf()
        {
            var pageNumber = 0;
            var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);

            foreach (var (url, node) in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!pageBytes.TryGetValue(node, out var bytes))
                    continue;

                var isCoverPage = IsCoverPage(url, outputFolder, outline.pdfCoverPage);

                var isTocPage = IsTocPage(url);
                if (isTocPage)
                {
                    // Refresh TOC page numbers
                    updatePageNumbers(pageNumbers);
                    updateHeadings(allHeadings);
                    var result = await printPdf(outline, url, 0); // 0 = don't extract headings from TOC page
                    bytes = result.bytes;

                    if (bytes == null)
                        continue;
                }

                using var document = PdfDocument.Open(bytes);
                for (var i = 1; i <= document.NumberOfPages; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    pageNumber++;

                    var pageBuilder = builder.AddPage(document, i, new AddPageOptions
                    {
                        CopyLinkFunc = x => CopyLink(node, x),
                    });

                    if (isCoverPage && !outline.pdfHeaderFooterOnCover)
                        continue;

                    if (isTocPage && !outline.pdfHeaderFooterOnToc)
                        continue;

                    var headerFooter = await printHeaderFooter(outline, pageNumber, numberOfPages, document.GetPage(i));
                    using var headerFooterDocument = PdfDocument.Open(headerFooter);

                    pageBuilder.NewContentStreamBefore();
                    pageBuilder.CurrentStream.Operations.Add(Push.Value);

                    // PDF produced by chromimum modifies global transformation matrix.
                    // Push and pop graphics state to fix graphics state
                    pageBuilder.CopyFrom(headerFooterDocument.GetPage(1));

                    pageBuilder.CurrentStream.Operations.Add(Pop.Value);
                }
            }
        }

        PdfAction CopyLink(Outline node, PdfAction action)
        {
            return action switch
            {
                GoToAction link => new GoToAction(new(pageNumbers[node] - 1 + link.Destination.PageNumber, link.Destination.Type, link.Destination.Coordinates)),
                UriAction url => HandleUriAction(url),
                _ => action,
            };

            PdfAction HandleUriAction(UriAction url)
            {
                if (!Uri.TryCreate(url.Uri, UriKind.Absolute, out var uri))
                    return url;

                if (!pagesByUrl.TryGetValue(CleanUrl(uri), out var pages))
                {
                    if (uri.Host == outlineUrl.Host && uri.Port == outlineUrl.Port)
                    {
                        // It is likely a 404 link if we are here
                        return new UriAction("");
                    }
                    return url;
                }

                if (!string.IsNullOrEmpty(uri.Fragment) && uri.Fragment.Length > 1)
                {
                    var name = uri.Fragment.Substring(1);
                    foreach (var (node, namedDests) in pages)
                    {
                        if (namedDests.TryGet(name, out var dest) && dest is not null)
                        {
                            return new GoToAction(new(pageNumbers[node] - 1 + dest.PageNumber, dest.Type, dest.Coordinates));
                        }
                    }

                    // what the page leaves out for the selection of the PDF has no place to go to
                    if (leftOutIds.Contains((CleanUrl(uri), Uri.UnescapeDataString(name))))
                    {
                        return new UriAction("");
                    }
                }

                return new GoToAction(CreatePageDestination(pageNumbers[pages[0].node]));
            }
        }

        ExplicitDestination CreatePageDestination(int pageNumber) =>
            new(pageNumber, ExplicitDestinationType.FitHorizontally, new(null, pageTops[pageNumber]));

        static Uri CleanUrl(Uri url) => new UriBuilder(url) { Query = null, Fragment = null }.Uri;

        Bookmarks CreateBookmarks(Outline[]? items)
        {
            var nextPageNumber = 1;
            var numbers = new Dictionary<Outline, int>();

            foreach (var node in Enumerate(items).Reverse())
            {
                if (pageNumbers.TryGetValue(node, out var pageNumber))
                    nextPageNumber = Math.Min(numberOfPages, pageNumber);
                else
                    pageNumber = nextPageNumber;
                numbers[node] = pageNumber;
            }

            return new(CreateBookmarksCore(items, 0).ToArray());

            IEnumerable<Outline> Enumerate(Outline[]? items)
            {
                if (items is null)
                    yield break;

                foreach (var item in items)
                {
                    yield return item;

                    foreach (var child in Enumerate(item.items))
                        yield return child;
                }
            }

            IEnumerable<BookmarkNode> CreateBookmarksCore(Outline[]? items, int level)
            {
                if (items is null)
                    yield break;

                foreach (var item in items)
                {
                    if (string.IsNullOrEmpty(item.name))
                        continue;

                    if (string.IsNullOrEmpty(item.href))
                    {
                        yield return new DocumentBookmarkNode(
                            item.name, level,
                            CreatePageDestination(numbers[item]),
                            CreateBookmarksCore(item.items, level + 1).ToArray());
                        continue;
                    }

                    if (!pagesByNode.TryGetValue(item, out var pageBuilder))
                    {
                        yield return new UriBookmarkNode(
                            item.name, level,
                            new Uri(outlineUrl, item.href).ToString(),
                            CreateBookmarksCore(item.items, level + 1).ToArray());
                        continue;
                    }

                    yield return new DocumentBookmarkNode(
                        item.name, level,
                        CreatePageDestination(numbers[item]),
                        CreateBookmarksCore(item.items, level + 1).ToArray());
                }
            }
        }
    }

    static HtmlTemplate TocHtmlTemplate(Uri baseUrl, Outline node, Dictionary<Outline, int>? pageNumbers, List<HeadingInfo>? headings)
    {
        // If pdfTocSource is "headings" and we have headings, generate TOC from headings
        if (node.pdfTocSource == "headings" && headings is { Count: > 0 })
        {
            var headingTocContent = BuildHeadingToc(baseUrl, headings);
            var cssStyles = Html($"""
                <style>
                  /* Indentation for heading levels */
                  li[data-level="1"] {"{ padding-left: 0; }"}
                  li[data-level="2"] {"{ padding-left: 1.5em; }"}
                  li[data-level="3"] {"{ padding-left: 3em; }"}
                  li[data-level="4"] {"{ padding-left: 4.5em; }"}
                  li[data-level="5"] {"{ padding-left: 6em; }"}
                  li[data-level="6"] {"{ padding-left: 7.5em; }"}
                </style>
                """);
            return Html($"""
                <!DOCTYPE html>
                <html>
                <head>
                  <link rel="stylesheet" href="/public/docfx.min.css">
                  <link rel="stylesheet" href="/public/main.css">
                  {cssStyles}
                </head>
                <body class="pdftoc">
                <h1>Table of Contents</h1>
                <ul>{headingTocContent}</ul>
                </body>
                </html>
                """);
        }

        // Default: generate TOC from toc.yml structure
        return Html($"""
            <!DOCTYPE html>
            <html>
            <head>
              <link rel="stylesheet" href="/public/docfx.min.css">
              <link rel="stylesheet" href="/public/main.css">
            </head>
            <body class="pdftoc">
            <h1>Table of Contents</h1>
            <ul>{node.items?.Select(TocNode)}</ul>
            </body>
            </html>
            """);

        HtmlTemplate TocNode(Outline node) => string.IsNullOrEmpty(node.name) ? default : Html(
            $"""
            <li>
              {(string.IsNullOrEmpty(node.href) ? node.name : Html(
                  $"""
                  <a href='{(string.IsNullOrEmpty(node.href) ? null : new Uri(baseUrl, node.href))}'>{node.name}
                  {(pageNumbers?.TryGetValue(node, out var n) is true ? Html($"<span class='spacer'></span> <span class='page-number'>{n}</span>") : null)}
                  </a>
                  """))}
              {(node.items?.Length > 0 ? Html($"<ul>{node.items.Select(TocNode)}</ul>") : null)}
            </li>
            """);
    }

    static HtmlTemplate BuildHeadingToc(Uri baseUrl, List<HeadingInfo> headings)
    {
        // Build flat list of all headings with CSS-based indentation for hierarchy
        var result = new List<HtmlTemplate>();

        foreach (var heading in headings)
        {
            var href = new UriBuilder(heading.PageUrl) { Fragment = heading.Id }.Uri;

            var pageNumberHtml = heading.PageNumber > 0
                ? Html($"<span class='spacer'></span> <span class='page-number'>{heading.PageNumber}</span>")
                : default;

            // Use data-level attribute for CSS styling of indentation
            var item = Html($"""
                <li data-level='{heading.Level}'>
                  <a href='{href}'>{heading.Text}
                  {pageNumberHtml}
                  </a>
                </li>
                """);

            result.Add(item);
        }

        return Html($"{result}");
    }

    /// <summary>
    /// Adds hidden links to headings to ensure Chromium saves heading anchors to named dests
    /// for cross page bookmark reference.
    /// </summary>
    static string EnsureHeadingAnchorScript =>
        """
        document.querySelectorAll('h1, h2, h3, h4, h5, h6').forEach(h => {
          if (h.id) {
            const a = document.createElement('a')
            a.href = '#' + h.id
            document.body.appendChild(a)
          }
        })
        """;

    static string DefaultFooterTemplate =>
        """
        <div style="width: 100%; font-size: 12px;">
          <div style="float: right; padding: 0 2em">
            <span class="pageNumber"></span> / <span class="totalPages"></span>
          </div>
        </div>
        """;

    /// <summary>
    /// Gets playwright page format from PdfPig's PageSize.
    /// </summary>
    private static bool TryGetPlaywrightPageFormat(PageSize pageSize, out string? pageFormat)
    {
        // List of supported formats: https://playwright.dev/dotnet/docs/api/class-page#page-pdf
        switch (pageSize)
        {
            case PageSize.Letter:
            case PageSize.Legal:
            case PageSize.Tabloid:
            case PageSize.Ledger:
            case PageSize.A0:
            case PageSize.A1:
            case PageSize.A2:
            case PageSize.A3:
            case PageSize.A4:
            case PageSize.A5:
            case PageSize.A6:
                pageFormat = pageSize.ToString();
                return true;

            // Following format is not supported format by playwright.
            // It need to use Width/Height settings.
            case PageSize.A7:
            case PageSize.A8:
            case PageSize.A9:
            case PageSize.A10:
            case PageSize.Custom:
            case PageSize.Executive:
            default:
                pageFormat = null;
                return false;
        }
    }

    /// <summary>
    /// Gets page size settings from PdfPig's Page object.
    /// </summary>
    private static (string Width, string Height, bool Landscape) GetPageSizeSettings(Page contentPage)
    {
        var isLandscape = contentPage.Width > contentPage.Height;
        var width = getMillimeter(contentPage.Width);
        var height = getMillimeter(contentPage.Height);

        return isLandscape
            ? (height, width, true) // On Landscape mode. It need to swap width/height.
            : (width, height, false);

        // Gets millimeter string representation from `pt` value.
        static string getMillimeter(double pt)
        {
            const double MillimeterPerInch = 25.4d;
            const double Dpi = 72d; // Use Default DPI of PDF.
            return $"{Math.Round(pt * MillimeterPerInch / Dpi)}mm";
        }
    }

    // Gets StringComparison instance for path string.
    private static StringComparison GetStringComparison()
    {
        return PathUtility.IsPathCaseInsensitive()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    private static bool IsTocPage(Uri url) => url.AbsolutePath.StartsWith("/_pdftoc/");

    private static bool IsCoverPage(Uri pageUri, string baseFolder, string? pdfCoverPage)
    {
        Debug.Assert(Path.IsPathFullyQualified(baseFolder));

        if (string.IsNullOrEmpty(pdfCoverPage))
            return false;

        string pagePath = pageUri.AbsolutePath.TrimStart('/');
        string coverPagePath = PathUtility.MakeRelativePath(baseFolder, Path.GetFullPath(Path.Combine(baseFolder, pdfCoverPage)));

        return pagePath.Equals(coverPagePath, GetStringComparison());
    }
}
