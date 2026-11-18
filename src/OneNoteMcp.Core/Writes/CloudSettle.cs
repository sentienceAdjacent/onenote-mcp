namespace OneNoteMcp.Core.Writes;

/// <summary>
/// The settle a hierarchy write holds under the write lock when it touches a cloud notebook (AD-7 Moves, J5; 3.12 Q4),
/// shared by the section move (<see cref="MoveContainerWrite"/>), the restore (<see cref="RestoreFromRecycleBinWrite"/>)
/// and the merge (<see cref="MergeSectionsWrite"/>). Extracted unchanged from the first two (story 3.6).
/// </summary>
internal static class CloudSettle
{
    /// <summary>The settle after a write that touches a cloud notebook: 10 s (AD-7 Moves, J5).</summary>
    internal static readonly TimeSpan Time = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The settle before a failed cloud write's re-read (3.12 Q4): waits <see cref="Time"/> through
    /// <paramref name="settle"/> when <paramref name="touchesCloud"/>; a cancel or the deadline cuts it short silently,
    /// since the original failure is what the caller gets.
    /// </summary>
    internal static async Task QuietlyAsync(Func<TimeSpan, CancellationToken, Task> settle, bool touchesCloud, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settle);
        if (!touchesCloud)
        {
            return;
        }

        try
        {
            await settle(Time, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The original failure is what the caller gets; the settle only spaces the next write.
        }
    }
}
