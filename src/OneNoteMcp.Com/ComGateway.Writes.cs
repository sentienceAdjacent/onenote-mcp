using System.Globalization;
using System.Runtime.InteropServices;
using OneNoteMcp.Com.Interop;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Xml;
using CoreNewPageStyle = OneNoteMcp.Core.NewPageStyle;
using CorePageInfo = OneNoteMcp.Core.PageInfo;
using CoreScope = OneNoteMcp.Core.HierarchyScope;
using CoreSpecialLocation = OneNoteMcp.Core.SpecialLocation;
using InteropNewPageStyle = OneNoteMcp.Com.Interop.NewPageStyle;
using InteropPageInfo = OneNoteMcp.Com.Interop.PageInfo;
using InteropScope = OneNoteMcp.Com.Interop.HierarchyScope;
using InteropSpecialLocation = OneNoteMcp.Com.Interop.SpecialLocation;

namespace OneNoteMcp.Com;

public sealed partial class ComGateway
{
    public Task SyncHierarchyAsync(string hierarchyId, CancellationToken ct)
    {
        const string method = nameof(IApplication.SyncHierarchy);
        if (string.IsNullOrEmpty(hierarchyId))
        {
            return Task.FromException(new OneNoteException(
                ErrorCode.InvalidArgument,
                $"{method} wasn't sent: no hierarchy ID was given.",
                retryable: false));
        }

        return RunAsync(
            method,
            app =>
            {
                app.SyncHierarchy(hierarchyId);
                return true;
            },
            // AppState (story 4.3): never repeated after a disconnect, and never an abandoned write (AD-3, AD-7).
            CallKind.AppState,
            ct);
    }

    public Task CloseNotebookAsync(string notebookId, CancellationToken ct)
    {
        const string method = nameof(IApplication.CloseNotebook);
        if (string.IsNullOrEmpty(notebookId))
        {
            return Task.FromException(new OneNoteException(
                ErrorCode.InvalidArgument,
                $"{method} wasn't sent: no notebook ID was given.",
                retryable: false));
        }

        return RunAsync(
            method,
            app =>
            {
                // Never force (Phill, 2026-11-12, epic 4 Q1 a).
                app.CloseNotebook(notebookId, force: false);
                return true;
            },
            // AppState sent as a write (story 4.4; AD-3, AD-7): never repeated after a disconnect, and tracked as an
            // abandoned write if its caller stops waiting while it runs, so the write lock is kept until it settles (AD-18).
            CallKind.Write,
            ct);
    }

    public Task<string> CreateNewPageAsync(string sectionId, CoreNewPageStyle style, CancellationToken ct)
    {
        var interopStyle = MapNewPageStyle(style);
        return RunAsync(
            nameof(IApplication.CreateNewPage),
            app =>
            {
                app.CreateNewPage(sectionId ?? string.Empty, out var pageId, interopStyle);
                return pageId;
            },
            // A write is never retried after a disconnect (AD-3): the pipeline reports outcomeUnknown instead.
            CallKind.Write,
            ct);
    }

    public Task<string> CreateSectionAsync(string parentId, string sectionFileName, CancellationToken ct)
    {
        const string method = nameof(IApplication.OpenHierarchy);
        if (string.IsNullOrEmpty(parentId))
        {
            return Task.FromException<string>(new OneNoteException(
                ErrorCode.InvalidArgument,
                $"{method} wasn't sent: no parent ID was given for the new section.",
                retryable: false));
        }

        if (SectionFileNames.Problem(sectionFileName) is { } problem)
        {
            return Task.FromException<string>(new OneNoteException(
                ErrorCode.InvalidArgument,
                $"{method} wasn't sent: the new section's file name {problem}.",
                userAction: NameUserAction,
                retryable: false));
        }

        return RunAsync(
            method,
            app =>
            {
                // Relative to the parent's ID only, never an absolute path (AD-7 Create row). An existing name returns the
                // existing section's ID (spike 5.1 constraint 5).
                app.OpenHierarchy(sectionFileName, parentId, out var sectionId, CreateFileType.cftSection);
                return sectionId;
            },
            // A write is never retried after a disconnect (AD-3): the pipeline reports outcomeUnknown instead.
            CallKind.Write,
            ct);
    }

    public Task<string> CreateSectionGroupAsync(string parentId, string groupName, CancellationToken ct)
    {
        const string method = nameof(IApplication.OpenHierarchy);
        if (string.IsNullOrEmpty(parentId))
        {
            return Task.FromException<string>(new OneNoteException(
                ErrorCode.InvalidArgument,
                $"{method} wasn't sent: no parent ID was given for the new section group.",
                retryable: false));
        }

        if (SectionFileNames.NameProblem(groupName) is { } problem)
        {
            return Task.FromException<string>(new OneNoteException(
                ErrorCode.InvalidArgument,
                $"{method} wasn't sent: the new section group's name {problem}.",
                userAction: NameUserAction,
                retryable: false));
        }

        // OpenHierarchy with cftFolder fails 0x80042018 for any '.' in the name (sitting 2026-11-15).
        if (SectionFileNames.GroupPeriodProblem(groupName) is { } period)
        {
            return Task.FromException<string>(new OneNoteException(
                ErrorCode.InvalidArgument,
                $"{method} wasn't sent: the new section group's name {period}.",
                userAction: SectionFileNames.GroupPeriodUserAction,
                retryable: false));
        }

        return RunAsync(
            method,
            app =>
            {
                // Relative to the parent's ID only, never an absolute path (AD-7 Create row).
                app.OpenHierarchy(groupName, parentId, out var groupId, CreateFileType.cftFolder);
                return groupId;
            },
            // A write is never retried after a disconnect (AD-3): the pipeline reports outcomeUnknown instead.
            CallKind.Write,
            ct);
    }

    public Task<string> CreateNotebookAsync(string folderPath, CancellationToken ct)
    {
        const string method = nameof(IApplication.OpenHierarchy);
        if (NotebookFolderProblem(folderPath) is { } problem)
        {
            return Task.FromException<string>(new OneNoteException(
                ErrorCode.InvalidArgument,
                $"{method} wasn't sent: the new notebook's folder {problem}.",
                userAction: "Create a notebook only in a local folder (C:\\…), named with no characters a Windows folder name can't hold.",
                retryable: false));
        }

        return RunAsync(
            method,
            app =>
            {
                // An absolute local folder, no parent ID (AD-7 Create row). The caller checked it is absent or empty.
                app.OpenHierarchy(folderPath, string.Empty, out var notebookId, CreateFileType.cftNotebook);
                return notebookId;
            },
            // A write is never retried after a disconnect (AD-3): the pipeline reports outcomeUnknown instead.
            CallKind.Write,
            ct);
    }

    public Task<string> OpenPackageAsync(string packagePath, string destinationFolder, CancellationToken ct)
    {
        const string method = nameof(IApplication.OpenPackage);
        if (LocalFileProblem(packagePath) is { } packageProblem)
        {
            return Task.FromException<string>(new OneNoteException(ErrorCode.InvalidArgument, $"{method} wasn't sent: the package {packageProblem}.", retryable: false));
        }

        if (LocalFileProblem(destinationFolder?.TrimEnd('\\')) is { } folderProblem)
        {
            return Task.FromException<string>(new OneNoteException(ErrorCode.InvalidArgument, $"{method} wasn't sent: the destination folder {folderProblem}.", retryable: false));
        }

        // Story 3.8: an ExternalWrite under timeouts.publishSeconds (a big package takes a while), never retried (AD-3).
        return RunAsync(
            method,
            app =>
            {
                app.OpenPackage(packagePath, destinationFolder!, out var pathOut);
                return pathOut;
            },
            CallKind.Write,
            ct,
            publish: true);
    }

    public Task<string> OpenHierarchyAsync(string path, CancellationToken ct)
    {
        const string method = nameof(IApplication.OpenHierarchy);
        if (LocalFileProblem(path?.TrimEnd('\\')) is { } problem)
        {
            return Task.FromException<string>(new OneNoteException(ErrorCode.InvalidArgument, $"{method} wasn't sent: the path {problem}.", retryable: false));
        }

        // Story 3.8: open an existing folder or file (cftNone never creates); AppState, never retried after a disconnect.
        return RunAsync(
            method,
            app =>
            {
                app.OpenHierarchy(path!, string.Empty, out var id, CreateFileType.cftNone);
                return id;
            },
            CallKind.Write,
            ct);
    }

    private const string NameUserAction = "Use a name with no path separators, no '..' and no characters a Windows file name can't hold.";

    /// <summary>
    /// Why <paramref name="folderPath"/> can't be a new notebook's folder, as a phrase that never quotes it; null when it is
    /// a fully qualified local path (not UNC, a device path or a URL) whose last segment is a valid name.
    /// </summary>
    private static string? NotebookFolderProblem(string? folderPath)
    {
        if (string.IsNullOrEmpty(folderPath))
        {
            return "is empty";
        }

        var slashed = folderPath.Replace('/', '\\');
        if (slashed.StartsWith(@"\\", StringComparison.Ordinal) || slashed.StartsWith(@"\??\", StringComparison.Ordinal)
            || !System.IO.Path.IsPathFullyQualified(slashed) || slashed.Length < 3 || !char.IsAsciiLetter(slashed[0]) || slashed[1] != ':')
        {
            return "isn't a fully qualified local path";
        }

        var leaf = System.IO.Path.GetFileName(slashed.TrimEnd('\\'));
        return SectionFileNames.NameProblem(leaf) is { } problem ? "has a last segment that " + problem : null;
    }

    public Task UpdatePageContentAsync(string pageXml, string expectedLastModified, CancellationToken ct)
    {
        const string method = nameof(IApplication.UpdatePageContent);
        if (OneNoteDates.ToUtc(expectedLastModified) is not { } date)
        {
            return Task.FromException(BadExpectedDate(method));
        }

        return RunAsync(
            method,
            app =>
            {
                // force is always false: OneNote checks the date for an open page (AD-7, AD-17).
                app.UpdatePageContent(pageXml ?? string.Empty, date, XMLSchema.xs2013, force: false);
                return true;
            },
            CallKind.Write,
            ct);
    }

    public Task DeletePageContentAsync(string pageId, string objectId, string expectedLastModified, CancellationToken ct)
    {
        const string method = nameof(IApplication.DeletePageContent);
        if (OneNoteDates.ToUtc(expectedLastModified) is not { } date)
        {
            return Task.FromException(BadExpectedDate(method));
        }

        return RunAsync(
            method,
            app =>
            {
                app.DeletePageContent(pageId ?? string.Empty, objectId ?? string.Empty, date, force: false);
                return true;
            },
            CallKind.Write,
            ct);
    }

    public Task UpdateHierarchyAsync(string changesXml, CancellationToken ct)
    {
        const string method = nameof(IApplication.UpdateHierarchy);
        if (string.IsNullOrEmpty(changesXml))
        {
            return Task.FromException(new OneNoteException(
                ErrorCode.InvalidArgument,
                $"{method} wasn't sent: no hierarchy XML was given.",
                retryable: false));
        }

        return RunAsync(
            method,
            app =>
            {
                app.UpdateHierarchy(changesXml, XMLSchema.xs2013);
                return true;
            },
            // A write is never retried after a disconnect (AD-3): the pipeline reports outcomeUnknown instead.
            CallKind.Write,
            ct);
    }

    public Task MergeSectionsAsync(string sourceId, string destinationId, CancellationToken ct)
    {
        const string method = nameof(IApplication.MergeSections);
        if (string.IsNullOrEmpty(sourceId) || string.IsNullOrEmpty(destinationId))
        {
            return Task.FromException(new OneNoteException(
                ErrorCode.InvalidArgument,
                $"{method} wasn't sent: no source or destination section ID was given.",
                retryable: false));
        }

        return RunAsync(
            method,
            app =>
            {
                app.MergeSections(sourceId, destinationId);
                return true;
            },
            // A write is never retried after a disconnect (AD-3): the pipeline reports outcomeUnknown instead.
            CallKind.Write,
            ct);
    }

    public Task DeleteHierarchyAsync(string objectId, string expectedLastModified, bool permanent, CancellationToken ct)
    {
        const string method = nameof(IApplication.DeleteHierarchy);
        if (string.IsNullOrEmpty(objectId))
        {
            return Task.FromException(new OneNoteException(
                ErrorCode.InvalidArgument,
                $"{method} wasn't sent: no object ID was given.",
                retryable: false));
        }

        // Never DateTime.MinValue, which skips OneNote's date check (AD-17; live, DeleteHierarchy, 2026-11-12).
        if (OneNoteDates.ToUtc(expectedLastModified) is not { } date)
        {
            return Task.FromException(BadExpectedDate(method, "object's hierarchy"));
        }

        return RunAsync(
            method,
            app =>
            {
                app.DeleteHierarchy(objectId, date, permanent);
                return true;
            },
            // A write is never retried after a disconnect (AD-3): the pipeline reports outcomeUnknown instead.
            CallKind.Write,
            ct);
    }

    public Task PublishAsync(string objectId, string targetPath, ExportFormat format, CancellationToken ct)
    {
        const string method = nameof(IApplication.Publish);
        if (string.IsNullOrEmpty(objectId))
        {
            return Task.FromException(new OneNoteException(
                ErrorCode.InvalidArgument,
                $"{method} wasn't sent: no object ID was given.",
                retryable: false));
        }

        // AD-11: only the formats Core names reach COM; any other value (pfOneNote2007's 8 included, which ends
        // ONENOTE.EXE) is refused here.
        if (ToPublishFormat(format) is not { } publishFormat)
        {
            return Task.FromException(new OneNoteException(
                ErrorCode.NotSupported,
                string.Create(CultureInfo.InvariantCulture, $"{method} wasn't sent: export format {(int)format} isn't supported."),
                retryable: false));
        }

        if (LocalFileProblem(targetPath) is { } problem)
        {
            return Task.FromException(new OneNoteException(
                ErrorCode.InvalidArgument,
                $"{method} wasn't sent: the target file {problem}.",
                retryable: false));
        }

        // AD-3: Publish has its own timeout (timeouts.publishSeconds); a write, so never retried after a disconnect.
        return RunAsync(
            method,
            app =>
            {
                app.Publish(objectId, targetPath, publishFormat, string.Empty);
                return true;
            },
            CallKind.Write,
            ct,
            publish: true);
    }

    /// <summary>Com's format for Core's <paramref name="format"/>, or null for a value that isn't a member (AD-5, AD-11).</summary>
    private static PublishFormat? ToPublishFormat(ExportFormat format) => format switch
    {
        ExportFormat.OneNote => PublishFormat.pfOneNote,
        ExportFormat.Pdf => PublishFormat.pfPDF,
        ExportFormat.Xps => PublishFormat.pfXPS,
        ExportFormat.Word => PublishFormat.pfWord,
        ExportFormat.Mhtml => PublishFormat.pfMHTML,
        ExportFormat.Html => PublishFormat.pfHTML,
        ExportFormat.Emf => PublishFormat.pfEMF,
        ExportFormat.OneNotePackage => PublishFormat.pfOneNotePackage,
        _ => null,
    };

    /// <summary>Why <paramref name="path"/> can't be a <c>Publish</c> target, or null: it must be a fully qualified local file path (<c>C:\…</c>), not UNC, a device path or a URL.</summary>
    private static string? LocalFileProblem(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "is empty";
        }

        var slashed = path.Replace('/', '\\');
        return slashed.StartsWith(@"\\", StringComparison.Ordinal) || slashed.StartsWith(@"\??\", StringComparison.Ordinal)
            || !System.IO.Path.IsPathFullyQualified(slashed) || slashed.Length < 4 || !char.IsAsciiLetter(slashed[0]) || slashed[1] != ':'
            || slashed.EndsWith('\\')
            ? "isn't a fully qualified local file path"
            : null;
    }
}
