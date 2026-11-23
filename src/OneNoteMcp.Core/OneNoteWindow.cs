namespace OneNoteMcp.Core;

/// <summary>
/// One of OneNote's open windows as <c>Windows</c> reports it (story 4.6; spike 4.12), in <c>Item</c> order. Read only
/// through its getters; nothing here is set (AD-11). <see cref="Handle"/> is the window's <c>WindowHandle</c>, the inner
/// pane rather than the top-level frame (W1), and names the window for <c>navigate_to</c>'s <c>window</c> argument.
/// <see cref="Current"/> is true for the window whose handle is <c>CurrentWindow</c>'s. The IDs are exactly what OneNote
/// returned, <c>""</c> included: <see cref="PageId"/> is <c>""</c> in a locked section (W5). <see cref="DockedLocation"/>
/// is OneNote's <c>DockLocation</c> value (-1 default, 0 none, 1 left, 2 right, 3 top, 4 bottom), and
/// <see cref="SideNote"/> is true for a quick note (W3).
/// </summary>
public sealed record OneNoteWindow(
    ulong Handle,
    bool Current,
    bool Active,
    string NotebookId,
    string SectionGroupId,
    string SectionId,
    string PageId,
    bool FullPageView,
    int DockedLocation,
    bool SideNote)
{
    /// <summary>The userAction for a <c>window</c> handle no open window has (story 4.6): <see cref="ErrorCode.InvalidArgument"/>, nothing shown.</summary>
    public const string UnknownHandleUserAction =
        "Call get_windows for OneNote's current windows and pass one of their handles, or leave window out to use OneNote's first window.";

    /// <summary>
    /// A window handle as <c>get_windows</c> reports it and <c>navigate_to</c> takes it (Decision Q1): <c>WindowHandle</c>'s 32
    /// significant bits as a signed number, so a handle with bit 31 set, whether OneNote zero- or sign-extends it, makes the round
    /// trip through the 32-bit integer argument check.
    /// </summary>
    public static int HandleOf(ulong windowHandle) => unchecked((int)windowHandle);

    /// <summary>The message for a <c>window</c> handle no open window has; it names the handle only.</summary>
    public static string UnknownHandleMessage(int handle) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"No open OneNote window has the handle {handle}, so nothing was shown.");
}
