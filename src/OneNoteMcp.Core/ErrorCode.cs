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

    /// <summary>
    /// AD-9: an argument the server refuses. An argument that doesn't fit the tool's input schema (an undeclared name, a
    /// missing required argument, a value of the wrong type or outside its enum), checked before the SDK binds the call
    /// (stories 2.5, 2.14); the AD-7 refusals of a write's arguments; and OneNote's <c>0x80042017</c> hrInvalidName
    /// (AD-9 Remapped).
    /// </summary>
    InvalidArgument,
    InvalidQuery,
    FileExists,
    WriteNotGranted,
    PermanentDeleteNotGranted,

    /// <summary>
    /// AD-21: a target-scoped read inside a password-protected section with no read grant; in such a section, the writes
    /// that need one (<c>delete_page_content</c>, a page write whose basis carries a hash, whose XML carries an
    /// <c>objectID</c> or that names <c>removeObjectIds</c>, a page permanent delete whose basis carries a hash; AD-17);
    /// or a <c>read_result</c> part whose grant has lapsed (AD-20).
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
