// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using Docfx.DataContracts.Common;
using Docfx.DataContracts.ManagedReference;
using Newtonsoft.Json;
using YamlDotNet.Serialization;

namespace Docfx.Build.ManagedReference.BuildOutputs;

/// <summary>
/// The content an API has in some target frameworks where it differs from the rest of the item.
/// </summary>
public class ApiVariantBuildOutput
{
    [YamlMember(Alias = Constants.PropertyName.AppliesTo)]
    [JsonProperty(Constants.PropertyName.AppliesTo)]
    [JsonPropertyName(Constants.PropertyName.AppliesTo)]
    public List<AppliesToViewModel> AppliesTo { get; set; }

    [YamlMember(Alias = "summary")]
    [JsonProperty("summary")]
    [JsonPropertyName("summary")]
    public string Summary { get; set; }

    [YamlMember(Alias = "remarks")]
    [JsonProperty("remarks")]
    [JsonPropertyName("remarks")]
    public string Remarks { get; set; }

    [YamlMember(Alias = "example")]
    [JsonProperty("example")]
    [JsonPropertyName("example")]
    public List<string> Examples { get; set; }

    [YamlMember(Alias = "syntax")]
    [JsonProperty("syntax")]
    [JsonPropertyName("syntax")]
    public ApiSyntaxBuildOutput Syntax { get; set; }

    [YamlMember(Alias = "overridden")]
    [JsonProperty("overridden")]
    [JsonPropertyName("overridden")]
    public ApiNames Overridden { get; set; }

    [YamlMember(Alias = "exceptions")]
    [JsonProperty("exceptions")]
    [JsonPropertyName("exceptions")]
    public List<ApiExceptionInfoBuildOutput> Exceptions { get; set; }

    [YamlMember(Alias = "seealso")]
    [JsonProperty("seealso")]
    [JsonPropertyName("seealso")]
    public List<ApiLinkInfoBuildOutput> SeeAlsos { get; set; }

    [YamlMember(Alias = "inheritance")]
    [JsonProperty("inheritance")]
    [JsonPropertyName("inheritance")]
    public List<ApiReferenceBuildOutput> Inheritance { get; set; }

    [YamlMember(Alias = "attributes")]
    [JsonProperty("attributes")]
    [JsonPropertyName("attributes")]
    public List<AttributeInfo> Attributes { get; set; }

    private bool _needExpand = true;

    /// <summary>
    /// Creates the output with its references unresolved, as a member of a page is created before the
    /// references of the page are known. <see cref="Expand"/> resolves them.
    /// </summary>
    public static ApiVariantBuildOutput FromModel(ItemVariantViewModel model, string[] supportedLanguages)
    {
        if (model == null)
        {
            return null;
        }

        return new ApiVariantBuildOutput
        {
            AppliesTo = model.AppliesTo,
            Summary = model.Summary,
            Remarks = model.Remarks,
            Examples = model.Examples,
            Syntax = ApiSyntaxBuildOutput.FromModel(model.Syntax, supportedLanguages),
            Overridden = ApiNames.FromUid(model.Overridden),
            Exceptions = model.Exceptions?.Select(ApiExceptionInfoBuildOutput.FromModel).ToList(),
            SeeAlsos = model.SeeAlsos?.Select(ApiLinkInfoBuildOutput.FromModel).ToList(),
            Inheritance = model.Inheritance?.Select(uid => new ApiReferenceBuildOutput { Uid = uid }).ToList(),
            Attributes = model.Attributes,
        };
    }

    public static ApiVariantBuildOutput FromModel(ItemVariantViewModel model, Dictionary<string, ApiReferenceBuildOutput> references, string[] supportedLanguages)
    {
        var output = FromModel(model, supportedLanguages);
        output?.Expand(references, supportedLanguages);
        return output;
    }

    public void Expand(Dictionary<string, ApiReferenceBuildOutput> references, string[] supportedLanguages)
    {
        if (_needExpand)
        {
            _needExpand = false;
            Syntax?.Expand(references, supportedLanguages);
            Overridden = ApiBuildOutputUtility.GetApiNames(Overridden?.Uid, references, supportedLanguages);
            Exceptions?.ForEach(e => e.Expand(references, supportedLanguages));
            SeeAlsos?.ForEach(e => e.Expand(references, supportedLanguages));
            Inheritance = Inheritance?.Select(i => ApiBuildOutputUtility.GetReferenceViewModel(i.Uid, references, supportedLanguages)).ToList();
        }
    }
}
