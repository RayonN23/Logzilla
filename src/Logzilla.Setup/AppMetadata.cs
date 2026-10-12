namespace Logzilla.Setup;

/// <summary>Names and identifiers shared by the installer's pieces.</summary>
public static class AppMetadata
{
    public const string ProductName = "Logzilla Log Viewer";
    public const string ExecutableName = "Logzilla.exe";
    public const string SetupCopyName = "Logzilla-Setup.exe";
    public const string Publisher = "Rayon Narain (Independent)";
    public const string Version = "3.1.1";
    public const string Description = "A log analyzer for very large files";

    /// <summary>Shown in Add/Remove Programs so a user knows who to contact.</summary>
    public const string Contact = "Rayon23@hotmail.com";

    /// <summary>
    /// Folder under the user's profile. Deliberately not ProductName: the install
    /// path and settings directory must stay put when the display name changes, or
    /// an upgrade would strand the previous install and its settings.
    /// </summary>
    public const string FolderName = "Logzilla";

    /// <summary>
    /// Shortcut name used before the product was renamed. Uninstall removes it too,
    /// otherwise renaming leaves a dead shortcut behind on upgraded machines.
    /// </summary>
    public const string LegacyShortcutName = "Logzilla";
}
