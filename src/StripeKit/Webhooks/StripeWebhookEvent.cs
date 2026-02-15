using System;
using System.Text.Json;

namespace StripeKit;

public sealed class StripeWebhookEvent
{
    private StripeWebhookEvent(string id, string type, string? objectId, string? objectType)
    {
        Id = id;
        Type = type;
        ObjectId = objectId;
        ObjectType = objectType;
    }

    public string Id { get; }
    public string Type { get; }
    public string? ObjectId { get; }
    public string? ObjectType { get; }

    // TODO: Expand fields as handlers evolve (keep this minimal for core parsing).
    public static StripeWebhookEvent FromJsonElement(JsonElement root)
    {
        string id = GetRequiredString(root, "id");
        string type = GetRequiredString(root, "type");
        string? objectId = null;
        string? objectType = null;

        if (root.TryGetProperty("data", out JsonElement dataElement) &&
            dataElement.TryGetProperty("object", out JsonElement objectElement))
        {
            if (objectElement.TryGetProperty("id", out JsonElement idElement) &&
                idElement.ValueKind == JsonValueKind.String)
            {
                objectId = idElement.GetString();
            }

            if (objectElement.TryGetProperty("object", out JsonElement typeElement) &&
                typeElement.ValueKind == JsonValueKind.String)
            {
                objectType = typeElement.GetString();
            }
        }

        return new StripeWebhookEvent(id, type, objectId, objectType);
    }

    private static string GetRequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement element) ||
            element.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Missing required webhook field: " + propertyName);
        }

        string? value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Missing required webhook field: " + propertyName);
        }

        return value;
    }
}
