namespace OneNoteMcp.Core.AppState;

/// <summary>
/// Checks and rewraps the <c>AppState</c> tools share (round-3 refactor sweep): the empty-<c>hierarchyId</c> refusal of
/// <c>sync_hierarchy</c> and <c>navigate_to</c>, and the <c>0x80042014</c> userAction of the two navigate tools (AD-9).
/// </summary>
internal static class AppStateChecks
{
    /// <summary>Step 1: an empty <paramref name="hierarchyId"/> is <see cref="ErrorCode.InvalidArgument"/>, before COM.</summary>
    public static void RequireHierarchyId(string hierarchyId)
    {
        ArgumentNullException.ThrowIfNull(hierarchyId);
        if (hierarchyId.Length == 0)
        {
            throw new OneNoteException(ErrorCode.InvalidArgument, "hierarchyId is empty: pass the ID of a notebook, section group, section or page.");
        }
    }

    /// <summary>
    /// <paramref name="ex"/> with <paramref name="userAction"/> when it is <see cref="ErrorCode.ObjectNotFound"/> from
    /// <c>0x80042014</c>: its code, message, HRESULT and <c>outcomeUnknown</c> kept, <paramref name="ex"/> as the inner
    /// exception, and <c>retryable</c> left unset. Null for any other failure, which the caller rethrows as it is.
    /// </summary>
    public static OneNoteException? WithUserAction(OneNoteException ex, string userAction)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.Code == ErrorCode.ObjectNotFound && ex.ComHResult == OneNoteHResults.ObjectDoesNotExist
            ? new OneNoteException(ex.Code, ex.Message, ex.ComHResult, ex, ex.OutcomeUnknown, userAction)
            : null;
    }
}
