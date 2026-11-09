using System.Text.Json.Serialization;

namespace OneNoteMcp.Core;

/// <summary>
/// The closed AD-9 error set. A code is added only by amending AD-9.
/// <c>BinaryUnavailable</c> is deliberately absent: it is a warning, never an error.
/// It serialises as its name (<c>"ObjectNotFound"</c>), which is the wire contract (AD-14).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ErrorCode>))]
public enum ErrorCode
{
    OneNoteNotRunning,
    IntegrityMismatch,
    ComUnavailable,
    ComBusy,
    Timeout,

    /// <summary>The client cancelled the call (its own cancellation token), before it finished.</summary>
    Cancelled,
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

    /// <summary>
    /// AD-21: a target-scoped read inside a password-protected section with no read grant, or a <c>read_result</c> part
    /// whose grant has lapsed (AD-20).
    /// </summary>
    ReadNotGranted,

    /// <summary>AD-20: <c>read_result</c> with a <c>resultId</c> that expired, was evicted, or was never issued.</summary>
    ResultExpired,
    ExportPathNotAllowed,
    SettingsInvalid,
    JournalFailed,
    BackupFailed,
    NotSupported,
    Internal,
}
