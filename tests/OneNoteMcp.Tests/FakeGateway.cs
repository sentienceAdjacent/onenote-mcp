using OneNoteMcp.Core;

namespace OneNoteMcp.Tests;

/// <summary>In-memory <see cref="IOneNoteGateway"/>. Like the real adapter, it throws only <see cref="OneNoteException"/>.</summary>
internal sealed class FakeGateway : IOneNoteGateway
{
    public string HierarchyXml { get; set; } = string.Empty;

    public OneNoteException? Failure { get; set; }

    public List<(string StartNodeId, HierarchyScope Scope)> Calls { get; } = [];

    public Task<string> GetHierarchyAsync(string startNodeId, HierarchyScope scope, CancellationToken ct)
    {
        Calls.Add((startNodeId, scope));
        return Failure is null ? Task.FromResult(HierarchyXml) : Task.FromException<string>(Failure);
    }
}
