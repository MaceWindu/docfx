// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using Docfx.DataContracts.Common;
using Newtonsoft.Json;
using YamlDotNet.Serialization;

namespace Docfx.DataContracts.ManagedReference;

/// <summary>
/// The content an API has in some target frameworks where it differs from the rest of the item.
/// Only the fields that differ are set.
/// </summary>
public class ItemVariantViewModel
{
    [YamlMember(Alias = Constants.PropertyName.AppliesTo)]
    [JsonProperty(Constants.PropertyName.AppliesTo)]
    [JsonPropertyName(Constants.PropertyName.AppliesTo)]
    public List<AppliesToViewModel> AppliesTo { get; set; }

    [YamlMember(Alias = "summary")]
    [JsonProperty("summary")]
    [JsonPropertyName("summary")]
    [MarkdownContent]
    public string Summary { get; set; }

    [YamlMember(Alias = "remarks")]
    [JsonProperty("remarks")]
    [JsonPropertyName("remarks")]
    [MarkdownContent]
    public string Remarks { get; set; }

    [YamlMember(Alias = "example")]
    [JsonProperty("example")]
    [JsonPropertyName("example")]
    [MarkdownContent]
    public List<string> Examples { get; set; }

    [YamlMember(Alias = "syntax")]
    [JsonProperty("syntax")]
    [JsonPropertyName("syntax")]
    public SyntaxDetailViewModel Syntax { get; set; }

    [YamlMember(Alias = "overridden")]
    [JsonProperty("overridden")]
    [JsonPropertyName("overridden")]
    [UniqueIdentityReference]
    public string Overridden { get; set; }

    [YamlMember(Alias = "exceptions")]
    [JsonProperty("exceptions")]
    [JsonPropertyName("exceptions")]
    public List<ExceptionInfo> Exceptions { get; set; }

    [YamlMember(Alias = "seealso")]
    [JsonProperty("seealso")]
    [JsonPropertyName("seealso")]
    public List<LinkInfo> SeeAlsos { get; set; }

    [YamlIgnore]
    [Newtonsoft.Json.JsonIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    [UniqueIdentityReference]
    public List<string> SeeAlsosUidReference => SeeAlsos?.Where(s => s.LinkType == LinkType.CRef).Select(s => s.LinkId).ToList();

    [YamlMember(Alias = "inheritance")]
    [JsonProperty("inheritance")]
    [JsonPropertyName("inheritance")]
    [UniqueIdentityReference]
    public List<string> Inheritance { get; set; }

    [YamlMember(Alias = "attributes")]
    [JsonProperty("attributes")]
    [JsonPropertyName("attributes")]
    public List<AttributeInfo> Attributes { get; set; }
}
