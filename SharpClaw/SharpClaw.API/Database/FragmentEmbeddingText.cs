namespace SharpClaw.API.Database;

public static class FragmentEmbeddingText
{
    public static string BuildInput(string name, string? type, string content)
    {
        var normalizedContent = string.IsNullOrWhiteSpace(content) ? string.Empty : content.Trim();
        if (normalizedContent.Length > 8_000)
            normalizedContent = normalizedContent[..8_000];

        var normalizedType = string.IsNullOrWhiteSpace(type) ? "knowledge" : type.Trim();
        return $"name: {name}\ntype: {normalizedType}\ncontent:\n{normalizedContent}";
    }
}
