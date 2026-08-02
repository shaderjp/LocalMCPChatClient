using System.Text.Json;

namespace LocalMCPChatClient.Infrastructure;

public static class ToolArgumentValidator
{
    public static string? Validate(string argumentsJson, JsonElement schema)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(argumentsJson); }
        catch (JsonException exception) { return $"引数JSONが不正です: {exception.Message}"; }
        using (document)
        {
            var arguments = document.RootElement;
            if (arguments.ValueKind != JsonValueKind.Object) return "ツール引数はJSONオブジェクトである必要があります。";
            if (schema.ValueKind != JsonValueKind.Object) return null;

            if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in required.EnumerateArray())
                {
                    var name = item.GetString();
                    if (name is not null && !arguments.TryGetProperty(name, out _)) return $"必須引数 '{name}' がありません。";
                }
            }
            if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object) return null;
            foreach (var property in arguments.EnumerateObject())
            {
                if (!properties.TryGetProperty(property.Name, out var propertySchema)) continue;
                if (!propertySchema.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String) continue;
                var expected = typeElement.GetString();
                if (!MatchesType(property.Value, expected)) return $"引数 '{property.Name}' は {expected} 型である必要があります。";
            }
            return null;
        }
    }

    private static bool MatchesType(JsonElement value, string? expected) => expected switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true
    };
}
