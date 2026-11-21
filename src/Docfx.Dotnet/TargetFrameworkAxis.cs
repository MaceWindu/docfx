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
    /// The ids of the values, in their order.
    /// </summary>
    public IEnumerable<string> Values => _values.Select(v => v.Id);

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

        // The schema forbids a null, which docfx does not check the configuration against.
        var axis = axes[0];
        if (axis is null || string.IsNullOrWhiteSpace(axis.Name) || axis.Values is not { Count: > 0 })
        {
            return Invalid("An axis needs a 'name' and at least one value.");
        }

        var values = new List<(string, string, GlobMatcher[])>();
        foreach (var value in axis.Values)
        {
            // Pages list units separated by spaces, e.g. `data-applies-to="ef9:net8.0 ef9:netstandard2.0"`.
            if (value is null || string.IsNullOrWhiteSpace(value.Id) || value.Id.Contains(Separator) || value.Id.Any(char.IsWhiteSpace)
                || value.Files is not { Count: > 0 } || value.Files.Any(string.IsNullOrWhiteSpace))
            {
                return Invalid($"Every value of axis '{axis.Name}' needs an 'id' without '{Separator}' or whitespace and at least one pattern in 'files'.");
            }

            // Values that differ only in case read as one to a reader, and to whatever a template keys on them.
            if (values.FirstOrDefault(v => string.Equals(v.Item1, value.Id, StringComparison.OrdinalIgnoreCase)).Item1 is { } same)
            {
                return Invalid(same == value.Id
                    ? $"Axis '{axis.Name}' has the value '{value.Id}' more than once."
                    : $"Axis '{axis.Name}' has the values '{same}' and '{value.Id}', which differ only in case.");
            }

            values.Add((value.Id, value.Label ?? value.Id, value.Files.Select(f => new GlobMatcher(f)).ToArray()));
        }

        // named in any case, as the values are told apart
        var defaultValue = axis.Default is null ? values[0].Item1
            : values.FirstOrDefault(v => string.Equals(v.Item1, axis.Default, StringComparison.OrdinalIgnoreCase)).Item1;
        if (defaultValue is null)
        {
            return Invalid($"The default '{axis.Default}' of axis '{axis.Name}' is not one of its values.");
        }

        return new(axis.Name, values, defaultValue, bases.ToArray());

        static TargetFrameworkAxis? Invalid(string message)
        {
            Logger.LogError($"Invalid 'axes': {message}", code: ErrorCodes.Metadata.InvalidAxis);
            return null;
        }
    }

    /// <summary>
    /// Returns the id of the first value whose patterns match <paramref name="projectPath"/>, relative to any of
    /// the <c>src</c> folders that hold it: the order of the values decides, not that of the folders.
    /// </summary>
    public string? Match(string projectPath)
    {
        var relatives = _bases
            .Select(basePath => Path.GetRelativePath(basePath, projectPath).Replace('\\', '/'))
            .Where(relative => !relative.StartsWith("../", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            .ToList();

        foreach (var (id, _, files) in _values)
        {
            if (relatives.Any(relative => files.Any(f => f.Match(relative))))
            {
                return id;
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
