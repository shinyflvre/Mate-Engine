#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using UnityEngine;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Debug = UnityEngine.Debug;

public class MoveToPrimaryScreen : MonoBehaviour
{
    private IntPtr unityHWND = IntPtr.Zero;

    [DllImport("user32.dll")]
    private static extern bool GetClipCursor(out RECT lpRect);
    [DllImport("user32.dll")]
    private static extern bool ClipCursor(ref RECT lpRect);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    void Start()
    {
        unityHWND = Process.GetCurrentProcess().MainWindowHandle;
    }

    public void MoveToPrimary()
    {
        if (unityHWND == IntPtr.Zero) return;

        if (!GetWindowRect(unityHWND, out RECT rect)) return;

        int currentWidth = rect.Right - rect.Left;
        int currentHeight = rect.Bottom - rect.Top;

        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        var bounds = screen.Bounds;

        int x = bounds.Left + (bounds.Width - currentWidth) / 2;
        int y = bounds.Top + (bounds.Height - currentHeight) / 2;

        GetClipCursor(out RECT backup);
        MoveWindow(unityHWND, x, y, currentWidth, currentHeight, true);
        ClipCursor(ref backup);

        Debug.Log($"[MoveToPrimaryScreen] moved window {currentWidth}x{currentHeight} to {x},{y}");
    }
}
#endif
