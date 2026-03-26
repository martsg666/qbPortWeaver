namespace qbPortWeaver
{
    /// <summary>Determines how files are transferred from source folders to the library.</summary>
    public enum ImportMode
    {
        /// <summary>Creates a hardlink in the library pointing to the same data on disk. Falls back to <see cref="Copy"/> if the hardlink fails (e.g. cross-volume).</summary>
        Hardlink,

        /// <summary>Copies the file to the library, leaving the original in place for seeding.</summary>
        Copy,

        /// <summary>Moves the file to the library (current behavior). Breaks seeding.</summary>
        Move
    }
}
