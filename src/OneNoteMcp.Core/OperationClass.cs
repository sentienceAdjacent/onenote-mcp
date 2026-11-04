namespace OneNoteMcp.Core;

/// <summary>
/// The AD-7 operation classes. Every tool declares exactly one (AD-5). It picks the write pipeline and, for
/// <see cref="ErrorCode.ComUnavailable"/> and <see cref="ErrorCode.Timeout"/>, whether a retry can succeed (AD-9).
/// </summary>
public enum OperationClass
{
    Read,
    PageWrite,
    Create,
    HierarchyWrite,
    SoftDelete,
    PermanentDelete,
    ExternalWrite,
    AppState,
}
