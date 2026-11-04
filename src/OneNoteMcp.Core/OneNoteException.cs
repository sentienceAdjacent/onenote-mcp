namespace OneNoteMcp.Core;

/// <summary>
/// The only exception a gateway (real or fake) throws (AD-9). Messages never contain page content.
/// </summary>
public sealed class OneNoteException : Exception
{
    public OneNoteException(ErrorCode code, string message, int? hresult = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        ComHResult = hresult;
    }

    public ErrorCode Code { get; }

    /// <summary>The COM HRESULT behind the error, when there was one.</summary>
    public int? ComHResult { get; }
}
