using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using OneNoteMcp.Com;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Results;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>AD-9: HRESULT names come from <c>OneNoteProbe.psm1</c>, never from memory, and each maps to the planned code.</summary>
public sealed partial class HResultMapTests
{
    // Every HRESULT that is not Internal. Anything else in the psm1 table must map to Internal.
    private static readonly Dictionary<uint, ErrorCode> ExpectedNonInternal = new()
    {
        [0x80080005] = ErrorCode.ComUnavailable,
        [0x800706BA] = ErrorCode.ComUnavailable,
        [0x80010108] = ErrorCode.ComUnavailable,
        [0x80010001] = ErrorCode.ComBusy,
        [0x8001010A] = ErrorCode.ComBusy,
        [0x8004201D] = ErrorCode.ComBusy,
        [0x80042030] = ErrorCode.ModalDialogOpen,
        [0x8004201B] = ErrorCode.SectionLocked,
        [0x80042010] = ErrorCode.StaleWrite,
        [0x80042023] = ErrorCode.Timeout,
        [0x80042004] = ErrorCode.ObjectNotFound,
        [0x80042005] = ErrorCode.ObjectNotFound,
        [0x8004200E] = ErrorCode.ObjectNotFound,
        [0x80042011] = ErrorCode.ObjectNotFound,
        [0x80042012] = ErrorCode.ObjectNotFound,
        [0x80042014] = ErrorCode.ObjectNotFound,
        [0x80042015] = ErrorCode.ObjectNotFound,
        [0x80042000] = ErrorCode.InvalidXml,
        [0x80042001] = ErrorCode.InvalidXml,
        [0x80042020] = ErrorCode.InvalidXml,
        [0x80042019] = ErrorCode.InvalidQuery,
        [0x8004201A] = ErrorCode.FileExists,
    };

    private static readonly Lazy<Dictionary<int, string>> PsmTable = new(ReadPsmTable);

    public static TheoryData<string, string> PsmEntries()
    {
        var data = new TheoryData<string, string>();
        foreach (var (hr, name) in PsmTable.Value.OrderBy(e => unchecked((uint)e.Key)))
        {
            data.Add($"0x{hr:X8}", name);
        }

        return data;
    }

    [Fact]
    public void PsmTable_HasTheExpectedSize()
    {
        // 49 OneNote codes (the docs skip 0x80042021) plus the generic COM/RPC codes.
        Assert.Equal(49, PsmTable.Value.Keys.Count(k => (unchecked((uint)k) & 0xFFFFF000) == 0x80042000));
        Assert.Equal(PsmTable.Value.Count, HResultMap.Names.Count);
    }

    [Fact]
    public void CSharpNames_EqualThePsmTableExactly() =>
        Assert.Equal(
            PsmTable.Value.OrderBy(e => e.Key).Select(e => $"0x{e.Key:X8}={e.Value}"),
            HResultMap.Names.OrderBy(e => e.Key).Select(e => $"0x{e.Key:X8}={e.Value}"));

    [Theory]
    [MemberData(nameof(PsmEntries))]
    public void EveryPsmEntry_HasItsNameAndPlannedCode(string hex, string name)
    {
        var hr = unchecked((int)uint.Parse(hex.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        var expected = ExpectedNonInternal.GetValueOrDefault(unchecked((uint)hr), ErrorCode.Internal);

        Assert.Equal(name, HResultMap.Name(hr));
        Assert.Equal(expected, HResultMap.ToErrorCode(hr));
    }

    [Fact]
    public void EveryNonInternalMapping_IsInThePsmTable() =>
        Assert.All(ExpectedNonInternal.Keys, hr => Assert.True(PsmTable.Value.ContainsKey(unchecked((int)hr)), $"0x{hr:X8}"));

    [Fact]
    public void UnknownHResult_IsInternal_WithHexOnlyMessage()
    {
        const int unknown = unchecked((int)0x80041234);

        Assert.Null(HResultMap.Name(unknown));
        Assert.Equal(ErrorCode.Internal, HResultMap.ToErrorCode(unknown));
        Assert.Equal("GetHierarchy failed: 0x80041234.", HResultMap.FailureMessage("GetHierarchy", unknown));
    }

    [Fact]
    public void KnownHResult_MessageNamesIt() =>
        Assert.Equal(
            "GetHierarchy failed: hrObjectDoesNotExist (0x80042014).",
            HResultMap.FailureMessage("GetHierarchy", unchecked((int)0x80042014)));

    // --- Triage (story 2.7) --------------------------------------------------------------------------------------

    [Fact]
    public void EveryNamedHResult_HasAnExplicitTriageRow()
    {
        Assert.All(HResultMap.Names.Keys, hr => Assert.True(HResultMap.Triaged.ContainsKey(hr), $"0x{hr:X8} {HResultMap.Name(hr)}"));
        Assert.Equal(HResultMap.Names.Count, HResultMap.Triaged.Count);
    }

    [Fact]
    public void EveryOneNoteHResult_IsTriaged_NotLeftToTheDefault() =>
        Assert.All(
            PsmTable.Value.Keys.Where(k => (unchecked((uint)k) & 0xFFFFF000) == 0x80042000),
            hr => Assert.True(HResultMap.Triaged.ContainsKey(hr), $"0x{hr:X8}"));

    // The Design Notes rows of story 2.7: code, whether a specific userAction is set, and the retryable override.
    public static TheoryData<uint, bool?> TriagedInternal => new()
    {
        { 0x8004200Bu, true },
        { 0x8004200Cu, true },
        { 0x80042022u, null },
        { 0x80042024u, true },
        { 0x8004202Eu, null },
        { 0x8004201Cu, null },
        { 0x80042017u, null },
        { 0x80042002u, null },
        { 0x80042003u, true },
        { 0x8004201Eu, true },
        { 0x80042007u, null },
        { 0x80042008u, null },
        { 0x80042009u, null },
        { 0x8004200Du, null },
        { 0x80042016u, null },
        { 0x8004200Au, null },
        { 0x8004201Fu, null },
        { 0x8004202Du, null },
        { 0x80042031u, null },
    };

    [Theory]
    [MemberData(nameof(TriagedInternal))]
    public void DesignNotesRow_IsInternal_WithItsOwnUserAction_AndRetryFlag(uint hresult, bool? retryable)
    {
        var hr = unchecked((int)hresult);

        Assert.Equal(ErrorCode.Internal, HResultMap.ToErrorCode(hr));
        Assert.False(string.IsNullOrWhiteSpace(HResultMap.UserAction(hr)));
        Assert.Equal(retryable, HResultMap.Retryable(hr));
    }

    [Theory]
    [InlineData(0x80042013u)]
    [InlineData(0x80042025u)]
    [InlineData(0x8004202Fu)]
    [InlineData(0x80042006u)]
    [InlineData(0x8004200Fu)]
    [InlineData(0x800706BEu)]
    public void UnreachedOrDeliberateInternalRows_KeepTheDefaults(uint hresult)
    {
        var hr = unchecked((int)hresult);

        Assert.Equal(ErrorCode.Internal, HResultMap.ToErrorCode(hr));
        Assert.Null(HResultMap.UserAction(hr));
        Assert.Null(HResultMap.Retryable(hr));
    }

    [Fact]
    public void ServerExecFailure_KeepsItsRestartUserAction() =>
        Assert.Equal(HResultMap.ServerExecFailureUserAction, HResultMap.UserAction(unchecked((int)0x80080005)));

    [Fact]
    public void ToException_CarriesTheRowsUserActionAndRetryFlag()
    {
        var ex = HResultMap.ToException("UpdatePageContent", new COMException("read-only", unchecked((int)0x8004200C)));

        Assert.Equal(ErrorCode.Internal, ex.Code);
        Assert.Equal(HResultMap.UserAction(unchecked((int)0x8004200C)), ex.UserAction);
        Assert.True(ex.Retryable);
        Assert.Equal("UpdatePageContent failed: hrPageReadOnly (0x8004200C).", ex.Message);

        var plain = HResultMap.ToException("GetHierarchy", new COMException("gone", unchecked((int)0x80042014)));
        Assert.Null(plain.UserAction);
        Assert.Null(plain.Retryable);
    }

    [Theory]
    [InlineData(OperationClass.Read)]
    [InlineData(OperationClass.PageWrite)]
    public void ARetryableRow_ReadsRetryableOnAReadAndAWrite_ButNotWhenTheOutcomeIsUnknown(OperationClass operationClass)
    {
        var ex = HResultMap.ToException("UpdatePageContent", new COMException("recording", unchecked((int)0x80042024)));

        var error = ToolErrors.From(ex, operationClass);
        Assert.True(error.Retryable);
        Assert.Equal(ex.UserAction, error.UserAction);

        var unknown = new OneNoteException(ex.Code, ex.Message, ex.ComHResult, ex, outcomeUnknown: true, userAction: ex.UserAction, retryable: ex.Retryable);
        var unknownError = ToolErrors.From(unknown, operationClass);
        Assert.False(unknownError.Retryable);
        Assert.Equal(ToolErrors.OutcomeUnknownUserAction, unknownError.UserAction);
    }

    private static Dictionary<int, string> ReadPsmTable()
    {
        var text = File.ReadAllText(RepoPaths.Combine("spikes", "com-capabilities", "OneNoteProbe.psm1"));
        var start = text.IndexOf("$script:HResultNames = @{", StringComparison.Ordinal);
        Assert.True(start >= 0, "HResultNames table not found in OneNoteProbe.psm1.");
        var end = text.IndexOf("\n}", start, StringComparison.Ordinal);
        var block = text[start..end];

        var table = new Dictionary<int, string>();
        foreach (Match m in EntryRegex().Matches(block))
        {
            var hr = unchecked((int)uint.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            Assert.True(table.TryAdd(hr, m.Groups[2].Value), $"Duplicate entry 0x{hr:X8} in the psm1 table.");
        }

        Assert.NotEmpty(table);
        return table;
    }

    [GeneratedRegex(@"'0x([0-9a-fA-F]{8})'\s*=\s*'([^']+)'")]
    private static partial Regex EntryRegex();
}
