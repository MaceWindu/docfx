// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

#nullable enable

namespace Docfx;

/// <summary>
/// The value of the <c>uidPrefix</c> metadata option, which accepts two forms.
/// </summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
[Newtonsoft.Json.JsonConverter(typeof(UidPrefixSettingConverter.NewtonsoftJsonConverter))]
[System.Text.Json.Serialization.JsonConverter(typeof(UidPrefixSettingConverter.SystemTextJsonConverter))]
internal class UidPrefixSetting
{
    /// <summary>
    /// The object form, mapping an assembly name to its prefix. This is the form to prefer: the maps of
    /// all metadata entries are combined into one before any of them is processed, which is what lets an
    /// entry address APIs documented by another entry.
    /// </summary>
    public Dictionary<string, string>? AssemblyPrefixes { get; init; }

    /// <summary>
    /// The string form, a prefix for the assemblies documented by the entry that declares it. Needed only
    /// when several entries document assemblies that share an assembly name, which the object form cannot
    /// tell apart. It takes precedence over the object form for this entry's own assemblies.
    /// </summary>
    public string? Prefix { get; init; }

    private string DebuggerDisplay => Prefix ?? $"{AssemblyPrefixes?.Count ?? 0} assemblies";

    public static implicit operator UidPrefixSetting(string prefix) => new() { Prefix = prefix };

    public static implicit operator UidPrefixSetting(Dictionary<string, string> assemblyPrefixes) => new() { AssemblyPrefixes = assemblyPrefixes };
}
