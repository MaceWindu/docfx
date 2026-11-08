// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using Newtonsoft.Json;
using YamlDotNet.Serialization;

namespace Docfx.DataContracts.ManagedReference;

/// <summary>
/// A set of target frameworks an API exists in.
/// </summary>
public class AppliesToViewModel
{
    [YamlMember(Alias = "frameworks")]
    [JsonProperty("frameworks")]
    [JsonPropertyName("frameworks")]
    public List<string> Frameworks { get; set; }
}
