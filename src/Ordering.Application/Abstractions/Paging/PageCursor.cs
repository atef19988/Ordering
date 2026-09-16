using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ordering.Application.Abstractions.Paging;

/// <summary>
/// Where the next page starts: the sort field and direction, the last row's sort value and its
/// tie-breaker key, and a fingerprint of the filter. On the wire it is base64url of a small JSON
/// document (<c>{"v":1,"f":"name","d":"asc","k":"Widget","c":"SKU-017","h":"3f2a…"}</c>); the
/// server checks that <c>v</c>, <c>f</c>, <c>d</c> and <c>h</c> match the current request before
/// trusting <c>k</c> and <c>c</c>. It is not signed: a forged cursor can only pick a different
/// starting row, which the caller could do anyway.
/// </summary>
/// <param name="Field">The whitelisted sort field the page was ordered by.</param>
/// <param name="Direction"><see cref="Ascending"/> or <see cref="Descending"/>.</param>
/// <param name="Key">The last row's sort value, as a string (decimals in invariant format).</param>
/// <param name="TieBreaker">The last row's unique key, which orders rows that share <paramref name="Key"/>.</param>
/// <param name="FilterHash">The <see cref="HashFilter"/> of every filter the page was computed under.</param>
public sealed record PageCursor(string Field, string Direction, string Key, string TieBreaker, string FilterHash)
{
    public const string Ascending = "asc";
    public const string Descending = "desc";

    private const int Version = 1;
    private const int FilterHashLength = 8;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string DirectionOf(bool descending) => descending ? Descending : Ascending;

    /// <summary>
    /// First eight hex characters of SHA-256 over the filter parts joined by <c>|</c>. Enough to
    /// reject a cursor replayed against a different query; it is a check, not a secret.
    /// </summary>
    public static string HashFilter(params string?[] parts)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts)));
        return Convert.ToHexStringLower(hash)[..FilterHashLength];
    }

    public bool Matches(string field, string direction, string filterHash) =>
        string.Equals(Field, field, StringComparison.Ordinal)
        && string.Equals(Direction, direction, StringComparison.Ordinal)
        && string.Equals(FilterHash, filterHash, StringComparison.Ordinal);

    public string Encode()
    {
        var document = new Document(Version, Field, Direction, Key, TieBreaker, FilterHash);
        return Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(document, Json));
    }

    /// <summary>Bad base64, bad JSON, a missing member or another version all fail; nothing throws.</summary>
    public static bool TryDecode(string encoded, [NotNullWhen(true)] out PageCursor? cursor)
    {
        cursor = null;

        if (string.IsNullOrWhiteSpace(encoded) || !Base64Url.IsValid(encoded))
        {
            return false;
        }

        Document? document;

        try
        {
            document = JsonSerializer.Deserialize<Document>(Base64Url.DecodeFromChars(encoded), Json);
        }
        catch (JsonException)
        {
            return false;
        }

        if (document is not { Version: Version, Field: not null, Direction: not null, Key: not null, TieBreaker: not null, FilterHash: not null })
        {
            return false;
        }

        cursor = new PageCursor(document.Field, document.Direction, document.Key, document.TieBreaker, document.FilterHash);
        return true;
    }

    /// <summary>The wire shape; member names are deliberately one letter to keep the cursor short.</summary>
    private sealed record Document(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("f")] string? Field,
        [property: JsonPropertyName("d")] string? Direction,
        [property: JsonPropertyName("k")] string? Key,
        [property: JsonPropertyName("c")] string? TieBreaker,
        [property: JsonPropertyName("h")] string? FilterHash);
}
