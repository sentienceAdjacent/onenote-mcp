namespace OneNoteMcp.Core;

/// <summary>
/// The only exception a gateway (real or fake) throws (AD-9). Messages never contain page content.
/// </summary>
public sealed class OneNoteException : Exception
{
    public OneNoteException(
        ErrorCode code,
        string message,
        int? hresult = null,
        Exception? innerException = null,
        bool outcomeUnknown = false,
        string? userAction = null)
        : base(message, innerException)
    {
        Code = code;
        ComHResult = hresult;
        OutcomeUnknown = outcomeUnknown;
        UserAction = userAction;
    }

    public ErrorCode Code { get; }

    /// <summary>The COM HRESULT behind the error, when there was one.</summary>
    public int? ComHResult { get; }

    /// <summary>
    /// True when a write may or may not have reached OneNote (a timed-out or disconnected write, AD-3).
    /// It forces <c>retryable: false</c> in the envelope (AD-9).
    /// </summary>
    public bool OutcomeUnknown { get; }

    /// <summary>
    /// A userAction more specific than the code's default, for a server-side check that knows exactly what the caller
    /// must change. An unknown outcome's userAction still wins (AD-9). Never carries page content.
    /// </summary>
    public string? UserAction { get; }
}
