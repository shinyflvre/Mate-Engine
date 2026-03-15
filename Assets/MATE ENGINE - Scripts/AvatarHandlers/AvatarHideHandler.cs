using UnityEngine;
using System;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Collections.Generic;

public class AvatarHideHandler : MonoBehaviour
{
    public int snapThresholdPx = 12;
    public int unsnapThresholdPx = 24;
    public int edgeInsetPx = 0;

    public int adjacencyTolerancePx = 6;
    public int adjacencyMinVerticalOverlapPx = 32;

    public int snapCalibrationFrames = 10;
    public int maxSnapCompensationPx = 96;

    public bool enableSmoothing = true;
    [Range(0.01f, 0.5f)] public float smoothingTime = 0.10f;
    public float smoothingMaxSpeed = 6000f;
    public bool keepTopmostWhileSnapped = true;
    public float unsnapGraceTime = 0.12f;
    public float unsnapCooldownSeconds = 0.3f;

    Animator animator;
    AvatarAnimatorController controller;
    IntPtr unityHWND;

    Transform leftHand;
    Transform rightHand;
    Camera cam;

    enum Side { None, Left, Right }
    Side snappedSide = Side.None;

    int cursorOffsetY;
    float velX, velY;
    bool smoothingActive;
    bool wasDragging;
    float snappedAt;
    float unsnapCooldownUntil;

    int dragBaseW;
    int dragBaseH;

    IntPtr snappedHmon = IntPtr.Zero;

    int snapCompX;
    int calibRemaining;

    struct MonitorData
    {
        public IntPtr hmon;
        public RECT rect;
    }

    void Start()
    {
#if UNITY_STANDALONE_WIN
        unityHWND = Process.GetCurrentProcess().MainWindowHandle;
#endif
        animator = GetComponent<Animator>();
        controller = GetComponent<AvatarAnimatorController>();
        if (animator != null && animator.isHuman && animator.avatar != null)
        {
            leftHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
            rightHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
        }
        cam = Camera.main;
        if (cam == null) cam = FindObjectOfType<Camera>();
        unsnapCooldownUntil = -1f;
        dragBaseW = 0;
        dragBaseH = 0;
        snapCompX = 0;
        calibRemaining = 0;
    }

    void OnDisable()
    {
        SetHide(false, false);
        snappedSide = Side.None;
        snappedHmon = IntPtr.Zero;
        unsnapCooldownUntil = -1f;
        SetTopMost(false);
        snapCompX = 0;
        calibRemaining = 0;
    }

    void Update()
    {
#if !UNITY_STANDALONE_WIN
        return;
#else
        if (unityHWND == IntPtr.Zero || animator == null || controller == null) return;

        if (controller.isDragging && !wasDragging)
        {
            if (GetWindowRect(unityHWND, out RECT wr) && GetCursorPos(out POINT cp))
            {
                dragBaseW = Math.Max(1, wr.Right - wr.Left);
                dragBaseH = Math.Max(1, wr.Bottom - wr.Top);
                cursorOffsetY = cp.y - wr.Top;
                smoothingActive = false;
                velX = 0f;
                velY = 0f;
            }
        }

        EnsureSaneWindowSize();

        if (controller.isDragging)
        {
            if (!GetCursorPos(out POINT cp)) { wasDragging = controller.isDragging; return; }
            if (!GetWindowRect(unityHWND, out RECT wrCur)) { wasDragging = controller.isDragging; return; }

            IntPtr hmonWin = MonitorFromWindow(unityHWND, MONITOR_DEFAULTTONEAREST);
            RECT monWin = GetMonitorRectFromHandle(hmonWin);

            bool allowLeftEdge;
            bool allowRightEdge;
            GetAllowedEdgesForMonitor(hmonWin, out allowLeftEdge, out allowRightEdge);

            int anchorLeftDesk = GetAnchorDesktopX(Side.Left);
            int anchorRightDesk = GetAnchorDesktopX(Side.Right);

            if (anchorLeftDesk < 0) anchorLeftDesk = wrCur.Left + Math.Max(1, (wrCur.Right - wrCur.Left) / 2);
            if (anchorRightDesk < 0) anchorRightDesk = wrCur.Left + Math.Max(1, (wrCur.Right - wrCur.Left) / 2);

            int thrSnap = Math.Max(1, snapThresholdPx);
            int leftEdgeX = monWin.Left + edgeInsetPx;
            int rightEdgeX = (monWin.Right - 1) - edgeInsetPx;

            bool nearLeft = allowLeftEdge && Mathf.Abs(anchorLeftDesk - leftEdgeX) <= thrSnap;
            bool nearRight = allowRightEdge && Mathf.Abs(anchorRightDesk - rightEdgeX) <= thrSnap;

            if (snappedSide == Side.None)
            {
                if (Time.unscaledTime >= unsnapCooldownUntil)
                {
                    if (nearLeft) SnapTo(Side.Left, cp, hmonWin, monWin);
                    else if (nearRight) SnapTo(Side.Right, cp, hmonWin, monWin);
                }
            }
            else
            {
                if (Time.unscaledTime >= snappedAt + unsnapGraceTime)
                {
                    RECT monSnap = GetSnappedMonitorRect();
                    int edgeX = GetBaseDesiredEdgeX(monSnap, snappedSide);
                    int thrUnsnap = Math.Max(1, unsnapThresholdPx);
                    if (Mathf.Abs(cp.x - edgeX) > thrUnsnap) Unsnap();
                }
            }

            if (snappedSide != Side.None)
            {
                if (!GetWindowRect(unityHWND, out RECT wr2)) { wasDragging = controller.isDragging; return; }
                RECT monNow = GetSnappedMonitorRect();

                int baseDesired = GetBaseDesiredEdgeX(monNow, snappedSide);
                ApplySnapCalibration(baseDesired);

                int desiredAnchorDesk = baseDesired + snapCompX;

                int anchorDesk = GetAnchorDesktopX(snappedSide);
                if (anchorDesk < 0) anchorDesk = wr2.Left + Math.Max(1, (wr2.Right - wr2.Left) / 2);
                int w = Math.Max(1, wr2.Right - wr2.Left);
                int anchorWinX = Mathf.Clamp(anchorDesk - wr2.Left, 0, w);

                int tx = desiredAnchorDesk - anchorWinX;
                int ty = cp.y - cursorOffsetY;

                MoveSmooth(wr2.Left, wr2.Top, tx, ty);

                if (keepTopmostWhileSnapped) SetTopMost(true);
            }
        }
        else
        {
            if (snappedSide != Side.None)
            {
                if (!GetWindowRect(unityHWND, out RECT wr)) return;
                RECT mon = GetSnappedMonitorRect();

                int baseDesired = GetBaseDesiredEdgeX(mon, snappedSide);
                ApplySnapCalibration(baseDesired);

                int desiredAnchorDesk = baseDesired + snapCompX;

                int anchorDesk = GetAnchorDesktopX(snappedSide);
                if (anchorDesk < 0) anchorDesk = wr.Left + Math.Max(1, (wr.Right - wr.Left) / 2);
                int w = Math.Max(1, wr.Right - wr.Left);
                int anchorWinX = Mathf.Clamp(anchorDesk - wr.Left, 0, w);

                int tx = desiredAnchorDesk - anchorWinX;
                int ty = wr.Top;

                MoveSmooth(wr.Left, wr.Top, tx, ty);

                if (keepTopmostWhileSnapped) SetTopMost(true);
            }
        }

        wasDragging = controller.isDragging;
#endif
    }

#if UNITY_STANDALONE_WIN
    int GetBaseDesiredEdgeX(RECT mon, Side side)
    {
        if (side == Side.Left) return mon.Left + edgeInsetPx;
        if (side == Side.Right) return (mon.Right - 1) - edgeInsetPx;
        return 0;
    }

    void ApplySnapCalibration(int baseDesired)
    {
        if (calibRemaining <= 0) return;

        int current = GetAnchorDesktopX(snappedSide);
        if (current >= 0)
        {
            int err = baseDesired - current;
            if (err != 0)
            {
                snapCompX = Mathf.Clamp(snapCompX + err, -Mathf.Abs(maxSnapCompensationPx), Mathf.Abs(maxSnapCompensationPx));
            }
        }

        calibRemaining--;
    }

    void GetAllowedEdgesForMonitor(IntPtr hmon, out bool allowLeft, out bool allowRight)
    {
        List<MonitorData> mons = GetAllMonitors();
        if (mons.Count == 0)
        {
            allowLeft = false;
            allowRight = false;
            return;
        }

        if (mons.Count == 1)
        {
            allowLeft = true;
            allowRight = true;
            return;
        }

        RECT cur = GetMonitorRectFromHandle(hmon);
        bool hasLeftNeighbor = false;
        bool hasRightNeighbor = false;

        int tol = Mathf.Max(0, adjacencyTolerancePx);
        int minOverlap = Mathf.Max(1, adjacencyMinVerticalOverlapPx);

        for (int i = 0; i < mons.Count; i++)
        {
            RECT r = mons[i].rect;

            int overlap = VerticalOverlap(cur, r);
            if (overlap < minOverlap) continue;

            if (Mathf.Abs(r.Right - cur.Left) <= tol) hasLeftNeighbor = true;
            if (Mathf.Abs(r.Left - cur.Right) <= tol) hasRightNeighbor = true;

            if (hasLeftNeighbor && hasRightNeighbor) break;
        }

        allowLeft = !hasLeftNeighbor;
        allowRight = !hasRightNeighbor;
    }

    int VerticalOverlap(RECT a, RECT b)
    {
        int top = Math.Max(a.Top, b.Top);
        int bottom = Math.Min(a.Bottom, b.Bottom);
        return Math.Max(0, bottom - top);
    }

    List<MonitorData> GetAllMonitors()
    {
        List<MonitorData> list = new List<MonitorData>();
        GCHandle gch = GCHandle.Alloc(list);
        IntPtr data = GCHandle.ToIntPtr(gch);

        MonitorEnumProc proc = (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
        {
            GCHandle h = GCHandle.FromIntPtr(dwData);
            List<MonitorData> target = (List<MonitorData>)h.Target;

            MONITORINFO mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (GetMonitorInfo(hMonitor, ref mi))
            {
                MonitorData md;
                md.hmon = hMonitor;
                md.rect = mi.rcMonitor;
                target.Add(md);
            }
            return true;
        };

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, data);
        gch.Free();
        return list;
    }

    int GetAnchorDesktopX(Side side)
    {
        Transform t = side == Side.Left ? leftHand : rightHand;
        if (t == null || cam == null) return -1;
        if (!GetUnityClientRect(out RECT uCli)) return -1;

        Vector3 sp = cam.WorldToScreenPoint(t.position);
        if (sp.z < 0.01f) return -1;

        float clientW = Mathf.Max(1f, uCli.Right - uCli.Left);
        float pxW = Mathf.Max(1, cam.pixelWidth);
        float sx = Mathf.Clamp(sp.x, 0, cam.pixelWidth) * (clientW / pxW);
        int desktopX = uCli.Left + Mathf.RoundToInt(sx);
        return desktopX;
    }

    void SnapTo(Side side, POINT cp, IntPtr hmon, RECT mon)
    {
        if (!GetWindowRect(unityHWND, out RECT wr)) return;

        int w = Math.Max(1, wr.Right - wr.Left);
        int h = Math.Max(1, wr.Bottom - wr.Top);

        if (dragBaseW <= 0) dragBaseW = w;
        if (dragBaseH <= 0) dragBaseH = h;

        cursorOffsetY = cp.y - wr.Top;
        snappedSide = side;
        snappedHmon = hmon;

        snapCompX = 0;
        calibRemaining = Mathf.Clamp(snapCalibrationFrames, 0, 60);

        SetHide(side == Side.Left, side == Side.Right);

        int anchorDesk = GetAnchorDesktopX(side);
        if (anchorDesk < 0) anchorDesk = wr.Left + Math.Max(1, (wr.Right - wr.Left) / 2);
        int anchorWinX = Mathf.Clamp(anchorDesk - wr.Left, 0, w);

        int baseDesired = GetBaseDesiredEdgeX(mon, side);
        int tx = baseDesired - anchorWinX;
        int ty = cp.y - cursorOffsetY;

        MoveOnly(tx, ty);

        smoothingActive = enableSmoothing;
        velX = 0f;
        velY = 0f;
        snappedAt = Time.unscaledTime;

        if (keepTopmostWhileSnapped) SetTopMost(true);
    }

    void Unsnap()
    {
        snappedSide = Side.None;
        snappedHmon = IntPtr.Zero;
        SetHide(false, false);
        smoothingActive = false;
        velX = 0f;
        velY = 0f;
        SetTopMost(false);

        snapCompX = 0;
        calibRemaining = 0;

        if (controller != null && controller.isDragging)
            unsnapCooldownUntil = Time.unscaledTime + Mathf.Max(0f, unsnapCooldownSeconds);
    }

    void SetHide(bool left, bool right)
    {
        animator.SetBool("HideLeft", left);
        animator.SetBool("HideRight", right);
    }

    void MoveSmooth(int curX, int curY, int targetX, int targetY)
    {
        if (!enableSmoothing || !smoothingActive)
        {
            if (curX != targetX || curY != targetY) MoveOnly(targetX, targetY);
            return;
        }

        float dt = Time.unscaledDeltaTime;
        float nx = Mathf.SmoothDamp(curX, targetX, ref velX, smoothingTime, smoothingMaxSpeed, dt);
        float ny = Mathf.SmoothDamp(curY, targetY, ref velY, smoothingTime, smoothingMaxSpeed, dt);
        int ix = Mathf.RoundToInt(nx);
        int iy = Mathf.RoundToInt(ny);

        if (Mathf.Abs(targetX - ix) <= 1 && Mathf.Abs(targetY - iy) <= 1)
        {
            ix = targetX;
            iy = targetY;
            smoothingActive = false;
            velX = 0f;
            velY = 0f;
        }

        if (ix != curX || iy != curY) MoveOnly(ix, iy);
    }

    void MoveOnly(int x, int y)
    {
        GetClipCursor(out RECT backup);
        SetWindowPos(unityHWND, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        ClipCursor(ref backup);
    }

    void EnsureSaneWindowSize()
    {
        if (!GetWindowRect(unityHWND, out RECT wr)) return;
        RECT vs = GetVirtualScreenRect();

        int w = Math.Max(1, wr.Right - wr.Left);
        int h = Math.Max(1, wr.Bottom - wr.Top);
        int vw = Math.Max(1, vs.Right - vs.Left);
        int vh = Math.Max(1, vs.Bottom - vs.Top);

        if (w <= vw && h <= vh) return;

        int targetW = dragBaseW > 0 ? Mathf.Clamp(dragBaseW, 1, vw) : Mathf.Clamp(w, 1, vw);
        int targetH = dragBaseH > 0 ? Mathf.Clamp(dragBaseH, 1, vh) : Mathf.Clamp(h, 1, vh);

        SetWindowPos(unityHWND, IntPtr.Zero, 0, 0, targetW, targetH, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    RECT GetSnappedMonitorRect()
    {
        if (snappedHmon != IntPtr.Zero) return GetMonitorRectFromHandle(snappedHmon);
        return GetMonitorFromWindow(unityHWND);
    }

    RECT GetMonitorRectFromHandle(IntPtr hmon)
    {
        RECT fallback = GetVirtualScreenRect();
        if (hmon == IntPtr.Zero) return fallback;

        MONITORINFO mi = new MONITORINFO();
        mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
        if (!GetMonitorInfo(hmon, ref mi)) return fallback;
        return mi.rcMonitor;
    }

    RECT GetMonitorFromWindow(IntPtr hwnd)
    {
        RECT fallback = GetVirtualScreenRect();
        IntPtr hmon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (hmon == IntPtr.Zero) return fallback;

        MONITORINFO mi = new MONITORINFO();
        mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
        if (!GetMonitorInfo(hmon, ref mi)) return fallback;
        return mi.rcMonitor;
    }

    RECT GetVirtualScreenRect()
    {
        RECT r;
        r.Left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        r.Top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        r.Right = r.Left + GetSystemMetrics(SM_CXVIRTUALSCREEN);
        r.Bottom = r.Top + GetSystemMetrics(SM_CYVIRTUALSCREEN);
        return r;
    }

    bool GetUnityClientRect(out RECT r)
    {
        r = new RECT();
        if (!GetClientRect(unityHWND, out RECT client)) return false;

        POINT p;
        p.x = 0;
        p.y = 0;
        if (!ClientToScreen(unityHWND, ref p)) return false;

        r.Left = p.x;
        r.Top = p.y;
        r.Right = p.x + client.Right;
        r.Bottom = p.y + client.Bottom;
        return true;
    }

    void SetTopMost(bool on)
    {
        GetClipCursor(out RECT backup);
        SetWindowPos(unityHWND, on ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        ClipCursor(ref backup);
    }

    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }

    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] static extern bool GetClipCursor(out RECT lpRect);
    [DllImport("user32.dll")] static extern bool ClipCursor(ref RECT lpRect);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

    const uint MONITOR_DEFAULTTONEAREST = 2;

    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_NOACTIVATE = 0x0010;

    const int SM_CXVIRTUALSCREEN = 78;
    const int SM_CYVIRTUALSCREEN = 79;
    const int SM_XVIRTUALSCREEN = 76;
    const int SM_YVIRTUALSCREEN = 77;
#endif
}
