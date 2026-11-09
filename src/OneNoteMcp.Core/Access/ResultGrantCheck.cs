using OneNoteMcp.Core.LargeResults;
using OneNoteMcp.Core.Settings;

namespace OneNoteMcp.Core.Access;

/// <summary>
/// <c>read_result</c>'s AD-21 check (AD-20): a part of a stored result that lies inside a protected section is served only
/// while the grant that allowed the original call holds. <c>once</c> holds for as long as the entry lives; <c>hours</c>
/// until its <c>expiresAt</c>, and <c>untilRevoked</c> until revoked, both re-read from <c>settings.json</c>. It never asks.
/// </summary>
public sealed class ResultGrantCheck(Func<SettingsSnapshot> settings, TimeProvider clock)
{
    /// <summary>The check for results stored by use cases built by hand: no stored grants.</summary>
    public static ResultGrantCheck WithoutStoredGrants { get; } = new(ReadAccess.NoSettings, TimeProvider.System);

    /// <summary>
    /// Throws <see cref="ErrorCode.ReadNotGranted"/> when the response spanning [<paramref name="start"/>,
    /// <paramref name="end"/>) of <paramref name="entry"/>'s body overlaps a protected section whose grant has lapsed.
    /// </summary>
    public void Check(StoredResult entry, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Grants.Grants.Count == 0)
        {
            return;
        }

        SettingsSnapshot? snapshot = null;
        var now = clock.GetUtcNow();
        foreach (var grant in entry.Grants.Grants)
        {
            if (!Overlaps(entry, grant, start, end))
            {
                continue;
            }

            snapshot ??= settings();
            if (!Holds(grant, snapshot, now))
            {
                // An expired hours grant stays expired for this stored result, even after a new grant, so only the original
                // call can succeed: not retryable. A revoked grant (untilRevoked, or hours before its end) holds again once
                // the user grants the same section again, so it keeps the table's flag (bug 1.22).
                throw new OneNoteException(
                    ErrorCode.ReadNotGranted,
                    $"This part of result {entry.ResultId} may hold pages of password-protected section {grant.SectionId}, and the read grant that allowed the original call has ended.",
                    userAction: ReadAccess.LapsedUserAction,
                    retryable: grant.Lifetime == ReadGrantLifetime.Hours && grant.ExpiresAt is { } expiry && now >= expiry ? false : null);
            }
        }
    }

    private static bool Holds(ResultGrant grant, SettingsSnapshot snapshot, DateTimeOffset now) => grant.Lifetime switch
    {
        // The entry itself expires ttlMinutes after the original call (AD-20).
        ReadGrantLifetime.Once => true,
        ReadGrantLifetime.Hours => grant.ExpiresAt is { } end && now < end && ReadAccess.Stored(snapshot, grant.SectionKey, now) is not null,
        ReadGrantLifetime.UntilRevoked => ReadAccess.Stored(snapshot, grant.SectionKey, now) is not null,
        _ => false,
    };

    /// <summary>
    /// True when the part [<paramref name="start"/>, <paramref name="end"/>) could hold the grant's section's pages.
    /// Fails closed (AD-21): when the section's place in the stored body can't be told (a body stored without an index,
    /// only <c>/</c>, or no element with the section's ID), every part counts as holding them (bug 1.22).
    /// </summary>
    private static bool Overlaps(StoredResult entry, ResultGrant grant, int start, int end)
    {
        if (grant.WholeResult || entry.Tree.Root.Element is null)
        {
            return true;
        }

        return entry.Tree.Find(grant.SectionId) is not { } section || (start < section.End && section.Start < end);
    }
}
