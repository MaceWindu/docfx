// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using Newtonsoft.Json;
using YamlDotNet.Serialization;

namespace Docfx.DataContracts.ManagedReference;

/// <summary>
/// A set of target frameworks an API exists in, for one value of an axis when there is one.
/// </summary>
public class AppliesToViewModel
{
    [YamlMember(Alias = "version")]
    [JsonProperty("version")]
    [JsonPropertyName("version")]
    public string Version { get; set; }

    [YamlMember(Alias = "label")]
    [JsonProperty("label")]
    [JsonPropertyName("label")]
    public string Label { get; set; }

    [YamlMember(Alias = "frameworks")]
    [JsonProperty("frameworks")]
    [JsonPropertyName("frameworks")]
    public List<string> Frameworks { get; set; }
}
