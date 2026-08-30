namespace qbPortWeaver;

/// <summary>Determines how files are transferred from source folders to the library.</summary>
/// <remarks>
/// <b>These member names are persisted verbatim.</b> The Media Manager stores the selected mode in the
/// registry as a string, and <c>MediaManagerService.ParseImportMode</c> reads it back with
/// <c>Enum.TryParse&lt;ImportMode&gt;(value, ignoreCase: true)</c>, falling back to
/// <see cref="Hardlink"/> when it does not match.
/// <para>So renaming a member here silently changes what every installed copy has saved: the old
/// string stops parsing, and those users are moved to <see cref="Hardlink"/> without a compile error,
/// a runtime error, or a log entry. Someone importing by <see cref="Move"/> would quietly start
/// hardlinking instead. Add members freely; do not rename or reorder-for-value the existing three.
/// The combo box in <c>MediaManagerForm.Designer.cs</c> lists the same strings literally, so it has to
/// be updated in step.</para>
/// </remarks>
public enum ImportMode
{
    /// <summary>Creates a hardlink in the library pointing to the same data on disk. Falls back to <see cref="Copy"/> if the hardlink fails (e.g. cross-volume).</summary>
    Hardlink,

    /// <summary>Copies the file to the library, leaving the original in place.</summary>
    Copy,

    /// <summary>Moves the file to the library, removing the original.</summary>
    Move
}
