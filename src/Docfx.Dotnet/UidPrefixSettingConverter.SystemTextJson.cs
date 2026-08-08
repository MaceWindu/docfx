// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Docfx;

internal partial class UidPrefixSettingConverter
{
    internal class SystemTextJsonConverter : JsonConverter<UidPrefixSetting>
    {
        public override UidPrefixSetting Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    return null;

                case JsonTokenType.String:
                    return new UidPrefixSetting { Prefix = reader.GetString() };

                case JsonTokenType.StartObject:
                    var assemblyPrefixes = new Dictionary<string, string>();
                    foreach (var property in JsonElement.ParseValue(ref reader).EnumerateObject())
                    {
                        assemblyPrefixes[property.Name] = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString();
                    }
                    return new UidPrefixSetting { AssemblyPrefixes = assemblyPrefixes };

                default:
                    throw new JsonException($"TokenType({reader.TokenType}) is not supported for 'uidPrefix'.");
            }
        }

        public override void Write(Utf8JsonWriter writer, UidPrefixSetting value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
                return;
            }

            if (value.AssemblyPrefixes is { } assemblyPrefixes)
            {
                writer.WriteStartObject();
                foreach (var (assemblyName, prefix) in assemblyPrefixes)
                {
                    writer.WritePropertyName(assemblyName);
                    writer.WriteStringValue(prefix);
                }
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteStringValue(value.Prefix);
            }
        }
    }
}
