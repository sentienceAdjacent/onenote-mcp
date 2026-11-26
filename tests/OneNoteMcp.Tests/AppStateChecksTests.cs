using OneNoteMcp.Core;
using OneNoteMcp.Core.AppState;
using OneNoteMcp.Core.UseCases;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// The round-3 refactor sweep's shared <c>AppState</c> checks (<see cref="AppStateChecks"/>), against the literals the
/// <c>sync_hierarchy</c> and navigate use cases carried before the sweep. The tools' own rows stay in their tool tests.
/// </summary>
public sealed class AppStateChecksTests
{
    private const int ObjectDoesNotExist = unchecked((int)0x80042014);

    [Fact]
    public void RequireHierarchyId_Empty_IsInvalidArgument_WithTodaysText()
    {
        var ex = Assert.Throws<OneNoteException>(() => AppStateChecks.RequireHierarchyId(string.Empty));

        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
        Assert.Equal("hierarchyId is empty: pass the ID of a notebook, section group, section or page.", ex.Message);
        Assert.Null(ex.ComHResult);
        Assert.Null(ex.UserAction);
        Assert.Null(ex.Retryable);
        Assert.False(ex.OutcomeUnknown);
    }

    [Theory]
    [InlineData("{4B0B0000-0000-4000-8000-000000000001}{1}{B0}")]
    [InlineData(" ")]
    public void RequireHierarchyId_AnyNonEmptyId_Passes(string id) => AppStateChecks.RequireHierarchyId(id);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithUserAction_ObjectDoesNotExist_KeepsCodeMessageHResultAndOutcome_AddsTheUserAction(bool outcomeUnknown)
    {
        var original = new OneNoteException(ErrorCode.ObjectNotFound, "NavigateTo failed: hrObjectDoesNotExist (0x80042014).", ObjectDoesNotExist, outcomeUnknown: outcomeUnknown);

        foreach (var userAction in new[] { NavigateToUseCase.ObjectNotFoundUserAction, NavigateToUrlUseCase.ObjectNotFoundUserAction })
        {
            var stale = AppStateChecks.WithUserAction(original, userAction);

            Assert.NotNull(stale);
            Assert.Equal(ErrorCode.ObjectNotFound, stale.Code);
            Assert.Equal(original.Message, stale.Message);
            Assert.Equal(ObjectDoesNotExist, stale.ComHResult);
            Assert.Same(original, stale.InnerException);
            Assert.Equal(outcomeUnknown, stale.OutcomeUnknown);
            Assert.Equal(userAction, stale.UserAction);
            Assert.Null(stale.Retryable);
        }
    }

    [Fact]
    public void TheNavigateUserActions_AreTodaysLiterals()
    {
        Assert.Equal("Re-read the hierarchy and use a current ID.", NavigateToUseCase.ObjectNotFoundUserAction);
        Assert.Equal(
            "The link may name something that no longer exists. If navigate_to_url was just called, wait a second and try once more; otherwise ask the user whether OneNote showed a message.",
            NavigateToUrlUseCase.ObjectNotFoundUserAction);
    }

    [Theory]
    [InlineData(ErrorCode.ObjectNotFound, null)] // ObjectNotFound without the HRESULT
    [InlineData(ErrorCode.ObjectNotFound, unchecked((int)0x8004200F))] // another HRESULT
    [InlineData(ErrorCode.Internal, unchecked((int)0x80042014))] // the HRESULT under another code
    [InlineData(ErrorCode.InvalidArgument, null)]
    public void WithUserAction_AnyOtherFailure_IsNull(ErrorCode code, int? hresult)
    {
        var other = new OneNoteException(code, "failed", hresult);

        Assert.Null(AppStateChecks.WithUserAction(other, NavigateToUseCase.ObjectNotFoundUserAction));
    }
}
