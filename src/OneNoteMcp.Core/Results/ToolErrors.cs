using System.Globalization;

namespace OneNoteMcp.Core.Results;

/// <summary>
/// The per-code retryable and userAction table (AD-9). "Retryable" means a retry can succeed, possibly after a
/// user action. <see cref="ErrorCode.ComUnavailable"/> and <see cref="ErrorCode.Timeout"/> are retryable for
/// <see cref="OperationClass.Read"/> only. An unknown outcome always forces <c>retryable: false</c>.
/// </summary>
public static class ToolErrors
{
    public const string OutcomeUnknownUserAction = "Re-read, then retry if the change is absent.";

    /// <summary>
    /// Appended to every "unlock it in OneNote" text: with OneNote's add-in access option off, an unlocked
    /// password-protected section still looks locked to COM (Phill, 2026-11-08).
    /// </summary>
    public const string AddInAccessOptionNote =
        "If it is already unlocked there, OneNote's option \"Enable add-in programs to access password protected sections when they are unlocked\" "
        + "(File > Options > Advanced > Passwords) is off: until it is on, add-ins, this server included, see the section as locked.";

    /// <summary>How the user grants a read of a password-protected section (AD-21). The model can't grant it.</summary>
    public static string ReadNotGrantedUserAction { get; } =
        "This section is password-protected, and reading its content needs the user's permission. Ask the user whether to grant it: "
        + "they can run " + Access.GrantCli.Run("--read-section \"<section name>\"") + ", or accept the dialog that appears when you read a page "
        + "of the section in a client that supports it (Claude Code does). Then retry. Don't try to get the content another way, and never run the grant command yourself.";

    public static ToolError From(OneNoteException exception, OperationClass operationClass)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var outcomeUnknown = exception.OutcomeUnknown;
        return new ToolError(
            Code: exception.Code,
            Retryable: !outcomeUnknown && IsRetryable(exception.Code, operationClass),
            UserAction: outcomeUnknown ? OutcomeUnknownUserAction : exception.UserAction ?? UserAction(exception.Code),
            Message: exception.Message,
            HResult: exception.ComHResult is { } hr ? FormatHResult(hr) : null,
            OutcomeUnknown: outcomeUnknown ? true : null);
    }

    public static string FormatHResult(int hresult) =>
        "0x" + hresult.ToString("X8", CultureInfo.InvariantCulture);

    public static bool IsRetryable(ErrorCode code, OperationClass operationClass) => code switch
    {
        ErrorCode.OneNoteNotRunning
            or ErrorCode.ComBusy
            or ErrorCode.ModalDialogOpen
            or ErrorCode.SectionLocked
            or ErrorCode.WriteNotGranted
            or ErrorCode.PermanentDeleteNotGranted
            or ErrorCode.ReadNotGranted
            or ErrorCode.SettingsInvalid => true,
        ErrorCode.ComUnavailable or ErrorCode.Timeout => operationClass == OperationClass.Read,
        _ => false,
    };

    public static string UserAction(ErrorCode code) => code switch
    {
        ErrorCode.OneNoteNotRunning => "Start OneNote desktop, then retry.",
        ErrorCode.IntegrityMismatch => "Run the server and OneNote at the same elevation (both normal or both as administrator), then restart the server.",
        ErrorCode.ComUnavailable => "Check that OneNote is running and responding, then retry.",
        ErrorCode.ComBusy => "Wait for OneNote to finish what it is doing, then retry.",
        ErrorCode.Timeout => "Check that OneNote is responding, then retry.",
        ErrorCode.ModalDialogOpen => "Close the open dialog in OneNote, then retry.",
        ErrorCode.SectionLocked => "Unlock the section in OneNote, then retry. " + AddInAccessOptionNote,
        ErrorCode.StaleWrite => "Re-read the object, then make the change against the current version.",
        ErrorCode.ObjectNotFound => "Re-read the hierarchy or page and use a current ID. If the notebook itself is missing, open it in OneNote.",
        ErrorCode.UnknownObjectId => "Re-read the page and use an objectID it contains.",
        ErrorCode.InvalidXml => "Fix the XML so it matches the OneNote 2013 schema, then retry.",
        ErrorCode.InvalidQuery => "Change the search query, then retry.",
        ErrorCode.FileExists => "Choose a path that does not exist yet.",
        ErrorCode.WriteNotGranted => "Grant write access to the notebook with the OneNoteMcp grant command, then retry.",
        ErrorCode.PermanentDeleteNotGranted => "Grant permanent-delete access to the notebook with the OneNoteMcp grant command, then retry.",
        ErrorCode.ReadNotGranted => ReadNotGrantedUserAction,
        ErrorCode.ResultExpired => "Repeat the original call to get a new resultId.",
        ErrorCode.ExportPathNotAllowed => "Choose a path under one of the configured export roots.",
        ErrorCode.SettingsInvalid => "Fix settings.json, then retry.",
        ErrorCode.JournalFailed => "Check that the journal folder exists, is writable and has free space.",
        ErrorCode.BackupFailed => "Nothing was deleted. Check that the backup location is writable and has free space.",
        ErrorCode.NotSupported => "This operation is not available in this release.",
        _ => "Report this error, with its HRESULT if it has one.",
    };
}
