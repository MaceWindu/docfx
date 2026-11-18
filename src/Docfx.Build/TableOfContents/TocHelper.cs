// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using Docfx.Common;
using Docfx.DataContracts.Common;
using Docfx.Plugins;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using Constants = Docfx.DataContracts.Common.Constants;

namespace Docfx.Build.TableOfContents;

public static class TocHelper
{
    internal static List<FileModel> ResolveToc(ImmutableList<FileModel> models)
    {
        var tocCache = new Dictionary<string, TocItemInfo>(FilePathComparer.OSPlatformSensitiveStringComparer);
        var nonReferencedTocModels = new List<FileModel>();

        foreach (var model in models)
        {
            tocCache[model.OriginalFileAndType.FullPath] = new TocItemInfo(model.OriginalFileAndType, (TocItemViewModel)model.Content);
        }
        var tocResolver = new TocResolver(tocCache);
        foreach (var key in tocCache.Keys.ToList())
        {
            tocCache[key] = tocResolver.Resolve(key);
        }

        foreach (var model in models)
        {
            // If the TOC file is referenced by other TOC, decrease the score
            var tocItemInfo = tocCache[model.OriginalFileAndType.FullPath];
            if (tocItemInfo.IsReferenceToc && tocItemInfo.Content.Order is null)
                tocItemInfo.Content.Order = 100;

            model.Content = tocItemInfo.Content;
            nonReferencedTocModels.Add(model);
        }

        return nonReferencedTocModels;
    }

    public static TocItemViewModel LoadSingleToc(string file)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);

        if (!EnvironmentContext.FileAbstractLayer.Exists(file))
        {
            throw new FileNotFoundException($"File {file} does not exist.", file);
        }

        var fileType = Utility.GetTocFileType(file);
        try
        {
            switch (fileType)
            {
                case TocFileType.Markdown:
                    return new()
                    {
                        Items = MarkdownTocReader.LoadToc(EnvironmentContext.FileAbstractLayer.ReadAllText(file), file)
                    };
                case TocFileType.Yaml:
                    {
                        var yaml = EnvironmentContext.FileAbstractLayer.ReadAllText(file);
                        var toc = DeserializeYamlToc(yaml)
                            ?? throw new NotSupportedException($"{file} is not a valid TOC file.");
                        KeepIdsAsText(toc, file);
                        return toc;
                    }
                default:
                    throw new NotSupportedException($"{file} is not a valid TOC file, supported TOC files should be either \"{Constants.TableOfContents.MarkdownTocFileName}\" or \"{Constants.TableOfContents.YamlTocFileName}\".");
            }
        }
        catch (Exception e)
        {
            var message = $"{file} is not a valid TOC File: {e}";
            Logger.LogError(message, code: ErrorCodes.Toc.InvalidTocFile);
            throw new DocumentException(message, e);
        }
    }

    /// <summary>
    /// The keys whose values name frameworks and axis values by id, which pages and PDFs compare as text.
    /// </summary>
    internal static readonly string[] IdKeys = ["appliesTo", "axis", "pdfFramework", "pdfAxisValue"];

    /// <summary>
    /// YAML reads an unquoted id that looks like a number as one, so a table of contents written by hand that
    /// names the axis value `3.10` would name 3.1, which pages do not match; `true` and `false`, also as `True`,
    /// `TRUE`, `False` and `FALSE`, are read as booleans.
    /// What it holds is used as text, `3.1` or `true`, with a warning to quote it: `docfx metadata` quotes such
    /// ids itself.
    /// </summary>
    private static void KeepIdsAsText(TocItemViewModel item, string file)
    {
        foreach (var key in IdKeys)
        {
            if (item.Metadata.TryGetValue(key, out var value) && value is not null)
            {
                item.Metadata[key] = AsText(value, key);
            }
        }
        // an empty entry, `-` alone, is null
        foreach (var child in item.Items ?? [])
        {
            if (child is not null)
            {
                KeepIdsAsText(child, file);
            }
        }

        object AsText(object value, string key)
        {
            switch (value)
            {
                case int or long or ulong or double:
                    var text = Convert.ToString(value, CultureInfo.InvariantCulture)!;
                    Logger.LogWarning(
                        $"'{key}' of table of contents '{file}' holds {text} unquoted, which YAML reads as a number, so it is used as '{text}': " +
                        "quote a value that looks like a number, such as \"3.10\", to keep it as written.",
                        file: file,
                        code: WarningCodes.Build.UnquotedTocValue);
                    return text;
                case bool flag:
                    // YAML reads three spellings of each as a boolean and keeps none of them, so all are named
                    var word = flag ? "true" : "false";
                    var spellings = flag ? "true, True or TRUE" : "false, False or FALSE";
                    Logger.LogWarning(
                        $"'{key}' of table of contents '{file}' holds {spellings} unquoted, which YAML reads as a boolean, so it is used as '{word}': " +
                        "quote it to keep it as written.",
                        file: file,
                        code: WarningCodes.Build.UnquotedTocValue);
                    return word;
                case IDictionary map:
                    foreach (var name in map.Keys.Cast<object>().ToList())
                    {
                        if (map[name] is { } entry)
                        {
                            map[name] = AsText(entry, key);
                        }
                    }
                    return map;
                case IList list:
                    for (var i = 0; i < list.Count; i++)
                    {
                        if (list[i] is { } entry)
                        {
                            list[i] = AsText(entry, key);
                        }
                    }
                    return list;
                default:
                    return value;
            }
        }
    }

    private static TocItemViewModel DeserializeYamlToc(string yaml)
    {
        // Parse yaml content to determine TOC type (`List<TocItemViewModel>` or TocItemViewModel).
        var parser = new Parser(new Scanner(new StringReader(yaml), skipComments: true));
        bool isListItems = parser.TryConsume<StreamStart>(out var _)
                        && parser.TryConsume<DocumentStart>(out var _)
                        && parser.TryConsume<SequenceStart>(out var _);

        return isListItems
            ? new TocItemViewModel { Items = YamlUtility.Deserialize<List<TocItemViewModel>>(new StringReader(yaml)) }
            : YamlUtility.Deserialize<TocItemViewModel>(new StringReader(yaml));
    }
}
