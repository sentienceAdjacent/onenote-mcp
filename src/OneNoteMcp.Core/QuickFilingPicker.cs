namespace OneNoteMcp.Core;

/// <summary>
/// What the user did in OneNote's QuickFiling picker (story 4.9; spike 4.13 F5), as its callback read it:
/// <see cref="SelectedItem"/> is the chosen ID, empty when nothing was chosen, and <see cref="PressedButton"/> the button's
/// index (0, the one added button) or <see cref="NoButton"/> for Cancel or Esc.
/// </summary>
public sealed record QuickFilingAnswer(string SelectedItem, uint PressedButton)
{
    /// <summary><c>PressedButton</c> for Cancel and Esc (spike 4.13 F5).</summary>
    public const uint NoButton = 0xFFFFFFFF;

    /// <summary>True when the user chose something: <see cref="SelectedItem"/> isn't empty.</summary>
    public bool Picked => !string.IsNullOrEmpty(SelectedItem);
}

/// <summary>
/// A QuickFiling picker the gateway showed (story 4.9; AD-3, AD-7 <c>AppState</c> <c>quick_filing</c>). <see cref="Answer"/>
/// completes when OneNote calls back, on whatever thread it uses, with the user's answer, or fails with the
/// <see cref="OneNoteException"/> a getter in the callback raised; it never completes on the caller's behalf, since nothing
/// on our side closes the picker (spike 4.13 F6-F8). While the gateway holds the picker, from just before <c>Run</c> until
/// the answer arrives or <see cref="Release"/>, whichever is first, AD-3's idle release keeps the COM reference.
/// <see cref="Release"/> lets go: it is idempotent, and after it an answer that still arrives is ignored by the gateway
/// (the caller has stopped waiting). The caller always calls it, once its wait ends.
/// </summary>
public sealed class QuickFilingPicker
{
    private readonly Action? onRelease;
    private int released;

    /// <param name="answer">Completes with the user's answer.</param>
    /// <param name="onRelease">The gateway's let-go, run once, on the first <see cref="Release"/>.</param>
    public QuickFilingPicker(Task<QuickFilingAnswer> answer, Action? onRelease = null)
    {
        ArgumentNullException.ThrowIfNull(answer);
        Answer = answer;
        this.onRelease = onRelease;
    }

    /// <summary>The user's answer, when OneNote calls back.</summary>
    public Task<QuickFilingAnswer> Answer { get; }

    /// <summary>True once <see cref="Release"/> has run.</summary>
    public bool Released => Volatile.Read(ref released) != 0;

    /// <summary>Lets go of the picker. Idempotent; never throws.</summary>
    public void Release()
    {
        if (Interlocked.Exchange(ref released, 1) == 0)
        {
            onRelease?.Invoke();
        }
    }
}
