// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Docfx.Dotnet;

/// <summary>
/// How an API differs in some of the target frameworks it exists in from the framework that provides
/// its content.
/// </summary>
internal class MetadataItemVariant
{
    public List<string> Frameworks { get; init; }

    /// <summary>
    /// Holds the fields that differ. Every other field is left unset.
    /// </summary>
    public MetadataItem Content { get; init; }

    /// <summary>
    /// Whether the API overrides nothing in these frameworks, while it does in the framework that provides its content.
    /// </summary>
    public bool NoOverridden { get; init; }

    /// <summary>
    /// Whether the API has no base type in these frameworks, as an interface has not, while it has one in the
    /// framework that provides its content: the variant's unset <c>Inheritance</c> would read as the same one.
    /// </summary>
    public bool NoInheritance { get; init; }
}
