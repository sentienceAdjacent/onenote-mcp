using System.Collections.Frozen;
using OneNoteMcp.Core;

namespace OneNoteMcp.Com;

/// <summary>
/// The HRESULT map (AD-9). It lives only in Com. Names are copied from <c>spikes/com-capabilities/OneNoteProbe.psm1</c>
/// (itself copied from learn.microsoft.com "Error codes (OneNote)"), never typed from memory; a unit test parses
/// the psm1 table and fails on any difference.
/// </summary>
internal static class HResultMap
{
    internal static readonly FrozenDictionary<int, string> Names = new Dictionary<int, string>
    {
        [unchecked((int)0x80042000)] = "hrMalformedXML",
        [unchecked((int)0x80042001)] = "hrInvalidXML",
        [unchecked((int)0x80042002)] = "hrCreatingSection",
        [unchecked((int)0x80042003)] = "hrOpeningSection",
        [unchecked((int)0x80042004)] = "hrSectionDoesNotExist",
        [unchecked((int)0x80042005)] = "hrPageDoesNotExist",
        [unchecked((int)0x80042006)] = "hrFileDoesNotExist",
        [unchecked((int)0x80042007)] = "hrInsertingImage",
        [unchecked((int)0x80042008)] = "hrInsertingInk",
        [unchecked((int)0x80042009)] = "hrInsertingHtml",
        [unchecked((int)0x8004200A)] = "hrNavigatingToPage",
        [unchecked((int)0x8004200B)] = "hrSectionReadOnly",
        [unchecked((int)0x8004200C)] = "hrPageReadOnly",
        [unchecked((int)0x8004200D)] = "hrInsertingOutlineText",
        [unchecked((int)0x8004200E)] = "hrPageObjectDoesNotExist",
        [unchecked((int)0x8004200F)] = "hrBinaryObjectDoesNotExist",
        [unchecked((int)0x80042010)] = "hrLastModifiedDateDidNotMatch",
        [unchecked((int)0x80042011)] = "hrGroupDoesNotExist",
        [unchecked((int)0x80042012)] = "hrPageDoesNotExistInGroup",
        [unchecked((int)0x80042013)] = "hrNoActiveSelection",
        [unchecked((int)0x80042014)] = "hrObjectDoesNotExist",
        [unchecked((int)0x80042015)] = "hrNotebookDoesNotExist",
        [unchecked((int)0x80042016)] = "hrInsertingFile",
        [unchecked((int)0x80042017)] = "hrInvalidName",
        [unchecked((int)0x80042018)] = "hrFolderDoesNotExist",
        [unchecked((int)0x80042019)] = "hrInvalidQuery",
        [unchecked((int)0x8004201A)] = "hrFileAlreadyExists",
        [unchecked((int)0x8004201B)] = "hrSectionEncryptedAndLocked",
        [unchecked((int)0x8004201C)] = "hrDisabledByPolicy",
        [unchecked((int)0x8004201D)] = "hrNotYetSynchronized",
        [unchecked((int)0x8004201E)] = "hrLegacySection",
        [unchecked((int)0x8004201F)] = "hrMergeFailed",
        [unchecked((int)0x80042020)] = "hrInvalidXMLSchema",
        [unchecked((int)0x80042022)] = "hrFutureContentLoss",
        [unchecked((int)0x80042023)] = "hrTimeOut",
        [unchecked((int)0x80042024)] = "hrRecordingInProgress",
        [unchecked((int)0x80042025)] = "hrUnknownLinkedNoteState",
        [unchecked((int)0x80042026)] = "hrNoShortNameForLinkedNote",
        [unchecked((int)0x80042027)] = "hrNoFriendlyNameForLinkedNote",
        [unchecked((int)0x80042028)] = "hrInvalidLinkedNoteUri",
        [unchecked((int)0x80042029)] = "hrInvalidLinkedNoteThumbnail",
        [unchecked((int)0x8004202A)] = "hrImportLNTThumbnailFailed",
        [unchecked((int)0x8004202B)] = "hrUnreadDisabledForNotebook",
        [unchecked((int)0x8004202C)] = "hrInvalidSelection",
        [unchecked((int)0x8004202D)] = "hrConvertFailed",
        [unchecked((int)0x8004202E)] = "hrRecycleBinEditFailed",
        [unchecked((int)0x8004202F)] = "hrIMConversationTypeInvalid",
        [unchecked((int)0x80042030)] = "hrAppInModalUI",
        [unchecked((int)0x80042031)] = "hrPublishFormatUnsupportedForLabels",

        // Generic COM / RPC codes seen around activation and lost connections.
        [unchecked((int)0x80004005)] = "E_FAIL",
        [unchecked((int)0x80040154)] = "REGDB_E_CLASSNOTREG",
        [unchecked((int)0x80080005)] = "CO_E_SERVER_EXEC_FAILURE",
        [unchecked((int)0x800706BA)] = "RPC_S_SERVER_UNAVAILABLE",
        [unchecked((int)0x800706BE)] = "RPC_S_CALL_FAILED",
        [unchecked((int)0x80010108)] = "RPC_E_DISCONNECTED",
        [unchecked((int)0x80010001)] = "RPC_E_CALL_REJECTED",
        [unchecked((int)0x8001010A)] = "RPC_E_SERVERCALL_RETRYLATER",
        [unchecked((int)0x80070005)] = "E_ACCESSDENIED",
        [unchecked((int)0x80070057)] = "E_INVALIDARG",
        [unchecked((int)0x80020005)] = "DISP_E_TYPEMISMATCH",
        [unchecked((int)0x8002000E)] = "DISP_E_BADPARAMCOUNT",
    }.ToFrozenDictionary();

    /// <summary>The documented name, or null for an HRESULT in neither table.</summary>
    internal static string? Name(int hresult) => Names.GetValueOrDefault(hresult);

    /// <summary>For the insert failures: the change may have partly applied.</summary>
    internal const string InsertingUserAction =
        "OneNote couldn't insert one element. Re-read the page to see what was applied, then send a corrected change for that element.";

    /// <summary>
    /// The triage of every named HRESULT (AD-9, story 2.7): its code, plus its own <c>userAction</c> and
    /// <c>retryable</c> flag where the code's defaults don't fit (null fields keep the defaults). The codes AD-9 keeps
    /// deliberately <see cref="ErrorCode.Internal"/> get a row too, with a specific action where one helps. Only an
    /// HRESULT with no name falls to <see cref="ErrorCode.Internal"/> and the defaults.
    /// </summary>
    internal static readonly FrozenDictionary<int, Triage> Triaged = new Dictionary<int, Triage>
    {
        // OneNote codes, 0x80042000–0x80042031 (the docs skip 0x80042021).
        [unchecked((int)0x80042000)] = new(ErrorCode.InvalidXml),
        [unchecked((int)0x80042001)] = new(ErrorCode.InvalidXml),
        [unchecked((int)0x80042002)] = new(ErrorCode.Internal, "OneNote couldn't create the section. Check that its folder is writable and its name is valid."),
        [unchecked((int)0x80042003)] = new(ErrorCode.Internal, "OneNote couldn't open the section. Ask the user to open it in OneNote to see why (still syncing, or its file is missing), then retry.", Retryable: true),
        [unchecked((int)0x80042004)] = new(ErrorCode.ObjectNotFound),
        [unchecked((int)0x80042005)] = new(ErrorCode.ObjectNotFound),
        [unchecked((int)0x80042006)] = new(ErrorCode.Internal), // A path, not an object (AD-9).
        [unchecked((int)0x80042007)] = new(ErrorCode.Internal, InsertingUserAction),
        [unchecked((int)0x80042008)] = new(ErrorCode.Internal, InsertingUserAction),
        [unchecked((int)0x80042009)] = new(ErrorCode.Internal, InsertingUserAction),
        [unchecked((int)0x8004200A)] = new(ErrorCode.Internal, "OneNote couldn't go to the page. Re-read the hierarchy to check the ID."),
        [unchecked((int)0x8004200B)] = new(ErrorCode.Internal, "OneNote reports the section as read-only (for example a view-only share). Ask the user for edit access, then retry.", Retryable: true),
        [unchecked((int)0x8004200C)] = new(ErrorCode.Internal, "OneNote reports the page as read-only. Ask the user to make it editable in OneNote, then retry.", Retryable: true),
        [unchecked((int)0x8004200D)] = new(ErrorCode.Internal, InsertingUserAction),
        [unchecked((int)0x8004200E)] = new(ErrorCode.ObjectNotFound),
        [unchecked((int)0x8004200F)] = new(ErrorCode.Internal), // Reads degrade it to the BinaryUnavailable warning (AD-9).
        [unchecked((int)0x80042010)] = new(ErrorCode.StaleWrite),
        [unchecked((int)0x80042011)] = new(ErrorCode.ObjectNotFound),
        [unchecked((int)0x80042012)] = new(ErrorCode.ObjectNotFound),
        [unchecked((int)0x80042013)] = new(ErrorCode.Internal), // Selection: no v1 call reaches it.
        [unchecked((int)0x80042014)] = new(ErrorCode.ObjectNotFound),
        [unchecked((int)0x80042015)] = new(ErrorCode.ObjectNotFound),
        [unchecked((int)0x80042016)] = new(ErrorCode.Internal, InsertingUserAction),
        [unchecked((int)0x80042017)] = new(ErrorCode.Internal, "OneNote refused the name. Choose a different name."),
        [unchecked((int)0x80042018)] = new(ErrorCode.Internal), // A path, not an object (AD-9).
        [unchecked((int)0x80042019)] = new(ErrorCode.InvalidQuery),
        [unchecked((int)0x8004201A)] = new(ErrorCode.FileExists),
        [unchecked((int)0x8004201B)] = new(ErrorCode.SectionLocked),
        [unchecked((int)0x8004201C)] = new(ErrorCode.Internal, "An administrator policy turns this off in OneNote, so retrying won't help. The user's administrator can change the policy."),
        [unchecked((int)0x8004201D)] = new(ErrorCode.ComBusy),
        [unchecked((int)0x8004201E)] = new(ErrorCode.Internal, "The section is in the OneNote 2007 format or older. Ask the user to upgrade the notebook in OneNote, then retry.", Retryable: true),
        [unchecked((int)0x8004201F)] = new(ErrorCode.Internal, "OneNote couldn't merge the change. Re-read the object to see its current state."),
        [unchecked((int)0x80042020)] = new(ErrorCode.InvalidXml),
        [unchecked((int)0x80042022)] = new(ErrorCode.Internal, "The change would lose content a newer OneNote wrote. Don't repeat it; ask the user to edit that content in OneNote."),
        [unchecked((int)0x80042023)] = new(ErrorCode.Timeout),
        [unchecked((int)0x80042024)] = new(ErrorCode.Internal, "A recording is in progress in OneNote. Ask the user to stop it, then retry.", Retryable: true),

        // Linked notes, unread marks, selection and IM: no v1 call reaches these.
        [unchecked((int)0x80042025)] = new(ErrorCode.Internal),
        [unchecked((int)0x80042026)] = new(ErrorCode.Internal),
        [unchecked((int)0x80042027)] = new(ErrorCode.Internal),
        [unchecked((int)0x80042028)] = new(ErrorCode.Internal),
        [unchecked((int)0x80042029)] = new(ErrorCode.Internal),
        [unchecked((int)0x8004202A)] = new(ErrorCode.Internal),
        [unchecked((int)0x8004202B)] = new(ErrorCode.Internal),
        [unchecked((int)0x8004202C)] = new(ErrorCode.Internal),
        [unchecked((int)0x8004202D)] = new(ErrorCode.Internal, "OneNote couldn't convert the content. Re-read the object to see its current state."),
        [unchecked((int)0x8004202E)] = new(ErrorCode.Internal, "The target is in the notebook's recycle bin. Use a current ID, or ask the user to restore it in OneNote."),
        [unchecked((int)0x8004202F)] = new(ErrorCode.Internal),
        [unchecked((int)0x80042030)] = new(ErrorCode.ModalDialogOpen),
        [unchecked((int)0x80042031)] = new(ErrorCode.Internal, "OneNote can't publish labels in this format. Choose another format."),

        // Generic COM / RPC codes.
        [unchecked((int)0x80004005)] = new(ErrorCode.Internal),
        [unchecked((int)0x80040154)] = new(ErrorCode.Internal),

        // CO_E_SERVER_EXEC_FAILURE: the gateway checks integrity levels before it calls COM (AD-3), so this can still come
        // back at activation only after the check passed; cause not observed. ComUnavailable, not IntegrityMismatch.
        [unchecked((int)0x80080005)] = new(ErrorCode.ComUnavailable, ServerExecFailureUserAction),
        [unchecked((int)0x800706BA)] = new(ErrorCode.ComUnavailable),
        [unchecked((int)0x800706BE)] = new(ErrorCode.Internal), // Ambiguous: bad XML, or OneNote crashing mid-call (AD-9).
        [unchecked((int)0x80010108)] = new(ErrorCode.ComUnavailable),
        [unchecked((int)0x80010001)] = new(ErrorCode.ComBusy),
        [unchecked((int)0x8001010A)] = new(ErrorCode.ComBusy),
        [unchecked((int)0x80070005)] = new(ErrorCode.Internal),
        [unchecked((int)0x80070057)] = new(ErrorCode.Internal),
        [unchecked((int)0x80020005)] = new(ErrorCode.Internal),
        [unchecked((int)0x8002000E)] = new(ErrorCode.Internal),
    }.ToFrozenDictionary();

    /// <summary>The code <see cref="Triaged"/> gives; <see cref="ErrorCode.Internal"/> for an HRESULT with no row.</summary>
    internal static ErrorCode ToErrorCode(int hresult) => Triaged.TryGetValue(hresult, out var triage) ? triage.Code : ErrorCode.Internal;

    /// <summary>The HRESULT's own <c>userAction</c>, or null when its code's default applies.</summary>
    internal static string? UserAction(int hresult) => Triaged.TryGetValue(hresult, out var triage) ? triage.UserAction : null;

    /// <summary>The HRESULT's own <c>retryable</c> flag, or null when its code's default applies.</summary>
    internal static bool? Retryable(int hresult) => Triaged.TryGetValue(hresult, out var triage) ? triage.Retryable : null;

    /// <summary><c>"{what} failed: {name} (0x80042014)."</c>, or hex only when the HRESULT has no known name.</summary>
    internal static string FailureMessage(string what, int hresult)
    {
        var hex = Core.Results.ToolErrors.FormatHResult(hresult);
        return Name(hresult) is { } name ? $"{what} failed: {name} ({hex})." : $"{what} failed: {hex}.";
    }

    /// <summary>The userAction for <c>0x80080005</c>, which can still come back at activation after the integrity check passed; cause not observed.</summary>
    internal const string ServerExecFailureUserAction = "Restart OneNote, then retry.";

    internal static OneNoteException ToException(string what, Exception ex) =>
        new(
            ToErrorCode(ex.HResult),
            FailureMessage(what, ex.HResult),
            ex.HResult,
            ex,
            userAction: UserAction(ex.HResult),
            retryable: Retryable(ex.HResult));
}

/// <summary>One row of <see cref="HResultMap.Triaged"/>: the code, and the action and retry flag that override its defaults when set.</summary>
internal readonly record struct Triage(ErrorCode Code, string? UserAction = null, bool? Retryable = null);
