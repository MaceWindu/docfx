// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace Docfx;

/// <summary>
/// An axis, besides the target framework, along which the projects of a metadata item differ, such as
/// the version of a dependency they are built against.
/// </summary>
internal class MetadataAxisConfig
{
    /// <summary>
    /// The name of the axis, e.g. `EF Core`.
    /// </summary>
    [JsonProperty("name")]
    [JsonPropertyName("name")]
    public string Name { get; set; }

    /// <summary>
    /// The values of the axis, in the order they are shown.
    /// </summary>
    [JsonProperty("values")]
    [JsonPropertyName("values")]
    public List<MetadataAxisValueConfig> Values { get; set; }

    /// <summary>
    /// The id of the value that provides the content of an API that exists in several.
    /// </summary>
    [JsonProperty("default")]
    [JsonPropertyName("default")]
    public string Default { get; set; }
}

/// <summary>
/// A value of a <see cref="MetadataAxisConfig"/>, and the projects that have it.
/// </summary>
internal class MetadataAxisValueConfig
{
    [JsonProperty("id")]
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonProperty("label")]
    [JsonPropertyName("label")]
    public string Label { get; set; }

    /// <summary>
    /// Glob patterns of the project files that have this value, relative to the `src` of the item.
    /// </summary>
    [JsonProperty("files")]
    [JsonPropertyName("files")]
    public List<string> Files { get; set; }
}
