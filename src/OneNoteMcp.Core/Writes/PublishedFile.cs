namespace OneNoteMcp.Core.Writes;

/// <summary>Why a file <c>Publish</c> wrote didn't count as landed within its limit (<see cref="PublishedFile.WaitAsync"/>).</summary>
public enum LandingFailure
{
    /// <summary>No file appeared at the path.</summary>
    NeverAppeared,

    /// <summary>The file appeared but stayed empty.</summary>
    StayedEmpty,

    /// <summary>The file's size was still changing between polls.</summary>
    StillGrowing,

    /// <summary>The file's size was stable, but another handle (OneNote's writer included) still held it.</summary>
    StillOpen,
}

/// <summary>
/// The one landing wait for a file <c>Publish</c> wrote (AD-7: "done means on disk"). OneNote can return from
/// <c>Publish</c> before the file is there (a package landed 250–530 ms later, spike 3.1 X2) or while it is still being
/// written. A file counts as landed only once it exists, isn't empty, its size is unchanged across two polls
/// <see cref="DefaultPollInterval"/> apart, and it opens with <see cref="FileShare.None"/> (no other handle holds it; 5.6
/// fresh review). Story 5.6's section backup (<see cref="SectionBackup"/>) and story 3.3's <c>publish</c> share it; each
/// maps a <see cref="LandingFailure"/> to its own error code.
/// </summary>
public static class PublishedFile
{
    /// <summary>The default gap between the polls that check a published file's size.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Polls <paramref name="path"/> until it has landed (null) or <paramref name="limit"/> has passed since
    /// <paramref name="started"/> (a timestamp on <paramref name="clock"/>), and then returns why it hadn't, from the last
    /// poll. A cancel or deadline on <paramref name="ct"/> stays an <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<LandingFailure?> WaitAsync(string path, long started, TimeSpan limit, TimeSpan pollInterval, TimeProvider clock, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(clock);
        long? last = null;
        var stillOpen = false;
        while (true)
        {
            var size = Size(path);
            if (size is > 0 && size == last)
            {
                // A stable size alone isn't proof that OneNote has finished writing; nothing else may hold it open.
                if (OpensExclusively(path))
                {
                    return null;
                }

                stillOpen = true;
            }
            else
            {
                stillOpen = false;
            }

            if (clock.GetElapsedTime(started) >= limit)
            {
                return size switch
                {
                    null => LandingFailure.NeverAppeared,
                    0 => LandingFailure.StayedEmpty,
                    _ when stillOpen => LandingFailure.StillOpen,
                    _ => LandingFailure.StillGrowing,
                };
            }

            last = size;
            await Task.Delay(pollInterval, clock, ct).ConfigureAwait(false);
        }
    }

    /// <summary>How a message says <paramref name="failure"/>, after "…failed:" or "…didn't land:"; never a path.</summary>
    public static string Describe(LandingFailure failure) => failure switch
    {
        LandingFailure.NeverAppeared => "its file never appeared",
        LandingFailure.StayedEmpty => "its file stayed empty",
        LandingFailure.StillOpen => "its file was still open for writing",
        LandingFailure.StillGrowing => "its file was still growing",
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "Unknown landing failure."),
    };

    /// <summary>The file's size, or null when it isn't there or can't be read.</summary>
    private static long? Size(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether the file opens with no sharing at all (<see cref="FileShare.None"/>): no other handle holds it.</summary>
    private static bool OpensExclusively(string path)
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
