using System.Text.Json;

namespace qbPortWeaver;

/// <summary>
/// Reads string, boolean and integer values out of JSON responses without trusting their type.
/// </summary>
/// <remarks><see cref="JsonElement.GetString"/> throws <see cref="InvalidOperationException"/>
/// when the value is neither a string nor null, so a client or plugin that reports a field in an
/// unexpected shape would abort the whole read rather than that one field. Every JSON response
/// this app parses comes from a separate program with its own release cycle, so a field changing
/// shape between versions is a normal event, not a defect. These helpers turn that case into an
/// absent value, which every caller already handles.
/// <para><b>The integer readers are the same trap wearing a Try name.</b>
/// <see cref="JsonElement.TryGetInt32(out int)"/> reads as total and is not: it throws
/// <see cref="InvalidOperationException"/> for every kind other than Number, JSON null included,
/// and only returns false for a number that will not fit. Call sites written against the name
/// therefore had unreachable "not an integer" branches whose diagnostics could never be
/// written. Route integer reads through here rather than restating the kind test at each
/// site.</para></remarks>
internal static class JsonElementExtensions
{
    /// <summary>Reads <paramref name="propertyName"/> as a string, or <see langword="null"/> when
    /// it is absent, JSON null, or not a string.</summary>
    internal static string? GetStringOrNull(this JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var element) ? element.AsStringOrNull() : null;

    /// <summary>Reads this element as a string, or <see langword="null"/> when it is JSON null or
    /// not a string.</summary>
    /// <remarks>For elements already pulled out of a parent - typically because the caller needs
    /// the element for something else as well.</remarks>
    internal static string? AsStringOrNull(this JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    /// <summary>Reads <paramref name="propertyName"/> as a 32-bit integer, or <see langword="null"/>
    /// when it is absent, JSON null, not a number, or a number that does not fit.</summary>
    internal static int? GetInt32OrNull(this JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var element) ? element.AsInt32OrNull() : null;

    /// <summary>Reads this element as a 32-bit integer, or <see langword="null"/> when it is JSON
    /// null, not a number, or a number that does not fit.</summary>
    /// <remarks>For elements already pulled out of a parent - an array entry, or a property the
    /// caller also needs for something else. Pairs with <see cref="AsStringOrNull"/>.
    /// <para>The kind test is what makes this total: <see cref="JsonElement.TryGetInt32(out int)"/>
    /// throws for a non-Number element rather than returning false, so it is only safe once the kind
    /// is known. Past that point false means "a number too large for <see cref="int"/>", which is
    /// absent for these callers too.</para></remarks>
    internal static int? AsInt32OrNull(this JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int value) ? value : null;

    /// <summary>Reads <paramref name="propertyName"/> as a boolean, or <see langword="null"/> when it
    /// is absent, JSON null, or not interpretable as one.</summary>
    /// <remarks>Accepts the three shapes these clients actually use for a flag: a JSON boolean, the
    /// numbers 1 and 0, and the strings "true"/"false". Null carries a real meaning for the callers
    /// here - the Nicotine+ bridge returns null for a setting the running version does not expose -
    /// so "unknown" must stay distinguishable from "off" rather than collapsing to false.</remarks>
    internal static bool? GetBoolOrNull(this JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var element)) return null;
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => element.TryGetInt32(out int number) ? number != 0 : null,
            JsonValueKind.String => bool.TryParse(element.GetString(), out bool parsed) ? parsed : null,
            _ => null,
        };
    }
}
