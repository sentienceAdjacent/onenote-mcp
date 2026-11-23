using System.Runtime.InteropServices;
using OneNoteMcp.Com.Interop;
using OneNoteMcp.Core;

namespace OneNoteMcp.Com;

/// <summary>
/// The QuickFiling picker's callback (story 4.9): the one code that runs a OneNote COM call off the STA (spine AD-2). OneNote
/// calls <see cref="OnDialogClosed"/> on a thread of its choosing (an MTA thread in all nine of spike 4.13's runs, never the
/// STA that called <c>Run</c>), so it works on any thread. It only reads <c>SelectedItem</c> and <c>PressedButton</c> on the
/// dialog it is given, records them by completing <see cref="Answer"/> (whose continuations run asynchronously, so none runs
/// on OneNote's thread), and returns: OneNote's UI thread waits for it (OneMore row 26). It never throws into OneNote, never
/// blocks, never touches the STA's <c>IApplication</c> and queues no STA work. A getter that fails completes the answer with
/// its mapped error (AD-9). Only the first call counts.
/// <para>
/// Public, with an internal constructor (Phill, 2026-11-23, 4.9 plan OQ2 a): COM sees a managed object only through public
/// interfaces, and only <see cref="ComGateway"/> creates one.
/// </para>
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class QuickFilingCallback : IQuickFilingDialogCallback
{
    private readonly TaskCompletionSource<QuickFilingAnswer> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal QuickFilingCallback()
    {
    }

    /// <summary>Completes with the first answer OneNote gives.</summary>
    internal Task<QuickFilingAnswer> Answer => answer.Task;

    public void OnDialogClosed(object dialog)
    {
        try
        {
            if (dialog is null)
            {
                answer.TrySetException(new OneNoteException(ErrorCode.Internal, "OneNote closed the QuickFiling picker without passing it back, so its answer couldn't be read."));
                return;
            }

            var picker = (IQuickFilingDialog)dialog;
            var selected = picker.SelectedItem ?? string.Empty;
            var pressed = picker.PressedButton;
            answer.TrySetResult(new QuickFilingAnswer(selected, pressed));
        }
#pragma warning disable CA1031 // Never throw into OneNote: its UI thread is waiting on this call (OneMore row 26).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            try
            {
                answer.TrySetException(HResultMap.ToException("QuickFilingDialog.OnDialogClosed", ex));
            }
#pragma warning disable CA1031 // As above: the mapping itself must not throw into OneNote.
            catch (Exception)
#pragma warning restore CA1031
            {
                answer.TrySetException(new OneNoteException(ErrorCode.Internal, "The QuickFiling picker's answer couldn't be read."));
            }
        }
    }
}
