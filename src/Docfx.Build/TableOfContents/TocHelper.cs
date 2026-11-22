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
using YamlDotNet.RepresentationModel;
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
                        KeepIdsAsText(toc, file, yaml);
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
    /// The keys whose values name frameworks and axis values by id, which pages compare as text.
    /// </summary>
    internal static readonly string[] IdKeys = ["appliesTo", "axis"];

    /// <summary>
    /// YAML reads an unquoted id that looks like a number as one, so a table of contents written by hand that
    /// names the axis value `3.10` would name 3.1, which pages do not match; `true` and `false`, also as `True`,
    /// `TRUE`, `False` and `FALSE`, are read as booleans.
    /// What it holds is used as text, `3.1` or `true`, with a warning to quote it that names what the file says:
    /// `docfx metadata` quotes such ids itself.
    /// </summary>
    private static void KeepIdsAsText(TocItemViewModel toc, string file, string yaml)
    {
        // The path of each value from the root: the indexes of the entries, the key, then the positions in what it
        // holds, by which the file is read again for what it says, only when there is such a value.
        var unquoted = new List<(string Key, object Value, string Text, List<object> Path)>();
        var path = new List<object>();
        Visit(toc);
        if (unquoted.Count == 0)
        {
            return;
        }

        var root = LoadYamlRoot(yaml);
        foreach (var (key, value, text, at) in unquoted)
        {
            var written = FindWrittenScalar(root, at, value);
            if (value is bool)
            {
                // YAML reads three spellings of each as a boolean and keeps none of them: without the file, all are named
                var spellings = written ?? (text == "true" ? "true, True or TRUE" : "false, False or FALSE");
                Logger.LogWarning(
                    $"'{key}' of table of contents '{file}' holds {spellings} unquoted, which YAML reads as a boolean, so it is used as '{text}': " +
                    "quote it to keep it as written.",
                    file: file,
                    code: WarningCodes.Build.UnquotedTocValue);
            }
            else
            {
                Logger.LogWarning(
                    $"'{key}' of table of contents '{file}' holds {written ?? text} unquoted, which YAML reads as a number, so it is used as '{text}': " +
                    "quote a value that looks like a number, such as \"3.10\", to keep it as written.",
                    file: file,
                    code: WarningCodes.Build.UnquotedTocValue);
            }
        }

        void Visit(TocItemViewModel item)
        {
            foreach (var key in IdKeys)
            {
                if (item.Metadata.TryGetValue(key, out var value) && value is not null)
                {
                    path.Add(key);
                    item.Metadata[key] = AsText(value, key);
                    path.RemoveAt(path.Count - 1);
                }
            }
            // an empty entry, `-` alone, is null
            for (var i = 0; i < (item.Items?.Count ?? 0); i++)
            {
                if (item.Items[i] is { } child)
                {
                    path.Add(i);
                    Visit(child);
                    path.RemoveAt(path.Count - 1);
                }
            }
        }

        object AsText(object value, string key)
        {
            switch (value)
            {
                case int or long or ulong or double or bool:
                    var text = value is bool flag ? (flag ? "true" : "false") : Convert.ToString(value, CultureInfo.InvariantCulture)!;
                    unquoted.Add((key, value, text, [.. path]));
                    return text;
                case IDictionary map:
                    var names = map.Keys.Cast<object>().ToList();
                    for (var i = 0; i < names.Count; i++)
                    {
                        if (map[names[i]] is { } entry)
                        {
                            path.Add(i);
                            map[names[i]] = AsText(entry, key);
                            path.RemoveAt(path.Count - 1);
                        }
                    }
                    return map;
                case IList list:
                    for (var i = 0; i < list.Count; i++)
                    {
                        if (list[i] is { } entry)
                        {
                            path.Add(i);
                            list[i] = AsText(entry, key);
                            path.RemoveAt(path.Count - 1);
                        }
                    }
                    return list;
                default:
                    return value;
            }
        }
    }

    private static YamlNode LoadYamlRoot(string yaml)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            return stream.Documents.Count > 0 ? stream.Documents[0].RootNode : null;
        }
        catch (YamlException)
        {
            return null;
        }
    }

    /// <summary>
    /// What the file says for a value YAML read as a number or a boolean, at its path from the root; nothing where
    /// the file does not have it there as it was read, such as through a merge key.
    /// </summary>
    private static string FindWrittenScalar(YamlNode root, List<object> path, object value)
    {
        var node = root;
        // the entries: those of a file that is a list are the list itself
        var isList = root is YamlSequenceNode;
        var step = 0;
        for (; step < path.Count && path[step] is int index; step++)
        {
            var items = isList
                ? node as YamlSequenceNode
                : node is YamlMappingNode entry && entry.Children.TryGetValue(new YamlScalarNode("items"), out var children) ? children as YamlSequenceNode : null;
            if (items is null || index >= items.Children.Count)
            {
                return null;
            }
            node = items.Children[index];
            isList = false;
        }

        if (step >= path.Count || path[step] is not string key ||
            node is not YamlMappingNode mapping || !mapping.Children.TryGetValue(new YamlScalarNode(key), out node))
        {
            return null;
        }

        for (step++; step < path.Count; step++)
        {
            var position = (int)path[step];
            node = node switch
            {
                YamlMappingNode map when position < map.Children.Count => map.Children.ElementAt(position).Value,
                YamlSequenceNode list when position < list.Children.Count => list.Children[position],
                _ => null,
            };
        }

        if (node is not YamlScalarNode { Style: ScalarStyle.Plain, Value: { } written })
        {
            return null;
        }
        return value switch
        {
            bool flag => string.Equals(written, flag ? "true" : "false", StringComparison.OrdinalIgnoreCase) ? written : null,
            _ => double.TryParse(written, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                number == Convert.ToDouble(value, CultureInfo.InvariantCulture) ? written : null,
        };
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
