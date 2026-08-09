using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClaudeUsageOverlay;

/// <summary>
/// The small amount of Win32 needed to make a window behave like scenery: always on top,
/// never in the taskbar or Alt-Tab list, and optionally transparent to the mouse so it can
/// never become something the user has to click out of the way.
/// </summary>
internal static class Native
{
    private const int GwlExStyle = -20;

    private const int WsExTransparent = 0x0000_0020;
    private const int WsExToolWindow = 0x0000_0080;
    private const int WsExNoActivate = 0x0800_0000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, index) : new IntPtr(GetWindowLong32(hWnd, index));

    private static void SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value)
    {
        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr64(hWnd, index, value);
        }
        else
        {
            SetWindowLong32(hWnd, index, value.ToInt32());
        }
    }

    /// <summary>
    /// Applies the overlay window styles. <paramref name="clickThrough"/> decides whether
    /// mouse input passes straight through to whatever is underneath.
    /// </summary>
    public static void ApplyOverlayStyles(Window window, bool clickThrough)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        style |= WsExToolWindow | WsExNoActivate;

        if (clickThrough)
        {
            style |= WsExTransparent;
        }
        else
        {
            style &= ~(long)WsExTransparent;
        }

        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
    }

    /// <summary>Releases an HICON produced by <c>Bitmap.GetHicon</c>.</summary>
    public static void ReleaseIcon(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            DestroyIcon(handle);
        }
    }
}
