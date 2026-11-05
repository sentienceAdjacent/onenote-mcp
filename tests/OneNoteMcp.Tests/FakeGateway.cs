using System.Globalization;
using OneNoteMcp.Core;

namespace OneNoteMcp.Tests;

/// <summary>
/// Fixture-backed <see cref="IOneNoteGateway"/> (AD-16). Every port method answers from <c>Fixtures/manifest.json</c>
/// with the captured file's text unchanged, or throws the error OneNote gave for that call. A call with no fixture
/// throws <see cref="ErrorCode.ObjectNotFound"/>, as OneNote does for an unknown ID. Like the real adapter, it throws
/// only <see cref="OneNoteException"/>.
/// </summary>
/// <remarks>
/// The story-3 scripting stays: <see cref="HierarchyXml"/>, when set, answers every <c>GetHierarchy</c>;
/// <see cref="Failure"/>, when set, is thrown by every method; <see cref="Calls"/> records <c>GetHierarchy</c> calls.
/// </remarks>
internal sealed class FakeGateway : IOneNoteGateway
{
    /// <summary>The committed fixtures: <c>tests/OneNoteMcp.Tests/Fixtures</c> in the source tree.</summary>
    public static readonly string DefaultDirectory = RepoPaths.Combine("tests", "OneNoteMcp.Tests", "Fixtures");

    private static readonly Lazy<Fixtures> Shared = new(() => Fixtures.Load(DefaultDirectory));

    private readonly Fixtures _fixtures;

    public FakeGateway()
        : this(Shared.Value)
    {
    }

    private FakeGateway(Fixtures fixtures) => _fixtures = fixtures;

    /// <summary>The committed manifest, for tests that look up fixture IDs.</summary>
    public static FixtureManifest Manifest => Shared.Value.Manifest;

    /// <summary>When set, every <c>GetHierarchy</c> returns it, whatever the arguments.</summary>
    public string? HierarchyXml { get; set; }

    public OneNoteException? Failure { get; set; }

    public List<(string StartNodeId, HierarchyScope Scope)> Calls { get; } = [];

    /// <summary>The text of a fixture file, relative to <c>Fixtures/</c>.</summary>
    public static string ReadFixture(string relativePath) => Shared.Value.Read(relativePath);

    public Task<string> GetHierarchyAsync(string startNodeId, HierarchyScope scope, CancellationToken ct)
    {
        Calls.Add((startNodeId, scope));
        return HierarchyXml is not null && Failure is null
            ? Task.FromResult(HierarchyXml)
            : Serve(nameof(IOneNoteGateway.GetHierarchyAsync), startNodeId, scope.ToString());
    }

    public Task<string> GetHierarchyParentAsync(string objectId, CancellationToken ct) =>
        Serve(nameof(IOneNoteGateway.GetHierarchyParentAsync), objectId);

    public Task<string> GetPageContentAsync(string pageId, PageInfo pageInfo, CancellationToken ct) =>
        Serve(nameof(IOneNoteGateway.GetPageContentAsync), pageId, pageInfo.ToString());

    public Task<string> GetBinaryPageContentAsync(string pageId, string callbackId, CancellationToken ct) =>
        Serve(nameof(IOneNoteGateway.GetBinaryPageContentAsync), pageId, callbackId);

    public Task<string> FindPagesAsync(string startNodeId, string query, bool includeUnindexed, CancellationToken ct) =>
        Serve(nameof(IOneNoteGateway.FindPagesAsync), startNodeId, query, Bool(includeUnindexed));

    public Task<string> FindMetaAsync(string startNodeId, string name, bool includeUnindexed, CancellationToken ct) =>
        Serve(nameof(IOneNoteGateway.FindMetaAsync), startNodeId, name, Bool(includeUnindexed));

    public Task<string> GetHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) =>
        Serve(nameof(IOneNoteGateway.GetHyperlinkToObjectAsync), hierarchyId, pageContentObjectId);

    public Task<string> GetWebHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) =>
        Serve(nameof(IOneNoteGateway.GetWebHyperlinkToObjectAsync), hierarchyId, pageContentObjectId);

    public Task<string> GetSpecialLocationAsync(SpecialLocation location, CancellationToken ct) =>
        Serve(nameof(IOneNoteGateway.GetSpecialLocationAsync), location.ToString());

    private static string Bool(bool value) => value ? "true" : "false";

    private Task<string> Serve(string asyncMethod, params string?[] args)
    {
        if (Failure is not null)
        {
            return Task.FromException<string>(Failure);
        }

        var method = asyncMethod[..^"Async".Length];
        if (!_fixtures.Entries.TryGetValue(FixtureManifest.Key(method, [.. args.Select(a => a ?? string.Empty)]), out var entry))
        {
            // OneNote's own words for an unknown ID (hrObjectDoesNotExist); no content in the message (AD-9).
            return Task.FromException<string>(new OneNoteException(
                ErrorCode.ObjectNotFound,
                $"{method} failed: hrObjectDoesNotExist (0x80042014).",
                unchecked((int)0x80042014)));
        }

        if (entry.Error is { } error)
        {
            return Task.FromException<string>(new OneNoteException(
                Enum.Parse<ErrorCode>(error.Code),
                error.Message,
                error.HResult is null ? null : unchecked((int)uint.Parse(error.HResult.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture))));
        }

        return Task.FromResult(_fixtures.Read(entry.File!));
    }

    private sealed class Fixtures
    {
        private readonly string _directory;
        private readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);

        private Fixtures(string directory, FixtureManifest manifest)
        {
            _directory = directory;
            Manifest = manifest;
            Entries = manifest.Entries.ToDictionary(e => e.Key, StringComparer.Ordinal);
        }

        public FixtureManifest Manifest { get; }

        public Dictionary<string, FixtureEntry> Entries { get; }

        public static Fixtures Load(string directory) =>
            new(directory, FixtureManifest.Load(Path.Combine(directory, FixtureManifest.FileName)));

        public string Read(string relativePath)
        {
            lock (_texts)
            {
                if (!_texts.TryGetValue(relativePath, out var text))
                {
                    // UTF-8 without a BOM, as the capture writes it: the bytes on disk are the string served.
                    text = File.ReadAllText(Path.Combine(_directory, relativePath), new System.Text.UTF8Encoding(false));
                    _texts.Add(relativePath, text);
                }

                return text;
            }
        }
    }
}
