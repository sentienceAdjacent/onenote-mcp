namespace OneNoteMcp.Core;

/// <summary>
/// The closed AD-9 error set. A code is added only by amending AD-9.
/// <c>BinaryUnavailable</c> is deliberately absent: it is a warning, never an error.
/// </summary>
public enum ErrorCode
{
    OneNoteNotRunning,
    IntegrityMismatch,
    ComUnavailable,
    ComBusy,
    Timeout,
    ModalDialogOpen,
    SectionLocked,
    StaleWrite,
    ObjectNotFound,
    UnknownObjectId,
    InvalidXml,
    InvalidQuery,
    FileExists,
    WriteNotGranted,
    PermanentDeleteNotGranted,
    ExportPathNotAllowed,
    SettingsInvalid,
    JournalFailed,
    BackupFailed,
    NotSupported,
    Internal,
}
