using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SharpClaw.API.Database;

public static partial class FragmentIds
{
    private const string Prefix = "frag_";
    private const int RawLength = 16;
    private const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";

    [GeneratedRegex("^frag_[a-z0-9]{16}$", RegexOptions.CultureInvariant)]
    private static partial Regex FragmentIdRegex();

    public static bool IsValid(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && FragmentIdRegex().IsMatch(value);
    }

    public static string NewId()
    {
        Span<char> suffix = stackalloc char[RawLength];
        Span<byte> bytes = stackalloc byte[RawLength];
        RandomNumberGenerator.Fill(bytes);

        for (var i = 0; i < RawLength; i++)
            suffix[i] = Alphabet[bytes[i] % Alphabet.Length];

        return Prefix + suffix.ToString();
    }
}
