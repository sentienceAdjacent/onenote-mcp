using OneNoteMcp.Core;

namespace OneNoteMcp.Tests;

/// <summary>
/// In-memory <see cref="IOneNoteGateway"/>. Like the real adapter, it throws only <see cref="OneNoteException"/>.
/// Only GetHierarchy is scripted; the other read methods fail <see cref="ErrorCode.NotSupported"/> until story 6's fixtures.
/// </summary>
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

    public Task<string> GetHierarchyParentAsync(string objectId, CancellationToken ct) => NotScripted();

    public Task<string> GetPageContentAsync(string pageId, PageInfo pageInfo, CancellationToken ct) => NotScripted();

    public Task<string> GetBinaryPageContentAsync(string pageId, string callbackId, CancellationToken ct) => NotScripted();

    public Task<string> FindPagesAsync(string startNodeId, string query, bool includeUnindexed, CancellationToken ct) => NotScripted();

    public Task<string> FindMetaAsync(string startNodeId, string name, bool includeUnindexed, CancellationToken ct) => NotScripted();

    public Task<string> GetHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) => NotScripted();

    public Task<string> GetWebHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) => NotScripted();

    public Task<string> GetSpecialLocationAsync(SpecialLocation location, CancellationToken ct) => NotScripted();

    private static Task<string> NotScripted([System.Runtime.CompilerServices.CallerMemberName] string method = "") =>
        Task.FromException<string>(new OneNoteException(ErrorCode.NotSupported, $"FakeGateway.{method} is not scripted yet (story 6)."));
}
