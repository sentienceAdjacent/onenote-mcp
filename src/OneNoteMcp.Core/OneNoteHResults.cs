namespace OneNoteMcp.Core;

/// <summary>
/// The OneNote HRESULTs that Core use cases branch on, declared once. <c>OneNoteMcp.Com</c>'s <c>HResultMap</c> keeps the
/// full name table and the mapping to <see cref="ErrorCode"/> (AD-9).
/// </summary>
public static class OneNoteHResults
{
    /// <summary>hrBinaryObjectDoesNotExist.</summary>
    public const int BinaryObjectDoesNotExist = unchecked((int)0x8004200F);

    /// <summary>hrObjectDoesNotExist.</summary>
    public const int ObjectDoesNotExist = unchecked((int)0x80042014);
}
