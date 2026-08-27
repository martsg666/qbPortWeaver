namespace qbPortWeaver;

/// <summary>User-facing text formatting shared across log entries, diagnostics and the UI, so every count reads the same way.</summary>
public static class TextFormat
{
    /// <summary>
    /// Returns "<paramref name="count"/> <paramref name="noun"/>", adding a plural "s" unless the
    /// count is exactly 1 (e.g. <c>2 warnings</c>, <c>1 error</c>).
    /// </summary>
    /// <remarks>Shared so every user-facing count reads the same way. Use
    /// <see cref="PluralizeNoun"/> when the sentence places the number away from the noun.</remarks>
    public static string Pluralize(int count, string noun) => $"{count} {PluralizeNoun(count, noun)}";

    /// <summary>
    /// Returns <paramref name="noun"/> alone, pluralised for <paramref name="count"/> - for
    /// sentences that state the number somewhere other than immediately before the noun
    /// (e.g. "recovery triggers after 3 consecutive closed checks").
    /// </summary>
    /// <remarks>Only regular "add an s" plurals are needed here; nothing in the app's messages
    /// pluralises irregularly, so keeping it this simple is deliberate rather than an oversight.</remarks>
    public static string PluralizeNoun(int count, string noun) => count == 1 ? noun : noun + "s";
}
