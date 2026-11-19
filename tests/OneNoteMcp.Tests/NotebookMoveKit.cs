using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Tests;

/// <summary>
/// Backlog 18: a second notebook X, granted too, and a hook that runs a move once on a chosen read, so a tool test can
/// show the pipeline's under-the-lock re-check (AD-7, story 3.12) refusing a write whose target changed notebook. The
/// caller picks the read (step 1's last before the lock); the kit only makes the move run once and counts the reads.
/// </summary>
internal static class NotebookMoveKit
{
    /// <summary>Notebook X's ID.</summary>
    public const string XId = "{NBX00000-0000-4000-8000-000000000009}{1}{B0}";

    /// <summary>Notebook X's folder.</summary>
    public const string XPath = @"C:\notebooks\X\";

    private const string Ns = "xmlns:one=\"" + OneNoteXml.Namespace2013 + "\"";

    /// <summary>Opens notebook X in <paramref name="gateway"/> and gives it the user's write grant in <paramref name="store"/>.</summary>
    public static void AddGrantedX(FakeGateway gateway, SettingsStore store, TimeProvider clock)
    {
        gateway.Parents[XId] = string.Empty;
        gateway.Hierarchies[(XId, HierarchyScope.Self)] = $"<?xml version=\"1.0\"?>\n<one:Notebook {Ns} name=\"X\" nickname=\"X\" ID=\"{XId}\" path=\"{XPath}\" />";
        new SettingsWriter(store, clock).SaveWriteGrant(
            new NotebookGrant(GrantKeys.NotebookKey(XPath), "X", XId, Write: true, PermanentDelete: true, clock.GetUtcNow(), GrantedVia.Cli));
    }

    /// <summary>
    /// Runs <paramref name="move"/> once, on the <paramref name="nth"/> <c>GetHierarchy</c> of <paramref name="id"/> at
    /// <paramref name="scope"/>, the read the caller chose as step 1's last before the write lock (a grant walk ends at the
    /// notebook's <c>self</c> read). Nothing here checks that choice: a test whose read order could drift pins it with
    /// <see cref="NotebookMove.Reads"/>.
    /// </summary>
    public static NotebookMove MoveOnRead(FakeGateway gateway, string id, HierarchyScope scope, Action move, int nth = 1)
    {
        var reads = 0;
        var moved = false;
        gateway.OnGetHierarchy = (readId, readScope) =>
        {
            if (readId == id && readScope == scope && ++reads == nth && !moved)
            {
                moved = true;
                move();
            }
        };
        return new NotebookMove(() => moved, () => reads);
    }
}

/// <summary>Whether the move ran, and how many matching reads the call made in all.</summary>
internal sealed record NotebookMove(Func<bool> Moved, Func<int> Reads);
