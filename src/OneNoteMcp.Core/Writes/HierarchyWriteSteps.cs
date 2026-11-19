using System.Globalization;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Writes;

/// <summary>
/// Steps the hierarchy writes share (story 3.11 refactor sweep): the AD-16 schema gate for the <c>UpdateHierarchy</c> XML
/// the server builds, a container's children read, the <c>InvalidArgument</c> refusal, and the moves' step-3 notebook
/// check. Each was copied unchanged from the writes that use it; their messages are the same as before, character for
/// character.
/// </summary>
internal static class HierarchyWriteSteps
{
    /// <summary>
    /// AD-16, the hard gate for <c>UpdateHierarchy</c>: throws <see cref="ErrorCode.Internal"/> when the 2013 schema refuses
    /// <paramref name="xml"/>. The XML is the server's own, so a failure is its bug, and nothing is sent.
    /// </summary>
    internal static void EnsureSchemaValid(string xml)
    {
        var problems = SchemaValidation.Problems(xml);
        if (problems.Count > 0)
        {
            throw new OneNoteException(
                ErrorCode.Internal,
                string.Create(CultureInfo.InvariantCulture, $"The server built UpdateHierarchy XML that the 2013 schema refuses ({problems.Count} problem(s), the first at {problems[0].DisplayName ?? "the document"}), so nothing was sent."));
        }
    }

    /// <summary>The children of <paramref name="id"/> (<c>GetHierarchy</c> scope <c>children</c>), read as a <see cref="HierarchyTarget"/>.</summary>
    internal static async Task<HierarchyTarget> ReadChildrenAsync(IOneNoteGateway gateway, string id, CancellationToken ct)
    {
        var xml = await gateway.GetHierarchyAsync(id, HierarchyScope.Children, ct).ConfigureAwait(false);
        return XmlReads.Readable(() => HierarchyTarget.Read(xml), "GetHierarchy");
    }

    /// <summary>An <see cref="ErrorCode.InvalidArgument"/> refusal, not retryable.</summary>
    internal static OneNoteException Invalid(string message, string userAction) =>
        new(ErrorCode.InvalidArgument, message, userAction: userAction, retryable: false);

    /// <summary>
    /// A move's step-3 compare (AD-7): <paramref name="path"/>'s notebook key must be the one step 1 granted, else
    /// <see cref="ErrorCode.WriteNotGranted"/> (retryable) with <paramref name="userAction"/>.
    /// </summary>
    internal static void CheckMoveNotebook(string what, string id, string? path, WriteTarget? granted, string userAction)
    {
        var key = string.IsNullOrEmpty(path) ? null : GrantKeys.NotebookKey(path);
        if (granted is null || key is null || !GrantKeys.Comparer.Equals(key, granted.NotebookKey))
        {
            throw new OneNoteException(
                ErrorCode.WriteNotGranted,
                $"{what} {id} is no longer in notebook {granted?.NotebookId ?? "(unknown)"}, whose write grant this call checked, so nothing was moved.",
                userAction: userAction,
                retryable: true);
        }
    }
}
