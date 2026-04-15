using System.Text.Json;

namespace SharpClaw.API.Helpers;

public static class Serialization
{
    public static string? SerializeToolPayload(object? payload)
    {
        if (payload is null)
            return null;

        if (payload is string text)
            return text;

        try
        {
            return JsonSerializer.Serialize(payload);
        }
        catch
        {
            return payload.ToString();
        }
    }
}