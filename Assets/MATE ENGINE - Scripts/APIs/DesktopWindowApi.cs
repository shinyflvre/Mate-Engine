using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using Kirurobo;

public struct DesktopPoint
{
    public int X;
    public int Y;

    public DesktopPoint(int x, int y)
    {
        X = x;
        Y = y;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct DesktopRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public DesktopRect(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public Rect ToUnityRect() => new Rect(Left, Top, Width, Height);

    public bool Contains(int x, int y) => x >= Left && x <= Right && y >= Top && y <= Bottom;
    public bool Intersects(DesktopRect other) => Left < other.Right && Right > other.Left && Top < other.Bottom && Bottom > other.Top;

    public static DesktopRect FromPositionSize(float x, float y, float width, float height)
    {
        int left = Mathf.RoundToInt(x);
        int top = Mathf.RoundToInt(y);
        return new DesktopRect(left, top, left + Mathf.RoundToInt(width), top + Mathf.RoundToInt(height));
    }
}

public struct DesktopMonitorInfo
{
    public IntPtr Id;
    public DesktopRect Rect;
}

public struct DesktopWindowInfo
{
    public IntPtr Id;
    public uint OwnerPid;
    public string OwnerName;
    public string Title;
    public DesktopRect Rect;
    public float Alpha;
    public int Layer;
    public bool IsTaskbarLike;
    public int ZOrder;

    public bool IsValid => Id != IntPtr.Zero && !Rect.IsEmpty;
}

public interface IDesktopWindowApi
{
    bool IsSupported { get; }
    uint CurrentProcessId { get; }
    bool RefreshOwnWindow();
    bool IsOwnWindowForeground();
    bool TryGetCursorPosition(out DesktopPoint point);
    bool TryGetOwnWindowRect(out DesktopRect rect);
    bool TryGetOwnClientRect(out DesktopRect rect);
    bool TryMoveOwnWindow(int x, int y, int width, int height, bool repaint);
    bool TryMoveOwnWindowPosition(int x, int y);
    void SetOwnTopmost(bool enabled);
    IReadOnlyList<DesktopWindowInfo> EnumerateWindows();
    bool TryGetWindowRect(IntPtr windowId, out DesktopRect rect);
    bool IsWindowAlive(IntPtr windowId);
    bool IsWindowMinimized(IntPtr windowId);
    bool IsWindowMaximized(IntPtr windowId);
    bool IsWindowFullscreen(DesktopWindowInfo window);
    bool IsAbove(IntPtr a, IntPtr b);
    bool IsPointOccludedByHigherWindow(IntPtr targetWindowId, int x, int y, Func<DesktopWindowInfo, bool> ignoreWindow);
    IReadOnlyList<DesktopMonitorInfo> GetMonitors();
    DesktopRect GetNearestMonitorRect(DesktopRect rect);
    DesktopRect GetMonitorRectForOwnWindow();
    DesktopRect GetTaskbarRectForOwnWindow();
}

public static class DesktopWindowApi
{
    static IDesktopWindowApi _current;

    public static IDesktopWindowApi Current
    {
        get
        {
            if (_current == null) _current = Create();
            return _current;
        }
    }

    public static void Reset() => _current = null;

    static IDesktopWindowApi Create()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        return new WindowsDesktopWindowApi();
#elif UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
        return new MacDesktopWindowApi();
#else
        return new UnsupportedDesktopWindowApi();
#endif
    }
}

abstract class DesktopWindowApiBase : IDesktopWindowApi
{
    protected readonly List<DesktopWindowInfo> Windows = new List<DesktopWindowInfo>(128);
    protected readonly List<DesktopMonitorInfo> Monitors = new List<DesktopMonitorInfo>(8);

    public abstract bool IsSupported { get; }
    public virtual uint CurrentProcessId => unchecked((uint)Process.GetCurrentProcess().Id);
    public abstract bool RefreshOwnWindow();
    public abstract bool IsOwnWindowForeground();
    public abstract bool TryGetCursorPosition(out DesktopPoint point);
    public abstract bool TryGetOwnWindowRect(out DesktopRect rect);
    public abstract bool TryGetOwnClientRect(out DesktopRect rect);
    public abstract bool TryMoveOwnWindow(int x, int y, int width, int height, bool repaint);
    public abstract bool TryMoveOwnWindowPosition(int x, int y);
    public abstract void SetOwnTopmost(bool enabled);
    public abstract IReadOnlyList<DesktopWindowInfo> EnumerateWindows();
    public abstract bool TryGetWindowRect(IntPtr windowId, out DesktopRect rect);
    public abstract bool IsWindowAlive(IntPtr windowId);
    public abstract bool IsWindowMinimized(IntPtr windowId);
    public abstract bool IsWindowMaximized(IntPtr windowId);
    public abstract bool IsAbove(IntPtr a, IntPtr b);
    public abstract IReadOnlyList<DesktopMonitorInfo> GetMonitors();

    public virtual bool IsWindowFullscreen(DesktopWindowInfo window)
    {
        DesktopRect mon = GetNearestMonitorRect(window.Rect);
        if (mon.IsEmpty) return false;
        const int tolerance = 2;
        return Mathf.Abs(window.Rect.Width - mon.Width) <= tolerance && Mathf.Abs(window.Rect.Height - mon.Height) <= tolerance;
    }

    public virtual bool IsPointOccludedByHigherWindow(IntPtr targetWindowId, int x, int y, Func<DesktopWindowInfo, bool> ignoreWindow)
    {
        var list = EnumerateWindows();
        for (int i = 0; i < list.Count; i++)
        {
            var window = list[i];
            if (window.Id == targetWindowId) return false;
            if (!window.Rect.Contains(x, y)) continue;
            if (ignoreWindow != null && ignoreWindow(window)) continue;
            return true;
        }
        return false;
    }

    public virtual DesktopRect GetNearestMonitorRect(DesktopRect rect)
    {
        var monitors = GetMonitors();
        if (monitors.Count == 0) return rect;

        int bestIndex = 0;
        int bestArea = int.MinValue;
        for (int i = 0; i < monitors.Count; i++)
        {
            int left = Math.Max(rect.Left, monitors[i].Rect.Left);
            int top = Math.Max(rect.Top, monitors[i].Rect.Top);
            int right = Math.Min(rect.Right, monitors[i].Rect.Right);
            int bottom = Math.Min(rect.Bottom, monitors[i].Rect.Bottom);
            int area = Math.Max(0, right - left) * Math.Max(0, bottom - top);
            if (area > bestArea)
            {
                bestArea = area;
                bestIndex = i;
            }
        }
        return monitors[bestIndex].Rect;
    }

    public virtual DesktopRect GetMonitorRectForOwnWindow()
    {
        if (TryGetOwnWindowRect(out DesktopRect rect)) return GetNearestMonitorRect(rect);
        var monitors = GetMonitors();
        return monitors.Count > 0 ? monitors[0].Rect : new DesktopRect(0, 0, Screen.currentResolution.width, Screen.currentResolution.height);
    }

    public virtual DesktopRect GetTaskbarRectForOwnWindow() => new DesktopRect();
}

class UnsupportedDesktopWindowApi : DesktopWindowApiBase
{
    public override bool IsSupported => false;
    public override bool RefreshOwnWindow() => false;
    public override bool IsOwnWindowForeground() => false;
    public override bool TryGetCursorPosition(out DesktopPoint point) { point = new DesktopPoint(); return false; }
    public override bool TryGetOwnWindowRect(out DesktopRect rect) { rect = new DesktopRect(); return false; }
    public override bool TryGetOwnClientRect(out DesktopRect rect) { rect = new DesktopRect(); return false; }
    public override bool TryMoveOwnWindow(int x, int y, int width, int height, bool repaint) => false;
    public override bool TryMoveOwnWindowPosition(int x, int y) => false;
    public override void SetOwnTopmost(bool enabled) { }
    public override IReadOnlyList<DesktopWindowInfo> EnumerateWindows() { Windows.Clear(); return Windows; }
    public override bool TryGetWindowRect(IntPtr windowId, out DesktopRect rect) { rect = new DesktopRect(); return false; }
    public override bool IsWindowAlive(IntPtr windowId) => false;
    public override bool IsWindowMinimized(IntPtr windowId) => false;
    public override bool IsWindowMaximized(IntPtr windowId) => false;
    public override bool IsAbove(IntPtr a, IntPtr b) => false;
    public override IReadOnlyList<DesktopMonitorInfo> GetMonitors() { Monitors.Clear(); return Monitors; }
}

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
class WindowsDesktopWindowApi : DesktopWindowApiBase
{
    const int SW_MAXIMIZE = 3;
    const int DWMWA_CLOAKED = 14;
    const int GWL_STYLE = -16;
    const int GWL_EXSTYLE = -20;
    const int WS_CAPTION = 0x00C00000;
    const int WS_EX_LAYERED = 0x00080000;
    const int WS_EX_TRANSPARENT = 0x00000020;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const uint LWA_COLORKEY = 0x00000001;
    const uint LWA_ALPHA = 0x00000002;
    const uint GW_HWNDPREV = 3;
    const uint GW_OWNER = 4;
    const uint GA_ROOT = 2;
    const uint MONITOR_DEFAULTTONEAREST = 2;
    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_NOACTIVATE = 0x0010;
    const int SM_CXVIRTUALSCREEN = 78;
    const int SM_CYVIRTUALSCREEN = 79;
    const int SM_XVIRTUALSCREEN = 76;
    const int SM_YVIRTUALSCREEN = 77;
    const int ABM_GETTASKBARPOS = 0x00000005;
    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

    readonly StringBuilder _classNameBuffer = new StringBuilder(256);
    IntPtr _ownWindow;
    readonly uint _pid;

    public WindowsDesktopWindowApi()
    {
        _pid = GetCurrentProcessId();
        RefreshOwnWindow();
    }

    public override bool IsSupported => true;
    public override uint CurrentProcessId => _pid;

    public override bool RefreshOwnWindow()
    {
        IntPtr mainWindow = Process.GetCurrentProcess().MainWindowHandle;
        if (mainWindow != IntPtr.Zero && IsWindowVisible(mainWindow))
        {
            _ownWindow = mainWindow;
            return true;
        }

        _ownWindow = FindOwnTopLevelWindow();
        return _ownWindow != IntPtr.Zero;
    }

    public override bool IsOwnWindowForeground()
    {
        if (_ownWindow == IntPtr.Zero) RefreshOwnWindow();
        return _ownWindow != IntPtr.Zero && GetForegroundWindow() == _ownWindow;
    }

    public override bool TryGetCursorPosition(out DesktopPoint point)
    {
        if (GetCursorPos(out POINT p))
        {
            point = new DesktopPoint(p.X, p.Y);
            return true;
        }
        point = new DesktopPoint();
        return false;
    }

    public override bool TryGetOwnWindowRect(out DesktopRect rect)
    {
        if (_ownWindow == IntPtr.Zero) RefreshOwnWindow();
        return TryGetWindowRect(_ownWindow, out rect);
    }

    public override bool TryGetOwnClientRect(out DesktopRect rect)
    {
        rect = new DesktopRect();
        if (_ownWindow == IntPtr.Zero) RefreshOwnWindow();
        if (_ownWindow == IntPtr.Zero || !GetClientRect(_ownWindow, out RECT client)) return false;
        POINT p = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(_ownWindow, ref p)) return false;
        rect = new DesktopRect(p.X, p.Y, p.X + client.Right, p.Y + client.Bottom);
        return true;
    }

    public override bool TryMoveOwnWindow(int x, int y, int width, int height, bool repaint)
    {
        if (_ownWindow == IntPtr.Zero) RefreshOwnWindow();
        return _ownWindow != IntPtr.Zero && MoveWindow(_ownWindow, x, y, width, height, repaint);
    }

    public override bool TryMoveOwnWindowPosition(int x, int y)
    {
        if (_ownWindow == IntPtr.Zero) RefreshOwnWindow();
        return _ownWindow != IntPtr.Zero && SetWindowPos(_ownWindow, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public override void SetOwnTopmost(bool enabled)
    {
        if (_ownWindow == IntPtr.Zero) RefreshOwnWindow();
        if (_ownWindow != IntPtr.Zero) SetWindowPos(_ownWindow, enabled ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public override IReadOnlyList<DesktopWindowInfo> EnumerateWindows()
    {
        Windows.Clear();
        int z = 0;
        EnumWindows((hWnd, lParam) =>
        {
            if (hWnd == _ownWindow || !IsWindowVisible(hWnd) || !TryGetWindowRect(hWnd, out DesktopRect r)) return true;
            _classNameBuffer.Clear();
            GetClassName(hWnd, _classNameBuffer, _classNameBuffer.Capacity);
            if (IsSameProcessWindow(hWnd) || IsEffectivelyTransparentWindow(hWnd, _classNameBuffer, 230)) return true;

            bool isTaskbar = SBEq(_classNameBuffer, "Shell_TrayWnd") || SBEq(_classNameBuffer, "Shell_SecondaryTrayWnd");
            if (!isTaskbar && (IsLikelyUniWindowMascot(hWnd, _classNameBuffer) || !IsEligibleWindow(hWnd, r, _classNameBuffer))) return true;

            GetWindowThreadProcessId(hWnd, out uint ownerPid);
            Windows.Add(new DesktopWindowInfo
            {
                Id = hWnd,
                OwnerPid = ownerPid,
                OwnerName = _classNameBuffer.ToString(),
                Title = string.Empty,
                Rect = r,
                Alpha = GetLayeredAlpha(hWnd),
                Layer = 0,
                IsTaskbarLike = isTaskbar,
                ZOrder = z++
            });
            return true;
        }, IntPtr.Zero);
        return Windows;
    }

    public override bool TryGetWindowRect(IntPtr windowId, out DesktopRect rect)
    {
        if (windowId != IntPtr.Zero && GetWindowRect(windowId, out RECT r))
        {
            rect = new DesktopRect(r.Left, r.Top, r.Right, r.Bottom);
            return true;
        }
        rect = new DesktopRect();
        return false;
    }

    public override bool IsWindowAlive(IntPtr windowId) => windowId != IntPtr.Zero && TryGetWindowRect(windowId, out _) && IsWindowVisible(windowId) && !IsCloaked(windowId);
    public override bool IsWindowMinimized(IntPtr windowId) => windowId != IntPtr.Zero && IsIconic(windowId);

    public override bool IsWindowMaximized(IntPtr windowId)
    {
        if (windowId == IntPtr.Zero) return false;
        WINDOWPLACEMENT placement = new WINDOWPLACEMENT { length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
        return GetWindowPlacement(windowId, ref placement) && placement.showCmd == SW_MAXIMIZE;
    }

    public override bool IsAbove(IntPtr a, IntPtr b)
    {
        if (a == b || a == IntPtr.Zero || b == IntPtr.Zero) return false;
        IntPtr h = b;
        for (int i = 0; i < 2048 && h != IntPtr.Zero; i++)
        {
            h = GetWindow(h, GW_HWNDPREV);
            if (h == a) return true;
        }
        return false;
    }

    public override bool IsPointOccludedByHigherWindow(IntPtr targetWindowId, int x, int y, Func<DesktopWindowInfo, bool> ignoreWindow)
    {
        IntPtr h = GetWindow(targetWindowId, GW_HWNDPREV);
        int z = 0;
        while (h != IntPtr.Zero)
        {
            if (h == _ownWindow || IsSameProcessWindow(h)) { h = GetWindow(h, GW_HWNDPREV); continue; }
            if (!IsWindowVisible(h) || IsCloaked(h) || !TryGetWindowRect(h, out DesktopRect r)) { h = GetWindow(h, GW_HWNDPREV); continue; }
            if (!r.Contains(x, y)) { h = GetWindow(h, GW_HWNDPREV); continue; }

            _classNameBuffer.Clear();
            GetClassName(h, _classNameBuffer, _classNameBuffer.Capacity);
            if (IsEffectivelyTransparentWindow(h, _classNameBuffer, 8) || IsLikelyUniWindowMascot(h, _classNameBuffer)) { h = GetWindow(h, GW_HWNDPREV); continue; }

            long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_TRANSPARENT) != 0) { h = GetWindow(h, GW_HWNDPREV); continue; }
            var info = new DesktopWindowInfo { Id = h, Rect = r, OwnerName = _classNameBuffer.ToString(), Alpha = GetLayeredAlpha(h), ZOrder = z++ };
            if (ignoreWindow != null && ignoreWindow(info)) { h = GetWindow(h, GW_HWNDPREV); continue; }
            return true;
        }
        return false;
    }

    public override IReadOnlyList<DesktopMonitorInfo> GetMonitors()
    {
        Monitors.Clear();
        GCHandle gch = GCHandle.Alloc(Monitors);
        IntPtr data = GCHandle.ToIntPtr(gch);
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
        {
            var target = (List<DesktopMonitorInfo>)GCHandle.FromIntPtr(dwData).Target;
            MONITORINFO mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (GetMonitorInfo(hMonitor, ref mi))
            {
                target.Add(new DesktopMonitorInfo { Id = hMonitor, Rect = new DesktopRect(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom) });
            }
            return true;
        }, data);
        gch.Free();
        if (Monitors.Count == 0) Monitors.Add(new DesktopMonitorInfo { Rect = GetVirtualScreenRect() });
        return Monitors;
    }

    public override DesktopRect GetMonitorRectForOwnWindow()
    {
        if (_ownWindow == IntPtr.Zero) RefreshOwnWindow();
        IntPtr hmon = _ownWindow != IntPtr.Zero ? MonitorFromWindow(_ownWindow, MONITOR_DEFAULTTONEAREST) : IntPtr.Zero;
        if (hmon != IntPtr.Zero)
        {
            MONITORINFO mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (GetMonitorInfo(hmon, ref mi)) return new DesktopRect(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom);
        }
        return base.GetMonitorRectForOwnWindow();
    }

    public override DesktopRect GetTaskbarRectForOwnWindow()
    {
        APPBARDATA data = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)) };
        uint result = SHAppBarMessage(ABM_GETTASKBARPOS, ref data);
        return result != 0 ? new DesktopRect(data.rc.Left, data.rc.Top, data.rc.Right, data.rc.Bottom) : new DesktopRect();
    }

    DesktopRect GetVirtualScreenRect()
    {
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        return new DesktopRect(left, top, left + GetSystemMetrics(SM_CXVIRTUALSCREEN), top + GetSystemMetrics(SM_CYVIRTUALSCREEN));
    }

    bool IsSameProcessWindow(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out uint pid);
        return pid == _pid;
    }

    IntPtr FindOwnTopLevelWindow()
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = -1;
        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero) return true;
            if (!IsSameProcessWindow(hWnd)) return true;
            if (!TryGetWindowRect(hWnd, out DesktopRect rect)) return true;

            long area = (long)rect.Width * rect.Height;
            if (area > bestArea)
            {
                bestArea = area;
                best = hWnd;
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    bool IsEligibleWindow(IntPtr hWnd, DesktopRect r, StringBuilder cls)
    {
        if (GetParent(hWnd) != IntPtr.Zero || GetAncestor(hWnd, GA_ROOT) != hWnd || IsIconic(hWnd) || GetWindowTextLength(hWnd) == 0 || IsCloaked(hWnd)) return false;
        if (r.Width < 200 || r.Height < 60) return false;
        if (SBEq(cls, "Progman") || SBEq(cls, "WorkerW") || SBEq(cls, "DV2ControlHost") || SBEq(cls, "MsgrIMEWindowClass")) return false;
        if (SBStartsWith(cls, "#") || SBContains(cls, "Desktop")) return false;
        return true;
    }

    bool IsCloaked(IntPtr hWnd)
    {
        int cloaked = 0;
        DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out cloaked, sizeof(int));
        return cloaked != 0;
    }

    bool IsEffectivelyTransparentWindow(IntPtr hWnd, StringBuilder cls, int alphaIgnoreBelow)
    {
        long ex = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
        if ((ex & WS_EX_LAYERED) == 0) return false;
        if ((ex & WS_EX_TRANSPARENT) != 0) return true;
        if ((ex & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return true;
        if (GetLayeredWindowAttributes(hWnd, out _, out byte alpha, out uint flags))
        {
            if ((flags & LWA_COLORKEY) != 0) return true;
            if ((flags & LWA_ALPHA) != 0 && alpha <= alphaIgnoreBelow) return true;
        }
        long st = GetWindowLongPtr(hWnd, GWL_STYLE).ToInt64();
        int titleLen = GetWindowTextLength(hWnd);
        if ((st & WS_CAPTION) == 0 && titleLen <= 1) return true;
        if ((st & WS_CAPTION) == 0 && (SBEq(cls, "UnityWndClass") || SBEq(cls, "UnityGUIView"))) return true;
        return false;
    }

    bool IsLikelyUniWindowMascot(IntPtr hWnd, StringBuilder cls)
    {
        long ex = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
        long st = GetWindowLongPtr(hWnd, GWL_STYLE).ToInt64();
        bool layered = (ex & WS_EX_LAYERED) != 0;
        bool toolOrNoAct = (ex & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0;
        bool clickThrough = (ex & WS_EX_TRANSPARENT) != 0;
        bool translucent = false;
        if (layered && GetLayeredWindowAttributes(hWnd, out _, out byte alpha, out uint flags)) translucent = ((flags & LWA_ALPHA) != 0 && alpha < 255) || ((flags & LWA_COLORKEY) != 0);
        int titleLen = GetWindowTextLength(hWnd);
        if (layered && (toolOrNoAct || clickThrough || translucent) && (st & WS_CAPTION) == 0 && titleLen <= 1) return true;
        if (layered && (toolOrNoAct || clickThrough || translucent) && SBEq(cls, "UnityWndClass")) return true;
        return false;
    }

    float GetLayeredAlpha(IntPtr hWnd)
    {
        long ex = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
        if ((ex & WS_EX_LAYERED) == 0) return 1f;
        if (GetLayeredWindowAttributes(hWnd, out _, out byte alpha, out uint flags) && (flags & LWA_ALPHA) != 0) return alpha / 255f;
        return 1f;
    }

    static bool SBEq(StringBuilder sb, string s)
    {
        if (sb.Length != s.Length) return false;
        for (int i = 0; i < s.Length; i++) if (sb[i] != s[i]) return false;
        return true;
    }

    static bool SBStartsWith(StringBuilder sb, string s)
    {
        if (sb.Length < s.Length) return false;
        for (int i = 0; i < s.Length; i++) if (sb[i] != s[i]) return false;
        return true;
    }

    static bool SBContains(StringBuilder sb, string s)
    {
        int n = sb.Length, m = s.Length;
        for (int i = 0; i <= n - m; i++)
        {
            int j = 0;
            while (j < m && sb[i + j] == s[j]) j++;
            if (j == m) return true;
        }
        return false;
    }

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct WINDOWPLACEMENT { public int length; public int flags; public int showCmd; public POINT ptMinPosition; public POINT ptMaxPosition; public RECT rcNormalPosition; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }
    [StructLayout(LayoutKind.Sequential)] struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public RECT rc; public int lParam; }

    [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] static extern IntPtr GetWindowLong32(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
    static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);
    [DllImport("user32.dll")] static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint pcrKey, out byte pbAlpha, out uint pdwFlags);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)] static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", SetLastError = true)] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)] static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    [DllImport("shell32.dll", SetLastError = true)] static extern uint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);
}
#endif

#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
class MacDesktopWindowApi : DesktopWindowApiBase
{
    const int MaxString = 256;
    const int MaxWindowCount = 512;
    bool _loggedCoreGraphicsBackend;

    public override bool IsSupported => true;
    public override bool RefreshOwnWindow()
    {
        try { _ = UniWindowController.current; return true; }
        catch { return false; }
    }

    public override bool IsOwnWindowForeground() => Application.isFocused;

    public override bool TryGetCursorPosition(out DesktopPoint point)
    {
        if (MacNative.GetCursorPosition(out NativePoint p))
        {
            point = new DesktopPoint(Mathf.RoundToInt((float)p.X), Mathf.RoundToInt((float)p.Y));
            return true;
        }
        point = new DesktopPoint();
        return false;
    }

    public override bool TryGetOwnWindowRect(out DesktopRect rect)
    {
        if (TryGetUniWindowRect(out rect)) return true;
        if (MacNative.GetOwnWindowRect(out NativeRect nativeRect))
        {
            rect = nativeRect.ToDesktopRect();
            return !rect.IsEmpty;
        }
        return false;
    }

    public override bool TryGetOwnClientRect(out DesktopRect rect)
    {
        if (MacNative.GetOwnClientRect(out NativeRect nativeRect))
        {
            rect = nativeRect.ToDesktopRect();
            return !rect.IsEmpty;
        }
        if (TryGetUniWindowClientRect(out rect)) return true;
        if (TryGetOwnWindowRect(out rect)) return true;
        return false;
    }

    public override bool TryMoveOwnWindow(int x, int y, int width, int height, bool repaint)
    {
        if (width <= 0 || height <= 0) return false;
        try
        {
            var controller = UniWindowController.current;
            controller.windowSize = new Vector2(width, height);
            controller.windowPosition = new Vector2(x, y);
            return true;
        }
        catch
        {
            return MacNative.MoveOwnWindow(x, y, width, height);
        }
    }

    public override bool TryMoveOwnWindowPosition(int x, int y)
    {
        if (TryGetOwnWindowRect(out DesktopRect r))
            return TryMoveOwnWindow(x, y, Mathf.Max(1, r.Width), Mathf.Max(1, r.Height), true);
        return false;
    }

    public override void SetOwnTopmost(bool enabled)
    {
        try { UniWindowController.current.isTopmost = enabled; }
        catch { MacNative.SetOwnTopmost(enabled); }
    }

    bool TryGetUniWindowRect(out DesktopRect rect)
    {
        try
        {
            var controller = UniWindowController.current;
            Vector2 pos = controller.windowPosition;
            Vector2 size = controller.windowSize;
            rect = DesktopRect.FromPositionSize(pos.x, pos.y, size.x, size.y);
            return !rect.IsEmpty;
        }
        catch
        {
            rect = new DesktopRect();
            return false;
        }
    }

    bool TryGetUniWindowClientRect(out DesktopRect rect)
    {
        try
        {
            var controller = UniWindowController.current;
            Vector2 pos = controller.windowPosition;
            Vector2 size = controller.clientSize;
            if (size.x <= 0f || size.y <= 0f) size = controller.windowSize;
            rect = DesktopRect.FromPositionSize(pos.x, pos.y, size.x, size.y);
            return !rect.IsEmpty;
        }
        catch
        {
            rect = new DesktopRect();
            return false;
        }
    }

    public override IReadOnlyList<DesktopWindowInfo> EnumerateWindows()
    {
        Windows.Clear();
        var native = new NativeWindowInfo[MaxWindowCount];
        int copied = MacNative.CopyWindowInfos(native, native.Length);
        AddNativeWindows(native, copied);
        LogCoreGraphicsBackend();
        return Windows;
    }

    void AddNativeWindows(NativeWindowInfo[] native, int copied)
    {
        for (int i = 0; i < copied; i++)
        {
            NativeWindowInfo w = native[i];
            var rect = new DesktopRect(w.Left, w.Top, w.Right, w.Bottom);
            if (!w.OnScreen || rect.Width < 200 || rect.Height < 60) continue;
            uint ownerPid = w.OwnerPid;
            string ownerName = w.OwnerName ?? string.Empty;
            string title = w.Title ?? string.Empty;

            if (ownerPid == CurrentProcessId) continue;
            if (w.Layer != 0) continue;
            if (w.Alpha <= 0.05f) continue;

            if (IsSystemOrDesktopWindow(ownerName, title)) continue;

            Windows.Add(new DesktopWindowInfo
            {
                Id = new IntPtr(unchecked((int)w.WindowId)),
                OwnerPid = ownerPid,
                OwnerName = ownerName,
                Title = title,
                Rect = rect,
                Alpha = w.Alpha,
                Layer = w.Layer,
                IsTaskbarLike = false,
                ZOrder = Windows.Count
            });
        }
    }

    public override bool TryGetWindowRect(IntPtr windowId, out DesktopRect rect)
    {
        uint id = unchecked((uint)windowId.ToInt64());
        if (id != 0 && MacNative.GetWindowRect(id, out NativeRect nativeRect))
        {
            rect = nativeRect.ToDesktopRect();
            return true;
        }
        rect = new DesktopRect();
        return false;
    }

    public override bool IsWindowAlive(IntPtr windowId) => TryGetWindowRect(windowId, out DesktopRect r) && !r.IsEmpty;
    public override bool IsWindowMinimized(IntPtr windowId) => !IsWindowAlive(windowId);
    public override bool IsWindowMaximized(IntPtr windowId) => false;

    public override bool IsAbove(IntPtr a, IntPtr b)
    {
        var windows = EnumerateWindows();
        int ai = -1, bi = -1;
        for (int i = 0; i < windows.Count; i++)
        {
            if (windows[i].Id == a) ai = i;
            if (windows[i].Id == b) bi = i;
        }
        return ai >= 0 && bi >= 0 && ai < bi;
    }

    public override IReadOnlyList<DesktopMonitorInfo> GetMonitors()
    {
        Monitors.Clear();
        int count = MacNative.GetMonitorCount();
        for (int i = 0; i < count; i++)
        {
            if (MacNative.GetMonitorRect(i, out NativeRect nativeRect))
            {
                Monitors.Add(new DesktopMonitorInfo { Id = new IntPtr(i + 1), Rect = nativeRect.ToDesktopRect() });
            }
        }
        if (Monitors.Count == 0)
            Monitors.Add(new DesktopMonitorInfo { Rect = new DesktopRect(0, 0, Screen.currentResolution.width, Screen.currentResolution.height) });
        return Monitors;
    }

    static bool IsSystemOrDesktopWindow(string ownerName, string title)
    {
        if (string.IsNullOrEmpty(ownerName)) return false;
        if (ownerName == "Dock" || ownerName == "SystemUIServer" || ownerName == "Window Server") return true;
        if (ownerName == "Wallpaper" || ownerName == "Control Center" || ownerName == "Notification Center") return true;
        if (title == "Desktop" || title == "Window") return true;
        return false;
    }

    void LogCoreGraphicsBackend()
    {
        if (_loggedCoreGraphicsBackend) return;
        _loggedCoreGraphicsBackend = true;
        UnityEngine.Debug.Log("Mate Engine macOS window API using CoreGraphics/AppKit backend.");
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativePoint
    {
        public double X;
        public double Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeRect
    {
        public double Left;
        public double Top;
        public double Right;
        public double Bottom;

        public DesktopRect ToDesktopRect()
        {
            return new DesktopRect(
                Mathf.RoundToInt((float)Left),
                Mathf.RoundToInt((float)Top),
                Mathf.RoundToInt((float)Right),
                Mathf.RoundToInt((float)Bottom));
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    struct NativeWindowInfo
    {
        public uint WindowId;
        public uint OwnerPid;
        public int Layer;
        public float Alpha;
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        [MarshalAs(UnmanagedType.I1)] public bool OnScreen;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxString)] public string OwnerName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxString)] public string Title;
    }

    static class MacNative
    {
        const string Lib = "MateDesktopWindowMac";

        [DllImport(Lib, EntryPoint = "MateDWCopyWindowInfos")]
        public static extern int CopyWindowInfos([Out] NativeWindowInfo[] windows, int capacity);

        [DllImport(Lib, EntryPoint = "MateDWGetWindowRect")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool GetWindowRect(uint windowId, out NativeRect rect);

        [DllImport(Lib, EntryPoint = "MateDWGetOwnWindowRect")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool GetOwnWindowRect(out NativeRect rect);

        [DllImport(Lib, EntryPoint = "MateDWGetOwnClientRect")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool GetOwnClientRect(out NativeRect rect);

        [DllImport(Lib, EntryPoint = "MateDWMoveOwnWindow")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool MoveOwnWindow(int x, int y, int width, int height);

        [DllImport(Lib, EntryPoint = "MateDWSetOwnTopmost")]
        public static extern void SetOwnTopmost([MarshalAs(UnmanagedType.I1)] bool enabled);

        [DllImport(Lib, EntryPoint = "MateDWGetCursorPosition")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool GetCursorPosition(out NativePoint point);

        [DllImport(Lib, EntryPoint = "MateDWGetMonitorCount")]
        public static extern int GetMonitorCount();

        [DllImport(Lib, EntryPoint = "MateDWGetMonitorRect")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool GetMonitorRect(int index, out NativeRect rect);

    }
}
#endif
