namespace OneNoteMcp.Core;

/// <summary>
/// The one driven port (hexagonal paradigm). Implementations throw only <see cref="OneNoteException"/>.
/// Methods return tasks that run on the gateway's STA queue.
/// </summary>
public interface IOneNoteGateway
{
    /// <summary>Returns 2013-schema hierarchy XML, unchanged. An empty <paramref name="startNodeId"/> means all notebooks.</summary>
    Task<string> GetHierarchyAsync(string startNodeId, HierarchyScope scope, CancellationToken ct);
}
