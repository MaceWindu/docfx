// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Newtonsoft.Json.Linq;

namespace Docfx;

/// <summary>
/// Reads a list of strings written either as an array or as a single string.
/// </summary>
internal static class StringOrListJsonConverter
{
    internal sealed class NewtonsoftJson : Newtonsoft.Json.JsonConverter
    {
        public override bool CanConvert(Type objectType) => objectType == typeof(List<string>);

        public override object ReadJson(Newtonsoft.Json.JsonReader reader, Type objectType, object existingValue, Newtonsoft.Json.JsonSerializer serializer)
        {
            return reader.TokenType switch
            {
                Newtonsoft.Json.JsonToken.Null => null,
                Newtonsoft.Json.JsonToken.String => new List<string> { (string)reader.Value },
                _ => JArray.Load(reader).ToObject<List<string>>(),
            };
        }

        public override void WriteJson(Newtonsoft.Json.JsonWriter writer, object value, Newtonsoft.Json.JsonSerializer serializer)
        {
            new JArray(((List<string>)value).ToArray<object>()).WriteTo(writer);
        }
    }

    internal sealed class SystemTextJson : System.Text.Json.Serialization.JsonConverter<List<string>>
    {
        public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                JsonTokenType.String => [reader.GetString()],
                JsonTokenType.StartArray => [.. JsonSerializer.Deserialize<string[]>(ref reader, options)],
                _ => throw new JsonException($"TokenType({reader.TokenType}) is not supported."),
            };
        }

        public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var item in value)
            {
                writer.WriteStringValue(item);
            }
            writer.WriteEndArray();
        }
    }
}
