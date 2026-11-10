// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Docfx.Common;
using Docfx.Glob;

#nullable enable

namespace Docfx.Dotnet;

/// <summary>
/// A resolved <see cref="MetadataAxisConfig"/>. Together with the target framework, its value names the
/// unit an API is documented for, written <c>value:framework</c>, e.g. <c>ef9:net8.0</c>.
/// </summary>
internal sealed class TargetFrameworkAxis
{
    private const char Separator = ':';

    private readonly List<(string Id, string Label, GlobMatcher[] Files)> _values;
    private readonly string[] _bases;

    private TargetFrameworkAxis(string name, List<(string, string, GlobMatcher[])> values, string defaultValue, string[] bases)
    {
        Name = name;
        _values = values;
        Default = defaultValue;
        _bases = bases;
    }

    public string Name { get; }

    public string Default { get; }

    /// <summary>
    /// Resolves the axes of a metadata item, logging an <see cref="ErrorCodes.Metadata.InvalidAxis"/>
    /// error and returning <see langword="null"/> when they cannot be used.
    /// </summary>
    public static TargetFrameworkAxis? Create(List<MetadataAxisConfig>? axes, IEnumerable<string> bases)
    {
        if (axes is not { Count: > 0 })
        {
            return null;
        }

        if (axes.Count > 1)
        {
            return Invalid("Only one axis is supported.");
        }

        var axis = axes[0];
        if (string.IsNullOrWhiteSpace(axis.Name) || axis.Values is not { Count: > 0 })
        {
            return Invalid("An axis needs a 'name' and at least one value.");
        }

        var values = new List<(string, string, GlobMatcher[])>();
        foreach (var value in axis.Values)
        {
            if (string.IsNullOrWhiteSpace(value.Id) || value.Id.Contains(Separator) || value.Files is not { Count: > 0 })
            {
                return Invalid($"Every value of axis '{axis.Name}' needs an 'id' without '{Separator}' and at least one pattern in 'files'.");
            }

            if (values.Any(v => v.Item1 == value.Id))
            {
                return Invalid($"Axis '{axis.Name}' has the value '{value.Id}' more than once.");
            }

            values.Add((value.Id, value.Label ?? value.Id, value.Files.Select(f => new GlobMatcher(f)).ToArray()));
        }

        if (axis.Default is not null && !values.Any(v => v.Item1 == axis.Default))
        {
            return Invalid($"The default '{axis.Default}' of axis '{axis.Name}' is not one of its values.");
        }

        return new(axis.Name, values, axis.Default ?? values[0].Item1, bases.ToArray());

        static TargetFrameworkAxis? Invalid(string message)
        {
            Logger.LogError($"Invalid 'axes': {message}", code: ErrorCodes.Metadata.InvalidAxis);
            return null;
        }
    }

    /// <summary>
    /// Returns the id of the first value whose patterns match <paramref name="projectPath"/>.
    /// </summary>
    public string? Match(string projectPath)
    {
        foreach (var basePath in _bases)
        {
            var relative = Path.GetRelativePath(basePath, projectPath).Replace('\\', '/');
            if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                continue;
            }

            foreach (var (id, _, files) in _values)
            {
                if (files.Any(f => f.Match(relative)))
                {
                    return id;
                }
            }
        }

        return null;
    }

    public string GetLabel(string id) => _values.FirstOrDefault(v => v.Id == id).Label ?? id;

    public int IndexOf(string? id) => _values.FindIndex(v => v.Id == id);

    public static string GetUnit(string value, string framework) => $"{value}{Separator}{framework}";

    /// <summary>
    /// Splits a unit into its value, <see langword="null"/> when there is no axis, and its framework.
    /// </summary>
    public static (string? Value, string Framework) SplitUnit(string unit)
    {
        var index = unit.IndexOf(Separator);
        return index < 0 ? (null, unit) : (unit[..index], unit[(index + 1)..]);
    }
}
