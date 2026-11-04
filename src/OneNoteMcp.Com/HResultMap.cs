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

    /// <summary>
    /// Maps by family (AD-9). The codes left deliberately <see cref="ErrorCode.Internal"/>, and why, are listed in AD-9;
    /// everything else unmapped falls to <see cref="ErrorCode.Internal"/> by the default arm.
    /// </summary>
    internal static ErrorCode ToErrorCode(int hresult) => unchecked((uint)hresult) switch
    {
        0x80080005 => ErrorCode.IntegrityMismatch,
        0x800706BA or 0x80010108 => ErrorCode.ComUnavailable,
        0x80010001 or 0x8001010A or 0x8004201D => ErrorCode.ComBusy,
        0x80042030 => ErrorCode.ModalDialogOpen,
        0x8004201B => ErrorCode.SectionLocked,
        0x80042010 => ErrorCode.StaleWrite,
        0x80042023 => ErrorCode.Timeout,
        0x80042004 or 0x80042005 or 0x8004200E or 0x80042011 or 0x80042012 or 0x80042014 or 0x80042015 => ErrorCode.ObjectNotFound,
        0x80042000 or 0x80042001 or 0x80042020 => ErrorCode.InvalidXml,
        0x80042019 => ErrorCode.InvalidQuery,
        0x8004201A => ErrorCode.FileExists,
        _ => ErrorCode.Internal,
    };

    /// <summary><c>"{what} failed: {name} (0x80042014)."</c>, or hex only when the HRESULT has no known name.</summary>
    internal static string FailureMessage(string what, int hresult)
    {
        var hex = Core.Results.ToolErrors.FormatHResult(hresult);
        return Name(hresult) is { } name ? $"{what} failed: {name} ({hex})." : $"{what} failed: {hex}.";
    }

    internal static OneNoteException ToException(string what, Exception ex) =>
        new(ToErrorCode(ex.HResult), FailureMessage(what, ex.HResult), ex.HResult, ex);
}
