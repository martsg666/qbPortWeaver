using System.Text.Json;

namespace qbPortWeaver;

/// <summary>
/// Reads string values out of JSON responses without trusting their type.
/// </summary>
/// <remarks><see cref="JsonElement.GetString"/> throws <see cref="InvalidOperationException"/>
/// when the value is neither a string nor null, so a client or plugin that reports a field in an
/// unexpected shape would abort the whole read rather than that one field. Every JSON response
/// this app parses comes from a separate program with its own release cycle, so a field changing
/// shape between versions is a normal event, not a defect. These helpers turn that case into an
/// absent value, which every caller already handles.</remarks>
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
}
