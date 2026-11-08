using System;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace GraphDBViewerWeb.Code;

///<summary>
///Text onto the clipboard, and what its button says about it: Copied for a moment, or the browser's refusal until the next
///press, so the page can say to copy by hand. A count of presses rather than a flag alone, so a second press inside the
///moment does not have its Copied taken back by the first one's timer. The Share dialog and the live link's card each
///had a copy of this until 2026-11-07, the card's letting a failure that was not the browser's escape and keeping a tick
///from an earlier press beside a refusal.
///</summary>
public sealed class ClipboardCopy
{
    ///<summary>How long a button says Copied.</summary>
    public static readonly TimeSpan Moment = TimeSpan.FromSeconds(2);

    private readonly TimeSpan moment;

    private int presses;

    public ClipboardCopy()
    {
        moment = Moment;
    }

    ///<summary>With a moment of its own, which a test keeps short.</summary>
    public ClipboardCopy(TimeSpan moment)
    {
        this.moment = moment;
    }

    ///<summary>The last press copied, and its moment has not passed.</summary>
    public bool Copied { get; private set; }

    ///<summary>The last press was not allowed the clipboard.</summary>
    public bool Refused { get; private set; }

    ///<summary>Neither said any more, as when what would be copied has changed.</summary>
    public void Reset()
    {
        Copied = false;
        Refused = false;
    }

    ///<summary>Copies <paramref name="text"/>, calling <paramref name="changed"/> when Copied is to be shown and when it is taken back.</summary>
    public async Task CopyAsync(IJSRuntime js, string text, Action changed)
    {
        int press = ++presses;

        try
        {
            await js.InvokeVoidAsync("navigator.clipboard.writeText", text);
            Copied = true;
            Refused = false;
        }
        catch
        {
            Copied = false;
            Refused = true;

            return;
        }

        changed();
        await Task.Delay(moment);

        if (press != presses)
            return;

        Copied = false;
        changed();
    }
}
