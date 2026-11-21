// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using Docfx.DataContracts.ManagedReference;

namespace Docfx.Dotnet;

internal class SetDerivedClass : IResolverPipeline
{
    // A set: a type that implements an interface twice, as `IConverter<int>` and `IConverter<string>`, derives from it once.
    private readonly Dictionary<string, HashSet<string>> _derivedClassMapping = [];

    // base class -> derived class -> the frameworks it derives from that base in
    private readonly Dictionary<string, Dictionary<string, List<string>>> _derivedClassFrameworks = [];

    public void Run(MetadataModel yaml, ResolverContext context)
    {
        // The pipeline is static and runs once for each metadata item: the frameworks an earlier run recorded for the
        // same classes, as a build of the same project in the same process does, would hide where they differ.
        _derivedClassFrameworks.Clear();

        if (yaml.Members is { Count: > 0 })
        {
            UpdateDerivedClassMapping(yaml.Members, context.References);
            AppendDerivedClass(yaml.Members);
        }
    }

    private void UpdateDerivedClassMapping(List<MetadataItem> items, Dictionary<string, ReferenceItem> reference)
    {
        foreach (var item in items ?? Enumerable.Empty<MetadataItem>())
        {
            // Handle class inheritance, including the base classes of other target frameworks, recorded as variants
            var superClasses = new HashSet<string>();
            foreach (var inheritance in (item.Variants ?? []).Select(v => v.Content?.Inheritance).Prepend(item.Inheritance))
            {
                if (GetSuperClass(inheritance, reference) is { } superClass && superClasses.Add(superClass))
                {
                    AddDerived(superClass, item.Name);
                }
            }

            // A class whose base type differs between frameworks derives from each base in only some of them,
            // which the page of the base marks.
            if (item.AppliesTo is { Count: > 0 } frameworks)
            {
                foreach (var framework in frameworks)
                {
                    var inheritance = item.Variants?.FirstOrDefault(v => v.Frameworks.Contains(framework) && v.Content?.Inheritance is { Count: > 0 })?.Content.Inheritance ?? item.Inheritance;
                    if (GetSuperClass(inheritance, reference) is { } superClass)
                    {
                        AddDerivedIn(superClass, item.Name, framework);
                    }
                }
            }

            // Handle interface implementations
            var implements = item.Implements;
            if (implements is { Count: > 0 })
            {
                foreach (var implementedInterface in implements)
                {
                    var superClass = implementedInterface;

                    if (reference.TryGetValue(superClass, out var referenceItem))
                    {
                        superClass = referenceItem.Definition ?? superClass;
                    }

                    AddDerived(superClass, item.Name);

                    // A type implementing an interface in only some frameworks is marked on the page of the interface.
                    foreach (var framework in item.ImplementsAppliesTo?.GetValueOrDefault(implementedInterface) ?? [])
                    {
                        AddDerivedIn(superClass, item.Name, framework);
                    }
                }
            }
        }
    }

    private void AddDerived(string superClass, string derivedClass)
    {
        ref var derivedClasses = ref CollectionsMarshal.GetValueRefOrAddDefault(_derivedClassMapping, superClass, out var exists);
        if (exists)
            derivedClasses.Add(derivedClass);
        else
            derivedClasses = [derivedClass];
    }

    private void AddDerivedIn(string superClass, string derivedClass, string framework)
    {
        ref var derivedIn = ref CollectionsMarshal.GetValueRefOrAddDefault(_derivedClassFrameworks, superClass, out var exists);
        if (!exists)
        {
            derivedIn = [];
        }

        ref var units = ref CollectionsMarshal.GetValueRefOrAddDefault(derivedIn, derivedClass, out var known);
        if (!known)
            units = [framework];
        else if (!units.Contains(framework))
            units.Add(framework);
    }

    private void AppendDerivedClass(List<MetadataItem> items)
    {
        foreach (var item in items ?? Enumerable.Empty<MetadataItem>())
        {
            switch (item.Type)
            {
                case MemberType.Class:
                case MemberType.Interface:
                    if (_derivedClassMapping.TryGetValue(item.Name, out var derivedClasses))
                    {
                        item.DerivedClasses = [.. derivedClasses.Order()];
                        if (_derivedClassFrameworks.TryGetValue(item.Name, out var derivedIn))
                        {
                            item.DerivedClassesAppliesTo = derivedIn;
                        }
                    }
                    continue;
            }
        }
    }

    // The base class a type derives from directly, by its definition; null for System.Object.
    private static string GetSuperClass(List<string> inheritance, Dictionary<string, ReferenceItem> reference)
    {
        if (inheritance is not { Count: > 0 })
        {
            return null;
        }

        var superClass = inheritance[inheritance.Count - 1];
        if (reference.TryGetValue(superClass, out ReferenceItem referenceItem))
        {
            superClass = referenceItem.Definition ?? superClass;
        }

        // ignore System.Object's derived class
        return superClass == "System.Object" ? null : superClass;
    }
}
