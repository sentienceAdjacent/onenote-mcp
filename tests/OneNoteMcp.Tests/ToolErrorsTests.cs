using OneNoteMcp.Core;
using OneNoteMcp.Core.Results;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>The AD-9 retryable and userAction table, and the outcome-unknown rule.</summary>
public sealed class ToolErrorsTests
{
    private static readonly ErrorCode[] AlwaysRetryable =
    [
        ErrorCode.OneNoteNotRunning, ErrorCode.ComBusy, ErrorCode.ModalDialogOpen, ErrorCode.SectionLocked,
        ErrorCode.WriteNotGranted, ErrorCode.PermanentDeleteNotGranted, ErrorCode.ReadNotGranted, ErrorCode.SettingsInvalid,
    ];

    private static readonly ErrorCode[] RetryableForReadOnly = [ErrorCode.ComUnavailable, ErrorCode.Timeout, ErrorCode.Cancelled];

    public static TheoryData<ErrorCode, OperationClass> AllCodesAndClasses()
    {
        var data = new TheoryData<ErrorCode, OperationClass>();
        foreach (var code in Enum.GetValues<ErrorCode>())
        {
            foreach (var cls in Enum.GetValues<OperationClass>())
            {
                data.Add(code, cls);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllCodesAndClasses))]
    public void Retryable_FollowsThePerCodeTable(ErrorCode code, OperationClass cls)
    {
        var expected = AlwaysRetryable.Contains(code) || (RetryableForReadOnly.Contains(code) && cls == OperationClass.Read);

        var error = ToolErrors.From(new OneNoteException(code, "m"), cls);

        Assert.Equal(expected, error.Retryable);
        Assert.Null(error.OutcomeUnknown);
    }

    [Theory]
    [MemberData(nameof(AllCodesAndClasses))]
    public void OutcomeUnknown_ForcesNotRetryable_AndTheFixedUserAction(ErrorCode code, OperationClass cls)
    {
        var error = ToolErrors.From(new OneNoteException(code, "m", outcomeUnknown: true), cls);

        Assert.False(error.Retryable);
        Assert.True(error.OutcomeUnknown);
        Assert.Equal("Re-read, then retry if the change is absent.", error.UserAction);
    }

    [Fact]
    public void AnExceptionsOwnRetryable_OverridesTheTable_ForThatErrorOnly()
    {
        // Bug 1.22: a stored result whose grant ended stays refused, so its ReadNotGranted isn't retryable.
        var lapsed = ToolErrors.From(new OneNoteException(ErrorCode.ReadNotGranted, "m", retryable: false), OperationClass.Read);
        var other = ToolErrors.From(new OneNoteException(ErrorCode.ReadNotGranted, "m"), OperationClass.Read);
        var forcedOn = ToolErrors.From(new OneNoteException(ErrorCode.Internal, "m", retryable: true), OperationClass.Read);
        var unknown = ToolErrors.From(new OneNoteException(ErrorCode.Timeout, "m", outcomeUnknown: true, retryable: true), OperationClass.PageWrite);

        Assert.False(lapsed.Retryable);
        Assert.True(other.Retryable);
        Assert.True(forcedOn.Retryable);
        Assert.False(unknown.Retryable);
    }

    [Fact]
    public void EveryCode_HasItsOwnShortUserAction()
    {
        var actions = Enum.GetValues<ErrorCode>().Select(ToolErrors.UserAction).ToList();

        Assert.All(actions, a => Assert.False(string.IsNullOrWhiteSpace(a)));
        Assert.Equal(actions.Count, actions.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("Close the open dialog in OneNote, then retry.", ToolErrors.UserAction(ErrorCode.ModalDialogOpen));
        Assert.Equal("Report this error, with its HRESULT if it has one.", ToolErrors.UserAction(ErrorCode.Internal));
    }

    [Fact]
    public void SectionLocked_NamesOneNotesAddInAccessOption()
    {
        // Story 1.13: with this option off, an unlocked protected section still looks locked to COM.
        var action = ToolErrors.UserAction(ErrorCode.SectionLocked);
        Assert.StartsWith("Unlock the section in OneNote, then retry. ", action, StringComparison.Ordinal);
        Assert.Contains("\"Enable add-in programs to access password protected sections when they are unlocked\"", action, StringComparison.Ordinal);
        Assert.Contains("File > Options > Advanced > Passwords", action, StringComparison.Ordinal);
    }

    [Fact]
    public void HResult_IsFormattedAsUpperHex_AndMessageIsKept()
    {
        var error = ToolErrors.From(
            new OneNoteException(ErrorCode.ObjectNotFound, "GetHierarchy failed: hrObjectDoesNotExist (0x80042014).", unchecked((int)0x80042014)),
            OperationClass.Read);

        Assert.Equal(ErrorCode.ObjectNotFound, error.Code);
        Assert.Equal("0x80042014", error.HResult);
        Assert.False(error.Retryable);
        Assert.Equal("GetHierarchy failed: hrObjectDoesNotExist (0x80042014).", error.Message);
    }

    [Fact]
    public void NoHResult_LeavesItNull()
    {
        var error = ToolErrors.From(new OneNoteException(ErrorCode.OneNoteNotRunning, "m"), OperationClass.Read);

        Assert.Null(error.HResult);
        Assert.True(error.Retryable);
        Assert.Equal("Start OneNote desktop, then retry.", error.UserAction);
    }
}
