namespace OneNoteMcp.Core.Writes;

/// <summary>
/// What the page move (<see cref="MovePageWrite"/>), the section move (<see cref="MoveContainerWrite"/>) and the restore from
/// the recycle bin (<see cref="RestoreFromRecycleBinWrite"/>) share (backlog 17): the moves' journal operation name and the
/// advice about a position in a password-protected target. Moved unchanged from <see cref="MovePageWrite"/>.
/// </summary>
public static class MoveWrites
{
    /// <summary>The journal operation of a page or container move: the tool's name.</summary>
    public const string OperationName = "move_hierarchy_object";

    /// <summary>
    /// What to do about a <c>position</c> in a password-protected target without a read grant (3.4 security review, Phill
    /// 2026-11-17): a position's checks would show the section's page count and where its subpages sit.
    /// </summary>
    public const string ProtectedPositionUserAction =
        "Leave position out to put the page last in the password-protected section. Placing it at a position needs a read grant for that section, held when the call starts: ask the user whether to grant it (they can run the grant command with --read-section, or accept the dialog that appears when you read one of its pages with get_page_content in a client that supports it), then retry. Never run the grant command yourself.";
}
