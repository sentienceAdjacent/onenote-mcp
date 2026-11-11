using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>CreateNewPage</c>: adds an empty page to a section, through the write pipeline (AD-7) as a <c>Create</c>. Step 1
/// needs the write grant of the section's notebook (AD-6); in a password-protected section that grant alone suffices,
/// and no read-grant dialog is ever raised (AD-21). Step 3 reads the section at <c>self</c>; step 4 refuses an ID that
/// isn't a section, or a section in the recycle bin, with <see cref="ErrorCode.ObjectNotFound"/> (Phill, 2026-11-10, 2.4
/// plan Q8), and a locked section with <see cref="ErrorCode.SectionLocked"/>, before COM. The new page's ID is
/// <c>meta.pageId</c> (Q2). It is returned in a protected section too: the model made the page, and the ID reveals
/// nothing inside the section. Messages carry IDs only (AD-9).
/// </summary>
public sealed class CreatePageUseCase(IOneNoteGateway gateway, WriteAccess access, WritePipeline pipeline) : IUseCase
{
    /// <summary>What to do when <c>sectionId</c> isn't a live section's ID.</summary>
    public const string NotASectionUserAction =
        "create_page needs the ID of a section that isn't in the recycle bin. Read the hierarchy (get_hierarchy with scope sections) and pass a one:Section ID.";

    public OperationClass Class => OperationClass.Create;

    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string sectionId, NewPageStyle style, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var pageId = await pipeline.RunAsync(context, new CreatePage(gateway, access, sectionId ?? string.Empty, style), ct).ConfigureAwait(false);
        return new ToolSuccess(null, new ToolMeta(context.CorrelationId, PageId: pageId), []);
    }

    private static OneNoteException NotASection(string sectionId, string why) =>
        new(ErrorCode.ObjectNotFound, $"Object {sectionId} {why}, so no page was created.", userAction: NotASectionUserAction);

    /// <summary>The <c>Create</c> class's steps for one <c>CreateNewPage</c>.</summary>
    private sealed class CreatePage(IOneNoteGateway gateway, WriteAccess access, string sectionId, NewPageStyle style) : WriteOperation<string>
    {
        private HierarchyNode? section;
        private string? createdPageId;

        public override OperationClass Class => OperationClass.Create;

        public override IReadOnlyCollection<string> WrittenPageIds => createdPageId is null ? [] : [createdPageId];

        public override Task AuthorizeAsync(CancellationToken ct) =>
            access.CheckAsync(sectionId, () => NotASection(sectionId, "isn't a section"), ct);

        public override async Task ReadAsync(CancellationToken ct)
        {
            var xml = await gateway.GetHierarchyAsync(sectionId, HierarchyScope.Self, ct).ConfigureAwait(false);
            section = XmlReads.Readable(() => HierarchyNode.Root(xml), "GetHierarchy");
        }

        public override void Validate()
        {
            var node = section ?? throw new InvalidOperationException("Validate runs after ReadAsync.");
            if (node.Kind != HierarchyKind.Section)
            {
                throw NotASection(sectionId, "isn't a section");
            }

            if (node.InRecycleBin)
            {
                throw NotASection(sectionId, "is in the recycle bin");
            }

            if (node.Locked)
            {
                throw new OneNoteException(ErrorCode.SectionLocked, $"Section {sectionId} is locked, so no page was created.");
            }
        }

        public override async Task<string> CallAsync(CancellationToken ct)
        {
            createdPageId = await gateway.CreateNewPageAsync(sectionId, style, ct).ConfigureAwait(false);
            return createdPageId;
        }
    }
}
