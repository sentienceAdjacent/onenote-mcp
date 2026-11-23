using System.Text.RegularExpressions;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.AppState;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.UseCases;

/// <summary>What is at a path, for <see cref="INotebookFolderProbe"/>.</summary>
public enum PathEntry
{
    Missing,
    File,
    Folder,
}

/// <summary>
/// The file-system reads <see cref="OpenNotebookUseCase"/> makes (story 4.5), a seam for unit tests: what is at a path, and
/// whether a folder directly holds a notebook's table of contents (<c>*.onetoc2</c>). Neither ever writes.
/// </summary>
public interface INotebookFolderProbe
{
    /// <summary>What is at <paramref name="path"/> now; a path that can't be read counts as <see cref="PathEntry.Missing"/>.</summary>
    PathEntry Entry(string path);

    /// <summary>True when <paramref name="folder"/> itself (not below it) holds a <c>*.onetoc2</c> file, hidden and system files included.</summary>
    bool HoldsNotebookToc(string folder);
}

/// <summary>What <see cref="OpenNotebookUseCase"/> logs for one call (AD-13): never the path.</summary>
/// <param name="CorrelationId">The tool call's correlation ID.</param>
/// <param name="PathKind"><c>url</c> or <c>localPath</c> (anything that isn't a URL, refused or not).</param>
/// <param name="Rule">The rule that refused the path (<see cref="OpenNotebookUseCase.Rules"/>), or <c>none</c>.</param>
/// <param name="Outcome"><c>opened</c>, <c>alreadyOpen</c>, <c>refused</c>, or the error code of a failure.</param>
public sealed record OpenNotebookLogEntry(string CorrelationId, string PathKind, string Rule, string Outcome);

/// <summary>
/// <c>open_notebook</c> (story 4.5; AD-7 <c>AppState</c>, <c>open_notebook</c>): opens an existing local notebook folder in
/// OneNote with <c>OpenHierarchy</c> <c>cftNone</c>, through the <see cref="AppStatePipeline"/> under the write lock (AD-18;
/// Phill, 2026-11-22, OQ1 a). User-visible (AD-11). It never creates anything: <c>cftNone</c> is no guard (spike 4.1 O6
/// turned an empty folder into a notebook), so every check runs before COM. No grant is needed and none is given (AD-6);
/// what it opens is protected or not by AD-21's What's protected on the canonical path.
/// <list type="bullet">
/// <item>Step 1: the path is made canonical (<see cref="FilePathGate.Canonical(string, out FilePathRefusal)"/>, as
/// <c>PageWrite</c> does); every check and the COM call use that form. <see cref="ErrorCode.InvalidArgument"/> with a
/// userAction for an empty path, a URL, every <see cref="FilePathRefusal"/>, a <c>.one</c>, <c>.onepkg</c> or
/// <c>.onetoc2</c> path, a missing path, a file, and a folder inside a OneDrive-synced folder.</item>
/// <item>Step 3, under the lock: one read of the open notebooks. One whose <c>NotebookKey</c> or <c>path</c> (as given,
/// as a <c>\\localhost\X$</c> path read as <c>X:\</c>, and made canonical) equals the canonical path is answered with its
/// current ID and <see cref="ToolWarnings.AlreadyExisted"/> (4.5 plan Q1 a), and nothing is sent to OneNote.</item>
/// <item>Step 4, under the lock: the OneDrive check again; then the notebook test: the folder still exists, no folder
/// above it holds a <c>.onetoc2</c>, and it holds one itself.</item>
/// <item>Step 7: <c>OpenHierarchy</c> of the canonical path, sent by the gateway as a write, never retried; a timeout,
/// disconnect, cancel or <c>0x800706BE</c> is <c>outcomeUnknown</c>, and the pipeline keeps the lock until it settles. Then
/// one read of the open notebooks for the result's <c>one:Notebook</c> element; a failed read leaves <c>xml</c> out.</item>
/// </list>
/// The result is <c>meta.objectId</c>, <c>meta.path</c> (canonical) and the warning
/// <see cref="ToolWarnings.NotebookListMayLag"/>. Messages and logs never carry the path (AD-13).
/// </summary>
public sealed partial class OpenNotebookUseCase : IUseCase
{
    /// <summary>The tool's name (AD-14).</summary>
    public const string OperationName = "open_notebook";

    /// <summary>The rule names the log line carries (AD-13), one per refusal.</summary>
    public static class Rules
    {
        public const string None = "none";
        public const string Empty = "empty";
        public const string Url = "url";
        public const string Network = "network";
        public const string Device = "device";
        public const string NotFullyQualified = "notFullyQualified";
        public const string InvalidCharacters = "invalidCharacters";
        public const string TooManyLinks = "tooManyLinks";
        public const string SectionFile = "sectionFile";
        public const string Package = "package";
        public const string TocFile = "tocFile";
        public const string Missing = "missing";
        public const string NotAFolder = "notAFolder";
        public const string OneDrive = "oneDrive";
        public const string InsideANotebook = "insideANotebook";
        public const string NoNotebookToc = "noNotebookToc";
    }

    /// <summary>What to do about a URL: the server never opens one (AD-7 <c>AppState</c>).</summary>
    public const string UrlUserAction =
        "open_notebook opens local notebook folders only. A notebook stored online is reopened by the user in OneNote (File > Open, under the account that stores it).";

    /// <summary>What to do about a path <see cref="FilePathGate"/> refuses.</summary>
    public const string PathUserAction =
        "Pass the full local path (C:\\…) of the notebook's folder, as close_notebook's meta.path gives it. Network, device and relative paths are refused; the user can open such a notebook in OneNote (File > Open).";

    /// <summary>What to do about a section file.</summary>
    public const string SectionFileUserAction =
        "open_notebook opens a whole notebook by its folder, not a section file: pass the folder of the notebook that holds the section, or the user opens the section in OneNote.";

    /// <summary>What to do about a package file.</summary>
    public const string PackageUserAction =
        "This is a OneNote package: open it with open_package, which unpacks it as a separate copy.";

    /// <summary>What to do about a notebook's table-of-contents file.</summary>
    public const string TocFileUserAction =
        "Pass the folder that holds this .onetoc2 file, not the file itself.";

    /// <summary>What to do about a missing path or one that is a file.</summary>
    public const string MissingUserAction =
        "Pass the full local path of an existing notebook folder, such as close_notebook's meta.path. To make a new notebook, use create_notebook.";

    /// <summary>What to do about a folder inside OneDrive (3.8 Q3: the sync client converts it, and OneNote closes).</summary>
    public const string OneDriveUserAction =
        "The folder is inside a OneDrive-synced folder, which this server doesn't open: OneDrive's client would turn it into an online notebook while OneNote has it open. The user opens it in OneNote (File > Open).";

    /// <summary>What to do about a folder inside another notebook's folder (a section group's, say).</summary>
    public const string InsideANotebookUserAction =
        "The folder is inside another notebook's folder (a section group's folder, for example). Open that notebook's folder instead, or the user opens this folder in OneNote.";

    /// <summary>What to do about a folder with no <c>.onetoc2</c>.</summary>
    public const string NoNotebookTocUserAction =
        "The folder holds no notebook (no .onetoc2 file). A notebook just created may not have one yet: wait a few minutes and try again, or the user opens the folder in OneNote (File > Open).";

    /// <summary>The <see cref="ToolWarnings.NotebookListMayLag"/> message.</summary>
    public const string ListMayLagMessage =
        "OneNote opened the notebook. Its sections and pages may take a few seconds to list (a notebook of 11 sections took 16 s), and OneNote's own notebook list may show it about a minute late.";

    /// <summary>The <see cref="ToolWarnings.NotebookListMayLag"/> userAction.</summary>
    public const string ListMayLagUserAction =
        "If get_hierarchy doesn't show its sections or pages yet, read it again in a few seconds. Use meta.objectId: the notebook has new IDs each time it is opened.";

    private readonly IOneNoteGateway gateway;
    private readonly AppStatePipeline pipeline;
    private readonly Func<OneDriveFolders> oneDrive;
    private readonly FilePathGate.ILinkReader? links;
    private readonly INotebookFolderProbe probe;
    private readonly Action<OpenNotebookLogEntry>? log;

    /// <param name="gateway">Reads the open notebooks and opens the folder.</param>
    /// <param name="pipeline">The AD-7 <c>AppState</c> step runner, with the write lock.</param>
    /// <param name="oneDrive">The OneDrive folders on this machine (backlog 13's seam), asked at step 1 and under the lock; none known when null.</param>
    /// <param name="log">Receives one entry per call (AD-13); none when null.</param>
    public OpenNotebookUseCase(IOneNoteGateway gateway, AppStatePipeline pipeline, Func<OneDriveFolders>? oneDrive = null, Action<OpenNotebookLogEntry>? log = null)
        : this(gateway, pipeline, oneDrive, log, links: null, probe: null)
    {
    }

    /// <summary>The seam unit tests use: links and the file system faked.</summary>
    internal OpenNotebookUseCase(
        IOneNoteGateway gateway,
        AppStatePipeline pipeline,
        Func<OneDriveFolders>? oneDrive,
        Action<OpenNotebookLogEntry>? log,
        FilePathGate.ILinkReader? links,
        INotebookFolderProbe? probe)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(pipeline);
        this.gateway = gateway;
        this.pipeline = pipeline;
        this.oneDrive = oneDrive ?? (() => OneDriveFolders.None);
        this.log = log;
        this.links = links;
        this.probe = probe ?? FileSystemProbe.Instance;
    }

    public OperationClass Class => OperationClass.AppState;

    /// <summary>Opens the notebook folder at <paramref name="path"/>.</summary>
    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var open = new Open(this, path ?? string.Empty);
        try
        {
            var opened = await pipeline.RunAsync(open, ct, context.CorrelationId).ConfigureAwait(false);
            Log(context, open, opened.AlreadyOpen ? "alreadyOpen" : "opened");
            var warning = opened.AlreadyOpen
                ? new ToolWarning(
                    ToolWarnings.AlreadyExisted,
                    $"The notebook at this path is already open in OneNote, so nothing was opened: meta.objectId is its current ID, {opened.Id}.")
                : new ToolWarning(ToolWarnings.NotebookListMayLag, ListMayLagMessage, ListMayLagUserAction);
            return new ToolSuccess(opened.Xml, new ToolMeta(context.CorrelationId, Path: open.Canonical, ObjectId: opened.Id), [warning]);
        }
        catch (OneNoteException ex)
        {
            Log(context, open, open.Rule == Rules.None ? ex.Code.ToString() : "refused");
            throw;
        }
        catch (OperationCanceledException)
        {
            Log(context, open, nameof(ErrorCode.Cancelled));
            throw;
        }
    }

    private void Log(ToolCallContext context, Open open, string outcome) =>
        log?.Invoke(new OpenNotebookLogEntry(context.CorrelationId, open.PathKind, open.Rule, outcome));

    /// <summary>
    /// True when an open notebook's <paramref name="notebookPath"/> names the folder <paramref name="canonical"/>: its
    /// <c>NotebookKey</c>, its <c>\\localhost\X$</c> form read as <c>X:\</c>, or either made canonical, equals it ignoring
    /// case (AD-7 <c>AppState</c>, Already open; spike 4.1 O9, O10). A URL never does.
    /// </summary>
    internal static bool Names(string? notebookPath, string canonical, FilePathGate.ILinkReader? links)
    {
        if (string.IsNullOrWhiteSpace(notebookPath) || CloudNotebookCreate.IsUrl(notebookPath))
        {
            return false;
        }

        var target = GrantKeys.NotebookKey(canonical);
        var forms = new List<string> { GrantKeys.NotebookKey(notebookPath) };
        if (MapLocalhostAdminShare(notebookPath) is { } mapped)
        {
            forms.Add(GrantKeys.NotebookKey(mapped));
        }

        foreach (var form in forms.ToList())
        {
            var resolved = links is null ? FilePathGate.Canonical(form, out _) : FilePathGate.Canonical(form, links, out _);
            if (resolved is not null)
            {
                forms.Add(GrantKeys.NotebookKey(resolved));
            }
        }

        return forms.Any(f => string.Equals(f, target, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary><c>\\localhost\X$\rest</c> as <c>X:\rest</c>, ignoring case; null for anything else (other servers or shares).</summary>
    internal static string? MapLocalhostAdminShare(string path)
    {
        var match = LocalhostAdminShare().Match(path.Replace('/', '\\'));
        return match.Success
            ? char.ToUpperInvariant(match.Groups["drive"].Value[0]) + ":" + (match.Groups["rest"].Value.Length == 0 ? "\\" : match.Groups["rest"].Value)
            : null;
    }

    [GeneratedRegex(@"^\\\\localhost\\(?<drive>[A-Za-z])\$(?<rest>\\.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocalhostAdminShare();

    private static bool IsUrl(string path)
    {
        var trimmed = path.TrimStart();
        return CloudNotebookCreate.IsUrl(trimmed)
            || trimmed.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What step 7 (or step 3, for an open notebook) found.</summary>
    private sealed record Opened(string Id, string? Xml, bool AlreadyOpen);

    private sealed class Open(OpenNotebookUseCase owner, string path) : AppStateOperation<Opened>
    {
        private Opened? alreadyOpen;

        /// <summary>The canonical path, once step 1 made it.</summary>
        public string? Canonical { get; private set; }

        /// <summary>The rule that refused the path, or <see cref="Rules.None"/>.</summary>
        public string Rule { get; private set; } = Rules.None;

        public string PathKind => IsUrl(path) ? "url" : "localPath";

        public override bool TakesWriteLock => true;

        public override void Validate()
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw Refuse(Rules.Empty, "path is empty, so nothing was opened.", MissingUserAction);
            }

            if (IsUrl(path))
            {
                throw Refuse(Rules.Url, "path is a URL, and open_notebook never opens one (OneNote can turn a cloud folder into a new notebook), so nothing was opened.", UrlUserAction);
            }

            var canonical = (owner.links is null ? FilePathGate.Canonical(path, out var refusal) : FilePathGate.Canonical(path, owner.links, out refusal))
                ?? throw Refuse(RuleOf(refusal), $"path is {Describe(refusal)}, so nothing was opened.", PathUserAction);
            Canonical = canonical;

            // The file-type refusals name files only: a folder called "Q3.one" is still a folder.
            var entry = owner.probe.Entry(canonical);
            var extension = entry == PathEntry.File ? Path.GetExtension(GrantKeys.NotebookKey(canonical)) : string.Empty;
            if (string.Equals(extension, ".one", StringComparison.OrdinalIgnoreCase))
            {
                throw Refuse(Rules.SectionFile, "path names a section file (.one), not a notebook folder, so nothing was opened.", SectionFileUserAction);
            }

            if (string.Equals(extension, ".onepkg", StringComparison.OrdinalIgnoreCase))
            {
                throw Refuse(Rules.Package, "path names a OneNote package (.onepkg), not a notebook folder, so nothing was opened.", PackageUserAction);
            }

            if (string.Equals(extension, ".onetoc2", StringComparison.OrdinalIgnoreCase))
            {
                throw Refuse(Rules.TocFile, "path names a notebook's table-of-contents file (.onetoc2), not its folder, so nothing was opened.", TocFileUserAction);
            }

            CheckIsAFolder(entry);
            CheckOneDrive(canonical);
        }

        public override async Task ReadAsync(CancellationToken ct)
        {
            var canonical = Canonical ?? throw new InvalidOperationException("Step 1 didn't run.");

            // Step 3: one read of the open notebooks, under the lock (AD-7 AppState, Already open).
            var notebooksXml = await owner.gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, ct).ConfigureAwait(false);
            var open = XmlReads.Readable(() => HierarchyNode.Notebooks(notebooksXml), "GetHierarchy")
                .FirstOrDefault(n => Names(n.Path, canonical, owner.links));
            if (open is not null)
            {
                alreadyOpen = new Opened(open.Id, HierarchyNode.NotebookElement(notebooksXml, open.Id), AlreadyOpen: true);
                return;
            }

            // Step 4: the OneDrive check again, then the notebook test (OQ3 a).
            CheckOneDrive(canonical);
            CheckFolderExists(canonical);
            for (var above = Path.GetDirectoryName(canonical); !string.IsNullOrEmpty(above); above = Path.GetDirectoryName(above))
            {
                if (owner.probe.HoldsNotebookToc(above))
                {
                    throw Refuse(Rules.InsideANotebook, "The folder is inside another notebook's folder (a folder above it holds a .onetoc2 file), so nothing was opened.", InsideANotebookUserAction);
                }
            }

            if (!owner.probe.HoldsNotebookToc(canonical))
            {
                throw Refuse(Rules.NoNotebookToc, "The folder holds no .onetoc2 file, so it isn't a notebook's folder, and nothing was opened.", NoNotebookTocUserAction);
            }
        }

        public override async Task<Opened> CallAsync(CancellationToken ct)
        {
            if (alreadyOpen is not null)
            {
                return alreadyOpen;
            }

            var canonical = Canonical ?? throw new InvalidOperationException("Step 1 didn't run.");

            // cftNone, never retried; the gateway sends it as a write (AD-3).
            var id = await owner.gateway.OpenHierarchyAsync(canonical, ct).ConfigureAwait(false);

            // The notebook's element, read after the open. The open stands whatever this read does.
            string? xml = null;
            try
            {
                var notebooksXml = await owner.gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, ct).ConfigureAwait(false);
                xml = HierarchyNode.NotebookElement(notebooksXml, id);
            }
            catch (Exception ex) when (ex is OneNoteException or OneNoteXmlException or System.Xml.XmlException or OperationCanceledException)
            {
                // Left out: the open succeeded.
            }

            return new Opened(id, xml, AlreadyOpen: false);
        }

        private void CheckFolderExists(string canonical) => CheckIsAFolder(owner.probe.Entry(canonical));

        private void CheckIsAFolder(PathEntry entry)
        {
            switch (entry)
            {
                case PathEntry.Missing:
                    throw Refuse(Rules.Missing, "Nothing exists at path, so nothing was opened.", MissingUserAction);
                case PathEntry.File:
                    throw Refuse(Rules.NotAFolder, "path names a file, not a notebook folder, so nothing was opened.", MissingUserAction);
            }
        }

        private void CheckOneDrive(string canonical)
        {
            if (owner.oneDrive().Holds(canonical))
            {
                throw Refuse(Rules.OneDrive, "The folder is inside a OneDrive-synced folder, so nothing was opened.", OneDriveUserAction);
            }
        }

        private OneNoteException Refuse(string rule, string message, string userAction)
        {
            Rule = rule;
            return new OneNoteException(ErrorCode.InvalidArgument, message, userAction: userAction, retryable: false);
        }

        private static string RuleOf(FilePathRefusal refusal) => refusal switch
        {
            FilePathRefusal.Network => Rules.Network,
            FilePathRefusal.Device => Rules.Device,
            FilePathRefusal.InvalidCharacters => Rules.InvalidCharacters,
            FilePathRefusal.TooManyLinks => Rules.TooManyLinks,
            _ => Rules.NotFullyQualified,
        };

        private static string Describe(FilePathRefusal refusal) => refusal switch
        {
            FilePathRefusal.Network => "on the network (a UNC path, a mapped network drive, or a link to one)",
            FilePathRefusal.Device => "a device path",
            FilePathRefusal.InvalidCharacters => "a path with control, invisible or wildcard characters",
            FilePathRefusal.TooManyLinks => $"a path through more than {FilePathGate.MaxLinkHops} links or junctions, or a loop",
            _ => "not a full local path (C:\\…)",
        };
    }

    /// <summary>The real file system; reads only.</summary>
    private sealed class FileSystemProbe : INotebookFolderProbe
    {
        public static FileSystemProbe Instance { get; } = new();

        public PathEntry Entry(string path)
        {
            try
            {
                return Directory.Exists(path) ? PathEntry.Folder : File.Exists(path) ? PathEntry.File : PathEntry.Missing;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return PathEntry.Missing;
            }
        }

        public bool HoldsNotebookToc(string folder) => NotebookFolders.HoldsANotebookToc(folder);
    }
}
