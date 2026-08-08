// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Docfx;

internal partial class UidPrefixSettingConverter
{
    internal class NewtonsoftJsonConverter : Newtonsoft.Json.JsonConverter
    {
        /// <inheritdoc/>
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(UidPrefixSetting);
        }

        /// <inheritdoc/>
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            switch (reader.TokenType)
            {
                case JsonToken.Null:
                    return null;

                case JsonToken.String:
                    return new UidPrefixSetting { Prefix = (string)reader.Value };

                case JsonToken.StartObject:
                    var assemblyPrefixes = new Dictionary<string, string>();
                    foreach (var property in JObject.Load(reader).Properties())
                    {
                        assemblyPrefixes[property.Name] = property.Value.Type == JTokenType.Null ? null : (string)property.Value;
                    }
                    return new UidPrefixSetting { AssemblyPrefixes = assemblyPrefixes };

                default:
                    throw new JsonSerializationException($"TokenType({reader.TokenType}) is not supported for 'uidPrefix'.");
            }
        }

        /// <inheritdoc/>
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (value is not UidPrefixSetting model)
            {
                writer.WriteNull();
                return;
            }

            if (model.AssemblyPrefixes is { } assemblyPrefixes)
            {
                writer.WriteStartObject();
                foreach (var (assemblyName, prefix) in assemblyPrefixes)
                {
                    writer.WritePropertyName(assemblyName);
                    writer.WriteValue(prefix);
                }
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteValue(model.Prefix);
            }
        }
    }
}
