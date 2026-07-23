using System.Runtime.InteropServices;

namespace ClipSync;

// Écoute native des changements de presse-papiers via AddClipboardFormatListener
// (pas de polling). Émet ClipboardChanged sur le thread UI (WndProc).
public sealed class ClipboardWatcher : NativeWindow, IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    public event Action? ClipboardChanged;

    public ClipboardWatcher()
    {
        CreateHandle(new CreateParams());
        AddClipboardFormatListener(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE)
            ClipboardChanged?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(Handle);
            DestroyHandle();
        }
    }
}
