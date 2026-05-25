using UnityEngine;
using System;
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
    IDesktopWindowApi windowApi;

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

    DesktopRect snappedMonitorRect;
    bool hasSnappedMonitor;

    int snapCompX;
    int calibRemaining;

    struct MonitorData
    {
        public IntPtr hmon;
        public DesktopRect rect;
    }

    void Start()
    {
        windowApi = DesktopWindowApi.Current;
        windowApi.RefreshOwnWindow();
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
        hasSnappedMonitor = false;
        unsnapCooldownUntil = -1f;
        SetTopMost(false);
        snapCompX = 0;
        calibRemaining = 0;
    }

    void Update()
    {
        if (windowApi == null) windowApi = DesktopWindowApi.Current;
        if (!windowApi.IsSupported || !windowApi.RefreshOwnWindow() || animator == null || controller == null) return;

        if (controller.isDragging && !wasDragging)
        {
            if (windowApi.TryGetOwnWindowRect(out DesktopRect wr) && windowApi.TryGetCursorPosition(out DesktopPoint cp))
            {
                dragBaseW = Math.Max(1, wr.Right - wr.Left);
                dragBaseH = Math.Max(1, wr.Bottom - wr.Top);
                cursorOffsetY = cp.Y - wr.Top;
                smoothingActive = false;
                velX = 0f;
                velY = 0f;
            }
        }

        EnsureSaneWindowSize();

        if (controller.isDragging)
        {
            if (!windowApi.TryGetCursorPosition(out DesktopPoint cp)) { wasDragging = controller.isDragging; return; }
            if (!windowApi.TryGetOwnWindowRect(out DesktopRect wrCur)) { wasDragging = controller.isDragging; return; }

            DesktopRect monWin = windowApi.GetMonitorRectForOwnWindow();

            bool allowLeftEdge;
            bool allowRightEdge;
            GetAllowedEdgesForMonitor(monWin, out allowLeftEdge, out allowRightEdge);

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
                    if (nearLeft) SnapTo(Side.Left, cp, monWin);
                    else if (nearRight) SnapTo(Side.Right, cp, monWin);
                }
            }
            else
            {
                if (Time.unscaledTime >= snappedAt + unsnapGraceTime)
                {
                    DesktopRect monSnap = GetSnappedMonitorRect();
                    int edgeX = GetBaseDesiredEdgeX(monSnap, snappedSide);
                    int thrUnsnap = Math.Max(1, unsnapThresholdPx);
                    if (Mathf.Abs(cp.X - edgeX) > thrUnsnap) Unsnap();
                }
            }

            if (snappedSide != Side.None)
            {
                if (!windowApi.TryGetOwnWindowRect(out DesktopRect wr2)) { wasDragging = controller.isDragging; return; }
                DesktopRect monNow = GetSnappedMonitorRect();

                int baseDesired = GetBaseDesiredEdgeX(monNow, snappedSide);
                ApplySnapCalibration(baseDesired);

                int desiredAnchorDesk = baseDesired + snapCompX;

                int anchorDesk = GetAnchorDesktopX(snappedSide);
                if (anchorDesk < 0) anchorDesk = wr2.Left + Math.Max(1, (wr2.Right - wr2.Left) / 2);
                int w = Math.Max(1, wr2.Right - wr2.Left);
                int anchorWinX = Mathf.Clamp(anchorDesk - wr2.Left, 0, w);

                int tx = desiredAnchorDesk - anchorWinX;
                int ty = cp.Y - cursorOffsetY;

                MoveSmooth(wr2.Left, wr2.Top, tx, ty);

                if (keepTopmostWhileSnapped) SetTopMost(true);
            }
        }
        else
        {
            if (snappedSide != Side.None)
            {
                if (!windowApi.TryGetOwnWindowRect(out DesktopRect wr)) return;
                DesktopRect mon = GetSnappedMonitorRect();

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
    }

    int GetBaseDesiredEdgeX(DesktopRect mon, Side side)
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

    void GetAllowedEdgesForMonitor(DesktopRect cur, out bool allowLeft, out bool allowRight)
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

        bool hasLeftNeighbor = false;
        bool hasRightNeighbor = false;

        int tol = Mathf.Max(0, adjacencyTolerancePx);
        int minOverlap = Mathf.Max(1, adjacencyMinVerticalOverlapPx);

        for (int i = 0; i < mons.Count; i++)
        {
            DesktopRect r = mons[i].rect;

            int overlap = VerticalOverlap(cur, r);
            if (overlap < minOverlap) continue;

            if (Mathf.Abs(r.Right - cur.Left) <= tol) hasLeftNeighbor = true;
            if (Mathf.Abs(r.Left - cur.Right) <= tol) hasRightNeighbor = true;

            if (hasLeftNeighbor && hasRightNeighbor) break;
        }

        allowLeft = !hasLeftNeighbor;
        allowRight = !hasRightNeighbor;
    }

    int VerticalOverlap(DesktopRect a, DesktopRect b)
    {
        int top = Math.Max(a.Top, b.Top);
        int bottom = Math.Min(a.Bottom, b.Bottom);
        return Math.Max(0, bottom - top);
    }

    List<MonitorData> GetAllMonitors()
    {
        List<MonitorData> list = new List<MonitorData>();
        var monitors = windowApi.GetMonitors();
        for (int i = 0; i < monitors.Count; i++)
            list.Add(new MonitorData { hmon = monitors[i].Id, rect = monitors[i].Rect });
        return list;
    }

    int GetAnchorDesktopX(Side side)
    {
        Transform t = side == Side.Left ? leftHand : rightHand;
        if (t == null || cam == null) return -1;
        if (!GetUnityClientRect(out DesktopRect uCli)) return -1;

        Vector3 sp = cam.WorldToScreenPoint(t.position);
        if (sp.z < 0.01f) return -1;

        float clientW = Mathf.Max(1f, uCli.Right - uCli.Left);
        float pxW = Mathf.Max(1, cam.pixelWidth);
        float sx = Mathf.Clamp(sp.x, 0, cam.pixelWidth) * (clientW / pxW);
        int desktopX = uCli.Left + Mathf.RoundToInt(sx);
        return desktopX;
    }

    void SnapTo(Side side, DesktopPoint cp, DesktopRect mon)
    {
        if (!windowApi.TryGetOwnWindowRect(out DesktopRect wr)) return;

        int w = Math.Max(1, wr.Right - wr.Left);
        int h = Math.Max(1, wr.Bottom - wr.Top);

        if (dragBaseW <= 0) dragBaseW = w;
        if (dragBaseH <= 0) dragBaseH = h;

        cursorOffsetY = cp.Y - wr.Top;
        snappedSide = side;
        snappedMonitorRect = mon;
        hasSnappedMonitor = true;

        snapCompX = 0;
        calibRemaining = Mathf.Clamp(snapCalibrationFrames, 0, 60);

        SetHide(side == Side.Left, side == Side.Right);

        int anchorDesk = GetAnchorDesktopX(side);
        if (anchorDesk < 0) anchorDesk = wr.Left + Math.Max(1, (wr.Right - wr.Left) / 2);
        int anchorWinX = Mathf.Clamp(anchorDesk - wr.Left, 0, w);

        int baseDesired = GetBaseDesiredEdgeX(mon, side);
        int tx = baseDesired - anchorWinX;
        int ty = cp.Y - cursorOffsetY;

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
        hasSnappedMonitor = false;
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
        windowApi.TryMoveOwnWindowPosition(x, y);
    }

    void EnsureSaneWindowSize()
    {
        if (!windowApi.TryGetOwnWindowRect(out DesktopRect wr)) return;
        DesktopRect vs = GetVirtualScreenRect();

        int w = Math.Max(1, wr.Right - wr.Left);
        int h = Math.Max(1, wr.Bottom - wr.Top);
        int vw = Math.Max(1, vs.Right - vs.Left);
        int vh = Math.Max(1, vs.Bottom - vs.Top);

        if (w <= vw && h <= vh) return;

        int targetW = dragBaseW > 0 ? Mathf.Clamp(dragBaseW, 1, vw) : Mathf.Clamp(w, 1, vw);
        int targetH = dragBaseH > 0 ? Mathf.Clamp(dragBaseH, 1, vh) : Mathf.Clamp(h, 1, vh);

        windowApi.TryMoveOwnWindow(wr.Left, wr.Top, targetW, targetH, true);
    }

    DesktopRect GetSnappedMonitorRect()
    {
        return hasSnappedMonitor ? snappedMonitorRect : windowApi.GetMonitorRectForOwnWindow();
    }

    DesktopRect GetVirtualScreenRect()
    {
        var monitors = windowApi.GetMonitors();
        if (monitors.Count == 0) return new DesktopRect(0, 0, Screen.currentResolution.width, Screen.currentResolution.height);
        DesktopRect r = monitors[0].Rect;
        for (int i = 1; i < monitors.Count; i++)
        {
            DesktopRect m = monitors[i].Rect;
            r = new DesktopRect(Math.Min(r.Left, m.Left), Math.Min(r.Top, m.Top), Math.Max(r.Right, m.Right), Math.Max(r.Bottom, m.Bottom));
        }
        return r;
    }

    bool GetUnityClientRect(out DesktopRect r)
    {
        return windowApi.TryGetOwnClientRect(out r);
    }

    void SetTopMost(bool on)
    {
        if (windowApi == null) windowApi = DesktopWindowApi.Current;
        windowApi.SetOwnTopmost(on);
    }
}
