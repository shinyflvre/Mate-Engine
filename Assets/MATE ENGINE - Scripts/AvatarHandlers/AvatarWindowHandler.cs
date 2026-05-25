using UnityEngine;
using System;
using System.Collections.Generic;
public class AvatarWindowHandler : MonoBehaviour
{
    [Header("Snap Safety")]
    public float minDragHoldSecondsToSit = 1f;
    public float unsnapCooldownSeconds = 0.3f;
    public float maxSnapMoveMonitorMultiplier = 1.5f;
    public float maxSnapMovePerFramePx = 640f;
    [Range(0f, 960f)] public float maxInitialSeatCorrectionPx = 360f;
    float _dragStartTime = -1f;
    bool _canSitHold;
    float _unsnapCooldownUntil = -1f;

    [Header("Snap Probe Offset")]
    public float probeZoneYOffsetLocal = 0f;
    Vector3 GetProbeWorld() => GetHipWorld() + transform.up * (probeZoneYOffsetLocal * transform.lossyScale.y);
    [Header("Snap Probe")]
    public float probeRadiusPx = 24f;
    public bool showProbeGizmo = true;
    public Color probeGizmoColor = Color.magenta;
    bool _guardZoneActive;
    Vector2 _guardCenterDesktop;
    [Header("Snap Guard Zone")]
    public bool useGuardZone = true;
    public float probeGuardPx = 240f;
    public Color probeGuardGizmoColor = Color.cyan;
    [Header("Sit Blockers")]
    public List<string> blockSitIfBoolTrue = new List<string>();
    readonly List<string> _blockSitValidNames = new List<string>();
    [Header("Window Sit BlendTree")]
    public int totalWindowSitAnimations = 4;
    static readonly int windowSitIndexParam = Animator.StringToHash("WindowSitIndex");
    bool wasSitting;
    [Header("Seat Alignment")]
    [Range(-256f, 256f)] public float seatOffsetPx = 0f;
    [Range(-0.05f, 0.05f)] public float windowSitYOffset = 0f;
    [Header("Occluder")]
    public Material occluderMaterial;
    public Camera targetCamera;
    public float targetQuadZOffset = 0.001f;
    public float othersQuadZOffset = 0.002f;
    public int maxOtherQuads = 12;
    [Header("Occluder Pool")]
    public bool precreateQuadsOnStart = true;
    public int prewarmOtherQuads = 6;
    [Header("Occluder Projection")]
    public bool forceScreenSpaceOccluders = false;
    public bool useSeatDepthTargetOccluder = true;
    public float screenSpaceTargetZOffset = 0.001f;
    public float screenSpaceOtherZOffset = 0.002f;
    public float targetSeatDepthBias = -0.06f;
    [Header("Target Quad Z Auto-Scale")]
    public bool autoScaleTargetZ = true;
    public float targetZBase = 3.2f;
    public float targetZRefScale = 1.0f;
    public float targetZSensitivity = 3.0f;
    public float targetZMin = 0.05f;
    public float targetZMax = 10f;
    [Header("Snap Smoothing")]
    public bool enableSnapSmoothing = true;
    [Range(0.01f, 0.5f)] public float snapSmoothingTime = 0.12f;
    public float snapSmoothingMaxSpeed = 6000f;
    [Header("Snap Diagnostics")]
    public bool logWindowSitDiagnostics = false;
    [Range(0.1f, 5f)] public float windowSitDiagnosticInterval = 0.5f;
    bool _snapSmoothingActive;
    float _snapVelX, _snapVelY;
    bool _havePrevSnapRect;
    DesktopRect _prevSnapRect;
    Vector3 _prevLossyScale;
    [Header("Snap Trigger")]
    public int minDragPixelsToSnap = 4;
    int _dragStartCursorX, _dragStartCursorY;
    [Header("Snap Guard")]
    public int snapGuardFrames = 8;
    public int snapLatchFrames = 18;
    public int unsnapVerticalBand = 16;
    public int sitUnsnapDragPixels = 24;
    [Header("Transparent-Window-Filter")]
    [Range(0, 255)] public int layeredAlphaIgnoreBelow = 230;
    public bool ignoreLayeredClickThrough = true;
    public bool ignoreLayeredToolOrNoActivate = true;
    public bool ignoreLayeredWithColorKey = true;
    [Header("Performance")]
    public float windowEnumFPS = 15f;
    public float windowEnumIdleFPS = 8f;

    float snapFraction;
    int _snapCursorY;
    bool wasDragging;
    bool _hasPendingSnap;
    DesktopWindowInfo _pendingSnapWindow;
    float _pendingSnapFraction;
    int _pendingSnapCursorY;
    IntPtr snappedWindowId = IntPtr.Zero;
    IDesktopWindowApi windowApi;
    Vector2 lastDesktopPosition;
    readonly List<DesktopWindowInfo> cachedWindows = new List<DesktopWindowInfo>(128);
    readonly List<DesktopWindowInfo> activeOccluders = new List<DesktopWindowInfo>(16);
    Animator animator;
    AvatarAnimatorController controller;
    Transform occluderRoot;
    GameObject targetQuadGO;
    Mesh targetMesh;
    readonly List<GameObject> otherQuadGOs = new List<GameObject>(16);
    readonly List<Mesh> otherMeshes = new List<Mesh>(16);
    Material _occluderSharedMat;
    int _guard;
    int _latch;
    float _nextEnumTime;
    DesktopRect _lastUnityCli;
    bool _haveUnityCli;
    bool _haveCurrentSnapRect;
    DesktopRect _currentSnapRect;
    bool _haveSnapOwnOffset;
    int _snapOwnOffsetX, _snapOwnOffsetY;
    int _snapOwnWidth, _snapOwnHeight;
    int _snapTargetLeftAtSnap, _snapTargetTopAtSnap;
    int _snapOwnLeftAtSnap, _snapOwnTopAtSnap;
    int _commandedOwnLeft, _commandedOwnTop;
    bool _haveCommandedOwnPosition;
    bool _logWindowSitDiagnosticsRuntime;
    float _nextWindowSitDiagnosticLogTime;
    bool _haveTargetQuadScreenRect;
    Rect _targetQuadScreenRect;
    int _targetQuadWindowWidth, _targetQuadWindowHeight;
    bool _haveTargetOccluderDepthOffset;
    float _targetOccluderDepthOffset;
    static readonly int[] TRI = { 0, 1, 2, 0, 2, 3 };
    readonly Vector3[] verts4 = new Vector3[4];
    readonly Vector3[] verts4Other = new Vector3[4];
    Transform boneHips, boneLUL, boneRUL, boneLFoot, boneRFoot, boneHead;
    SkinnedMeshRenderer[] skinned;
    bool _skinnedCached;
    bool seatCalibrated;
    Vector3 seatLocalAtSnap;
    Vector3 boundsMinSnapLocal;
    Vector3 boundsSizeSnapLocal;
    float seatNormY;
    int _lastSnapTopY;
    uint _currentPid;
    float _guardRadiusSq;
    void Start()
    {
        windowApi = DesktopWindowApi.Current;
        windowApi.RefreshOwnWindow();
        _currentPid = windowApi.CurrentProcessId;
        _logWindowSitDiagnosticsRuntime = HasWindowSitDebugArgument();
        animator = GetComponent<Animator>();
        controller = GetComponent<AvatarAnimatorController>();
        if (targetCamera == null) targetCamera = Camera.main;
        CacheRigRefs(); BuildBlockSitCache(); EnsureOccluderRoot();
        if (occluderMaterial != null) _occluderSharedMat = new Material(occluderMaterial);
        if (precreateQuadsOnStart)
        {
            EnsureTargetQuad();
            int pre = Mathf.Clamp(prewarmOtherQuads, 0, Mathf.Max(maxOtherQuads, 0));
            for (int i = 0; i < pre; i++) EnsureOtherQuad(i);
            SetTargetQuadActive(false); SetOtherQuadsActive(0);
        }
        SetTopMost(SaveLoadHandler.Instance != null ? SaveLoadHandler.Instance.data.isTopmost : true);
        _nextEnumTime = 0f;
        _prevLossyScale = transform.lossyScale;
        _lastSnapTopY = int.MinValue;
        cachedWindows.Capacity = Mathf.Max(cachedWindows.Capacity, 128);
        activeOccluders.Capacity = Mathf.Max(activeOccluders.Capacity, maxOtherQuads);
    }
    void OnDisable()
    {
        ClearSnapAndHide();
    }

    void OnDestroy()
    {
        CleanupOccluderArtifacts();
    }

    void BuildBlockSitCache()
    {
        _blockSitValidNames.Clear();
        if (animator == null || blockSitIfBoolTrue == null || blockSitIfBoolTrue.Count == 0) return;
        var wanted = new HashSet<string>(blockSitIfBoolTrue);
        var ps = animator.parameters;
        for (int i = 0; i < ps.Length; i++)
            if (ps[i].type == AnimatorControllerParameterType.Bool && wanted.Contains(ps[i].name))
                _blockSitValidNames.Add(ps[i].name);
    }
    bool IsSitBlocked()
    {
        if (animator == null || _blockSitValidNames.Count == 0) return false;
        for (int i = 0; i < _blockSitValidNames.Count; i++)
            if (animator.GetBool(_blockSitValidNames[i])) return true;
        return false;
    }
    void Update()
    {
        if (windowApi == null) windowApi = DesktopWindowApi.Current;
        if (!windowApi.IsSupported) return;

        if (snappedWindowId != IntPtr.Zero)
        {
            if ((transform.lossyScale - _prevLossyScale).sqrMagnitude > 1e-8f) { _snapSmoothingActive = false; _snapVelX = _snapVelY = 0f; }
            _prevLossyScale = transform.lossyScale;
        }

        if (!windowApi.RefreshOwnWindow() || animator == null || controller == null) return;
        if (!SaveLoadHandler.Instance.data.enableWindowSitting) { ClearPendingSnap(); ClearSnapAndHide(); return; }
        if (IsSitBlocked()) { ClearPendingSnap(); if (snappedWindowId != IntPtr.Zero) ClearSnapAndHide(); return; }

        bool isWindowSitNow = animator.GetBool("isWindowSit");
        if (isWindowSitNow && !wasSitting) animator.SetFloat(windowSitIndexParam, UnityEngine.Random.Range(0, totalWindowSitAnimations));
        wasSitting = isWindowSitNow;

        float enumHz = (controller.isDragging || snappedWindowId != IntPtr.Zero) ? Mathf.Max(1f, windowEnumFPS) : Mathf.Max(1f, windowEnumIdleFPS);
        if (Time.unscaledTime >= _nextEnumTime)
        {
            UpdateCachedWindows();
            if (snappedWindowId != IntPtr.Zero) RebuildActiveOccluders();
            _nextEnumTime = Time.unscaledTime + 1f / enumHz;
        }

        bool releasedThisFrame = wasDragging && !controller.isDragging;
        bool canCommitSnapOnRelease = releasedThisFrame && _canSitHold && DraggedPastSnapThreshold();

        if (controller.isDragging && !wasDragging)
        {
            if (windowApi.TryGetCursorPosition(out DesktopPoint cp))
            {
                _dragStartCursorX = cp.X; _dragStartCursorY = cp.Y;
                if (snappedWindowId != IntPtr.Zero && isWindowSitNow) _snapCursorY = cp.Y;
            }
            _dragStartTime = Time.unscaledTime;
            _canSitHold = false;
        }
        if (controller.isDragging)
        {
            if (!_canSitHold && _dragStartTime >= 0f && Time.unscaledTime - _dragStartTime >= minDragHoldSecondsToSit) _canSitHold = true;
        }
        else
        {
            _canSitHold = false;
            _dragStartTime = -1f;
        }

        if (snappedWindowId != IntPtr.Zero)
        {
            bool handled = false;
            for (int i = 0; i < cachedWindows.Count; i++)
            {
                var win = cachedWindows[i];
                if (win.Id != snappedWindowId) continue;
                if (windowApi.IsWindowMaximized(win.Id) || windowApi.IsWindowFullscreen(win)) { ClearSnapAndHide(); handled = true; break; }
            }
            if (!handled && (!windowApi.TryGetWindowRect(snappedWindowId, out DesktopRect liveRect) || liveRect.IsEmpty || windowApi.IsWindowMinimized(snappedWindowId))) { ClearSnapAndHide(); }
        }
        if (controller.isDragging)
        {
            if (snappedWindowId == IntPtr.Zero)
            {
                if (_canSitHold && DraggedPastSnapThreshold()) UpdatePendingSnapCandidate();
                else ClearPendingSnap();
            }
            else if (animator.GetBool("isWindowSit"))
            {
                if (DraggedPastSitUnsnapThreshold()) { SetGuardZoneFromCurrent(); ClearSnapAndHide(true); }
                else FollowSnapped(false);
            }
            else if (!IsStillNearSnappedWindow()) { SetGuardZoneFromCurrent(); ClearSnapAndHide(true); }
            else FollowSnapped(false);
        }
        else
        {
            bool committed = false;
            if (releasedThisFrame)
            {
                committed = canCommitSnapOnRelease && snappedWindowId == IntPtr.Zero && CommitPendingSnapOnRelease();
                if (!committed) ClearPendingSnap();
            }
            if (snappedWindowId != IntPtr.Zero && !committed) FollowSnapped(false);
        }
        if (animator.GetBool("isBigScreenAlarm"))
        {
            if (isWindowSitNow) animator.SetBool("isWindowSit", false);
            ClearSnapAndHide();
        }

        wasDragging = controller.isDragging;
    }
    void LateUpdate() { UpdateOccluderQuadsFrameSync(); }
    bool DraggedPastSnapThreshold()
    {
        if (!windowApi.TryGetCursorPosition(out DesktopPoint cp)) return true;
        return Mathf.Abs(cp.X - _dragStartCursorX) >= minDragPixelsToSnap || Mathf.Abs(cp.Y - _dragStartCursorY) >= minDragPixelsToSnap;
    }
    bool DraggedPastSitUnsnapThreshold()
    {
        if (!windowApi.TryGetCursorPosition(out DesktopPoint cp)) return false;
        int threshold = Mathf.Max(minDragPixelsToSnap, sitUnsnapDragPixels);
        return Mathf.Abs(cp.X - _dragStartCursorX) >= threshold || Mathf.Abs(cp.Y - _dragStartCursorY) >= threshold;
    }
    void SetGuardZoneFromCurrent()
    {
        if (!useGuardZone) return;
        if (ComputeZoneDesktop(out float gx, out float gy))
        {
            _guardCenterDesktop = new Vector2(gx, gy);
            _guardZoneActive = true;
            float r = ScaledGuardRadiusF();
            _guardRadiusSq = r * r;
        }
    }
    float ScaleFactor() => boneHips != null ? boneHips.lossyScale.magnitude : Mathf.Max(0.0001f, transform.lossyScale.magnitude);
    int ScaledProbeRadiusI() => Mathf.Max(1, Mathf.RoundToInt(probeRadiusPx * ScaleFactor()));
    int ScaledGuardRadiusI() => Mathf.Max(1, Mathf.RoundToInt(probeGuardPx * ScaleFactor()));
    float ScaledProbeRadiusF() => probeRadiusPx * ScaleFactor();
    float ScaledGuardRadiusF() => probeGuardPx * ScaleFactor();
    Vector3 GetHipWorld() => boneHips != null ? boneHips.position : transform.position;
    bool ComputeZoneDesktop(out float px, out float py) => ComputeDesktopFromWorld(GetProbeWorld(), out px, out py);
    bool ComputeSeatDesktop(out float px, out float py) => ComputeDesktopFromWorld(GetSeatWorldCurrent(), out px, out py);
    bool ComputeDesktopFromWorld(Vector3 wp, out float px, out float py)
    {
        px = py = 0f;
        if (targetCamera == null) return false;
        if (!GetUnityClientRect(out DesktopRect uCli)) return false;
        _haveUnityCli = true; _lastUnityCli = uCli;
        Vector3 sp = targetCamera.WorldToScreenPoint(wp);
        if (sp.z < 0.01f) return false;
        float clientW = Mathf.Max(1f, uCli.Right - uCli.Left);
        float clientH = Mathf.Max(1f, uCli.Bottom - uCli.Top);
        px = uCli.Left + sp.x * (clientW / Mathf.Max(1, targetCamera.pixelWidth));
        py = uCli.Top + (targetCamera.pixelHeight - sp.y) * (clientH / Mathf.Max(1, targetCamera.pixelHeight));
        return true;
    }
    void CacheRigRefs()
    {
        if (animator != null && animator.isHuman)
        {
            boneHips = animator.GetBoneTransform(HumanBodyBones.Hips);
            boneLUL = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            boneRUL = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            boneLFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            boneRFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
            boneHead = animator.GetBoneTransform(HumanBodyBones.Head);
        }
        if (!_skinnedCached)
        {
            skinned = GetComponentsInChildren<SkinnedMeshRenderer>(true);
            _skinnedCached = true;
        }
    }
    bool IsEffectivelyTransparentWindow(DesktopWindowInfo window)
    {
        return window.Alpha <= Mathf.Clamp01(layeredAlphaIgnoreBelow / 255f);
    }

    bool IsSameProcessWindow(DesktopWindowInfo window) => window.OwnerPid == _currentPid;

    void ClearSnapAndHide(bool fromUnsnap = false)
    {
        ClearPendingSnap();
        _havePrevSnapRect = false;
        _snapSmoothingActive = false;
        _snapVelX = _snapVelY = 0f;
        if (fromUnsnap) _unsnapCooldownUntil = Time.unscaledTime + Mathf.Max(0f, unsnapCooldownSeconds);
        snappedWindowId = IntPtr.Zero;
        seatCalibrated = false;
        _haveCurrentSnapRect = false;
        _haveSnapOwnOffset = false;
        _haveCommandedOwnPosition = false;
        _haveTargetQuadScreenRect = false;
        _haveTargetOccluderDepthOffset = false;
        if (animator != null) { animator.SetBool("isWindowSit", false); animator.SetBool("isTaskbarSit", false); }
        SetTopMost(SaveLoadHandler.Instance != null ? SaveLoadHandler.Instance.data.isTopmost : true);
        SetTargetQuadActive(false); SetOtherQuadsActive(0);
        _guard = _latch = 0;
        activeOccluders.Clear();
    }

    void ClearPendingSnap()
    {
        _hasPendingSnap = false;
        _pendingSnapWindow = default;
        _pendingSnapFraction = 0f;
        _pendingSnapCursorY = 0;
    }

    void UpdateCachedWindows()
    {
        cachedWindows.Clear();
        var windows = windowApi.EnumerateWindows();
        for (int i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            if (!window.IsValid || IsSameProcessWindow(window) || IsEffectivelyTransparentWindow(window)) continue;
            cachedWindows.Add(window);
        }
    }

    void RebuildActiveOccluders()
    {
        activeOccluders.Clear();
        int snappedIndex = -1;
        for (int i = 0; i < cachedWindows.Count; i++)
        {
            if (cachedWindows[i].Id == snappedWindowId)
            {
                snappedIndex = i;
                break;
            }
        }
        if (snappedIndex < 0) return;

        for (int i = 0; i < cachedWindows.Count && activeOccluders.Count < maxOtherQuads; i++)
        {
            var w = cachedWindows[i];
            if (w.Id == snappedWindowId || IsSameProcessWindow(w)) continue;
            if (IsEffectivelyTransparentWindow(w)) continue;
            if (!(w.IsTaskbarLike || i < snappedIndex)) continue;
            activeOccluders.Add(w);
        }
    }

    void UpdatePendingSnapCandidate()
    {
        if (TryFindSnapCandidate(out DesktopWindowInfo win, out float fraction, out int cursorY))
        {
            _hasPendingSnap = true;
            _pendingSnapWindow = win;
            _pendingSnapFraction = fraction;
            _pendingSnapCursorY = cursorY;
        }
        else ClearPendingSnap();
    }

    bool TryFindSnapCandidate(out DesktopWindowInfo candidate, out float fraction, out int cursorY)
    {
        candidate = default;
        fraction = 0f;
        cursorY = _dragStartCursorY;
        if (Time.unscaledTime < _unsnapCooldownUntil) return false;
        if (_guardZoneActive) _guardZoneActive = false;
        if (IsSitBlocked()) return false;
        if (useGuardZone && _guardZoneActive && ComputeZoneDesktop(out float gx, out float gy))
        {
            float dx = gx - _guardCenterDesktop.x;
            float dy = gy - _guardCenterDesktop.y;
            if (dx * dx + dy * dy < _guardRadiusSq) return false;
            _guardZoneActive = false;
        }
        if (!ComputeZoneDesktop(out float px, out float py)) return false;

        int spr = ScaledProbeRadiusI();
        float sprF = spr;

        for (int i = 0; i < cachedWindows.Count; i++)
        {
            var win = cachedWindows[i];
            int left = win.Rect.Left, right = win.Rect.Right, top = win.Rect.Top;
            if (!(px >= left && px <= right)) continue;
            if (Mathf.Abs(py - top) > sprF) continue;
            if (IsSameProcessWindow(win)) continue;
            if (IsOccludedByHigherWindowsAtPoint(win.Id, Mathf.RoundToInt(px), Mathf.RoundToInt(py))) continue;
            if (IsEffectivelyTransparentWindow(win)) continue;

            candidate = win;
            fraction = Mathf.Clamp01((px - left) / Mathf.Max(1, right - left));
            if (windowApi.TryGetCursorPosition(out DesktopPoint cp)) cursorY = cp.Y;
            return true;
        }
        return false;
    }

    bool CommitPendingSnapOnRelease()
    {
        DesktopWindowInfo win;
        float fraction;
        int cursorY;
        if (_hasPendingSnap)
        {
            win = _pendingSnapWindow;
            fraction = _pendingSnapFraction;
            cursorY = _pendingSnapCursorY;
        }
        else if (!TryFindSnapCandidate(out win, out fraction, out cursorY))
        {
            return false;
        }

        ClearPendingSnap();
        if (!TryValidateReleaseSnapWindow(ref win, out float px, out float py))
        {
            if (!TryFindSnapCandidate(out win, out fraction, out cursorY)) return false;
            if (!TryValidateReleaseSnapWindow(ref win, out px, out py)) return false;
        }

        lastDesktopPosition = GetUnityWindowPosition();
        snappedWindowId = win.Id;
        _guardZoneActive = false;

        animator.SetBool("isWindowSit", true);
        animator.SetBool("isTaskbarSit", win.IsTaskbarLike);
        animator.Update(0f);

        snapFraction = Mathf.Clamp01((px - win.Rect.Left) / Mathf.Max(1, win.Rect.Width));
        if (float.IsNaN(snapFraction)) snapFraction = fraction;

        _lastSnapTopY = win.Rect.Top;
        SetTopMost(true);

        if (windowApi.TryGetCursorPosition(out DesktopPoint cp)) _snapCursorY = cp.Y;
        else _snapCursorY = cursorY;
        _guard = Mathf.Max(1, snapGuardFrames);
        _latch = Mathf.Max(1, snapLatchFrames);

        _snapSmoothingActive = false;
        _snapVelX = _snapVelY = 0f;
        _havePrevSnapRect = false;
        _haveTargetQuadScreenRect = false;
        _haveTargetOccluderDepthOffset = false;

        if (!CaptureSeatAnchor()) { ClearSnapAndHide(); return false; }

        RebuildActiveOccluders();
        if (!PlaceInitialSnap(win.Rect))
        {
            ClearSnapAndHide(true);
            return false;
        }
        _snapSmoothingActive = false;
        return snappedWindowId != IntPtr.Zero;
    }

    bool TryValidateReleaseSnapWindow(ref DesktopWindowInfo win, out float px, out float py)
    {
        px = py = 0f;
        if (!win.IsValid) return false;
        if (!windowApi.TryGetWindowRect(win.Id, out DesktopRect liveRect) || liveRect.IsEmpty) return false;
        win.Rect = liveRect;
        if (windowApi.IsWindowMaximized(win.Id) || windowApi.IsWindowFullscreen(win)) return false;
        if (!ComputeZoneDesktop(out px, out py)) return false;
        if (!IsNearWindowTop(win, px, py)) return false;
        return !IsOccludedByHigherWindowsAtPoint(win.Id, Mathf.RoundToInt(px), Mathf.RoundToInt(py));
    }
    void CancelSnapSmoothingIfTargetMoved(DesktopRect tr)
    {
        if (!_havePrevSnapRect) { _prevSnapRect = tr; _havePrevSnapRect = true; return; }
        if (tr.Left != _prevSnapRect.Left || tr.Top != _prevSnapRect.Top || tr.Right != _prevSnapRect.Right || tr.Bottom != _prevSnapRect.Bottom)
        {
            _snapSmoothingActive = false; _snapVelX = _snapVelY = 0f;
        }
        _prevSnapRect = tr;
    }
    bool CaptureSeatAnchor()
    {
        if (targetCamera == null) return false;
        Bounds localBounds = WorldBoundsToRootLocal(GetCombinedWorldBounds());
        boundsMinSnapLocal = localBounds.min;
        boundsSizeSnapLocal = localBounds.size;

        Vector3 guessL = transform.worldToLocalMatrix.MultiplyPoint3x4(SeatWorldGuess());
        seatLocalAtSnap = guessL;
        float denom = Mathf.Max(0.0001f, boundsSizeSnapLocal.y);
        seatNormY = Mathf.Clamp01((guessL.y - boundsMinSnapLocal.y) / denom);
        seatCalibrated = true;
        return true;
    }
    void FollowSnapped(bool dragging)
    {
        if (snappedWindowId == IntPtr.Zero || !windowApi.TryGetWindowRect(snappedWindowId, out DesktopRect tr)) { ClearSnapAndHide(); return; }
        _currentSnapRect = tr;
        _haveCurrentSnapRect = true;
        CancelSnapSmoothingIfTargetMoved(tr);
        FollowTargetByCachedOffset(tr); SetTopMost(true);
    }
    bool PlaceInitialSnap(DesktopRect r)
    {
        _currentSnapRect = r;
        _haveCurrentSnapRect = true;
        if (!ComputeSeatDesktop(out float px, out float py)) return false;
        int left = r.Left, right = r.Right, top = r.Top;
        float desiredPX = left + snapFraction * Mathf.Max(1, right - left);
        float desiredPY = top + seatOffsetPx;
        int dx = Mathf.RoundToInt(desiredPX - px);
        int dy = Mathf.RoundToInt(desiredPY - py);

        if (!windowApi.TryGetOwnWindowRect(out DesktopRect ur)) return false;
        int w = ur.Right - ur.Left, h = ur.Bottom - ur.Top;
        int rawDx = dx, rawDy = dy;
        bool skippedSeatCorrection = false;
        float maxInitialCorrection = Mathf.Max(0f, maxInitialSeatCorrectionPx);
        if (Mathf.Abs(dx) > maxInitialCorrection || Mathf.Abs(dy) > maxInitialCorrection)
        {
            skippedSeatCorrection = true;
            dx = 0;
            dy = 0;
            Debug.LogWarning(
                "[AvatarWindowHandler] Ignored large initial window sit correction " +
                $"({rawDx},{rawDy}) px. Keeping drop position and following the target window by offset.",
                this);
        }

        int targetX = ur.Left + dx, targetY = GetInitialOwnTopForSeatDelta(ur.Top, dy);
        if (IsUnsafeSnapMove(ur, r, targetX, targetY, w, h, true, out string unsafeReason))
        {
            Debug.LogWarning("[AvatarWindowHandler] Cancelled unsafe window sit move: " + unsafeReason);
            return false;
        }

        bool moved = dx == 0 && dy == 0;
        if (!moved) moved = windowApi.TryMoveOwnWindow(targetX, targetY, w, h, true);
        if (!moved) return false;

        _snapOwnOffsetX = targetX - r.Left;
        _snapOwnOffsetY = targetY - r.Top;
        _snapOwnWidth = Mathf.Max(1, w);
        _snapOwnHeight = Mathf.Max(1, h);
        _snapTargetLeftAtSnap = r.Left;
        _snapTargetTopAtSnap = r.Top;
        _snapOwnLeftAtSnap = targetX;
        _snapOwnTopAtSnap = targetY;
        _commandedOwnLeft = targetX;
        _commandedOwnTop = targetY;
        _haveCommandedOwnPosition = true;
        _haveSnapOwnOffset = true;
        _snapVelX = _snapVelY = 0f;
        CaptureTargetOccluderDepth();

        if (GetUnityClientRect(out DesktopRect clientRect))
        {
            LogWindowSitDiagnostic("commit",
                $"target={RectToLog(r)} ownBefore={RectToLog(ur)} client={RectToLog(clientRect)} " +
                $"camera={CameraToLog()} seat=({px:F1},{py:F1}) desired=({desiredPX:F1},{desiredPY:F1}) " +
                $"delta=({dx},{dy}) rawDelta=({rawDx},{rawDy}) skippedSeatCorrection={skippedSeatCorrection} " +
                $"command=({targetX},{targetY},{w},{h}) offset=({_snapOwnOffsetX},{_snapOwnOffsetY}) " +
                $"originTarget=({_snapTargetLeftAtSnap},{_snapTargetTopAtSnap}) originOwn=({_snapOwnLeftAtSnap},{_snapOwnTopAtSnap})");
        }
        else
        {
            LogWindowSitDiagnostic("commit",
                $"target={RectToLog(r)} ownBefore={RectToLog(ur)} client=<none> camera={CameraToLog()} " +
                $"seat=({px:F1},{py:F1}) desired=({desiredPX:F1},{desiredPY:F1}) " +
                $"delta=({dx},{dy}) rawDelta=({rawDx},{rawDy}) skippedSeatCorrection={skippedSeatCorrection} " +
                $"command=({targetX},{targetY},{w},{h}) offset=({_snapOwnOffsetX},{_snapOwnOffsetY}) " +
                $"originTarget=({_snapTargetLeftAtSnap},{_snapTargetTopAtSnap}) originOwn=({_snapOwnLeftAtSnap},{_snapOwnTopAtSnap})");
        }

        return true;
    }

    void FollowTargetByCachedOffset(DesktopRect r)
    {
        if (!_haveSnapOwnOffset)
        {
            if (!PlaceInitialSnap(r)) ClearSnapAndHide(true);
            return;
        }

        int targetX = _snapOwnLeftAtSnap + (r.Left - _snapTargetLeftAtSnap);
        int targetY = GetFollowOwnTopForTarget(r);
        int w = Mathf.Max(1, _snapOwnWidth);
        int h = Mathf.Max(1, _snapOwnHeight);
        int currentX = _haveCommandedOwnPosition ? _commandedOwnLeft : targetX;
        int currentY = _haveCommandedOwnPosition ? _commandedOwnTop : targetY;
        var commandedRect = new DesktopRect(currentX, currentY, currentX + w, currentY + h);

        if (IsUnsafeSnapMove(commandedRect, r, targetX, targetY, w, h, false, out string unsafeReason))
        {
            Debug.LogWarning("[AvatarWindowHandler] Cancelled unsafe window sit follow: " + unsafeReason);
            ClearSnapAndHide(true);
            return;
        }

        int nx = targetX;
        int ny = targetY;
        if (_snapSmoothingActive && enableSnapSmoothing)
        {
            float dt = Time.unscaledDeltaTime;
            nx = Mathf.RoundToInt(Mathf.SmoothDamp(currentX, targetX, ref _snapVelX, snapSmoothingTime, snapSmoothingMaxSpeed, dt));
            ny = Mathf.RoundToInt(Mathf.SmoothDamp(currentY, targetY, ref _snapVelY, snapSmoothingTime, snapSmoothingMaxSpeed, dt));
            if (Mathf.Abs(targetX - nx) <= 1 && Mathf.Abs(targetY - ny) <= 1)
            {
                nx = targetX;
                ny = targetY;
                _snapSmoothingActive = false;
                _snapVelX = _snapVelY = 0f;
            }
        }

        if (nx != currentX || ny != currentY)
        {
            if (windowApi.TryMoveOwnWindow(nx, ny, w, h, true))
            {
                _commandedOwnLeft = nx;
                _commandedOwnTop = ny;
                _haveCommandedOwnPosition = true;
            }
        }

        LogWindowSitDiagnosticThrottled("follow",
            $"target={RectToLog(r)} command=({nx},{ny},{w},{h}) desired=({targetX},{targetY}) " +
            $"offset=({_snapOwnOffsetX},{_snapOwnOffsetY}) originTarget=({_snapTargetLeftAtSnap},{_snapTargetTopAtSnap}) " +
            $"originOwn=({_snapOwnLeftAtSnap},{_snapOwnTopAtSnap}) current=({currentX},{currentY})");
    }

    int GetFollowOwnTopForTarget(DesktopRect targetRect)
    {
#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
        return _snapOwnTopAtSnap - (targetRect.Top - _snapTargetTopAtSnap);
#else
        return _snapOwnTopAtSnap + (targetRect.Top - _snapTargetTopAtSnap);
#endif
    }

    int GetInitialOwnTopForSeatDelta(int ownTop, int desktopSeatDeltaY)
    {
#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
        return ownTop - desktopSeatDeltaY;
#else
        return ownTop + desktopSeatDeltaY;
#endif
    }

    bool IsUnsafeSnapMove(DesktopRect ownRect, DesktopRect targetWindowRect, int targetX, int targetY, int width, int height, bool enforceStepGuard, out string reason)
    {
        reason = null;
        if (windowApi == null || ownRect.IsEmpty || targetWindowRect.IsEmpty) return false;
        DesktopRect monitor = windowApi.GetNearestMonitorRect(targetWindowRect);
        if (monitor.IsEmpty) return false;

        float multiplier = Mathf.Max(0.25f, maxSnapMoveMonitorMultiplier);
        float maxDeltaX = Mathf.Max(width, monitor.Width) * multiplier;
        float maxDeltaY = Mathf.Max(height, monitor.Height) * multiplier;
        int deltaX = targetX - ownRect.Left;
        int deltaY = targetY - ownRect.Top;
        float maxStep = Mathf.Max(1f, maxSnapMovePerFramePx);
        if (enforceStepGuard && (Mathf.Abs(deltaX) > maxStep || Mathf.Abs(deltaY) > maxStep))
        {
            reason = $"delta=({deltaX},{deltaY}) exceeds per-frame guard {Mathf.RoundToInt(maxStep)}";
            return true;
        }

        if (Mathf.Abs(deltaX) > maxDeltaX || Mathf.Abs(deltaY) > maxDeltaY)
        {
            reason = $"delta=({deltaX},{deltaY}) exceeds monitor guard ({Mathf.RoundToInt(maxDeltaX)},{Mathf.RoundToInt(maxDeltaY)})";
            return true;
        }

#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
        return false;
#else
        DesktopRect targetOwnRect = new DesktopRect(targetX, targetY, targetX + width, targetY + height);
        DesktopRect expandedMonitor = new DesktopRect(monitor.Left - width, monitor.Top - height, monitor.Right + width, monitor.Bottom + height);
        if (!targetOwnRect.Intersects(expandedMonitor))
        {
            reason = $"target rect {targetOwnRect.Left},{targetOwnRect.Top},{targetOwnRect.Right},{targetOwnRect.Bottom} is outside monitor guard";
            return true;
        }
        return false;
#endif
    }

    bool ShouldLogWindowSitDiagnostics() => logWindowSitDiagnostics || _logWindowSitDiagnosticsRuntime;

    bool HasWindowSitDebugArgument()
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--window-sit-debug", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(arg, "-windowSitDebug", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    void LogWindowSitDiagnostic(string phase, string message)
    {
        if (!ShouldLogWindowSitDiagnostics()) return;
        Debug.Log("[AvatarWindowHandler][WindowSit] " + phase + " " + message, this);
    }

    void LogWindowSitDiagnosticThrottled(string phase, string message)
    {
        if (!ShouldLogWindowSitDiagnostics()) return;
        if (Time.unscaledTime < _nextWindowSitDiagnosticLogTime) return;
        _nextWindowSitDiagnosticLogTime = Time.unscaledTime + Mathf.Max(0.1f, windowSitDiagnosticInterval);
        LogWindowSitDiagnostic(phase, message);
    }

    string RectToLog(DesktopRect rect) => $"({rect.Left},{rect.Top},{rect.Right},{rect.Bottom} {rect.Width}x{rect.Height})";

    string CameraToLog()
    {
        if (targetCamera == null) return "<none>";
        return $"{targetCamera.pixelWidth}x{targetCamera.pixelHeight}";
    }
    bool IsStillNearSnappedWindow()
    {
        if (_latch > 0) { _latch--; return true; }
        if (_guard > 0) { _guard--; return true; }
        if (IsClickHoldWithoutDrag()) return true;

        for (int i = 0; i < cachedWindows.Count; i++)
        {
            var win = cachedWindows[i];
            if (win.Id != snappedWindowId) continue;
            if (windowApi.TryGetWindowRect(win.Id, out DesktopRect liveRect)) win.Rect = liveRect;
            return IsStillNearWindowTop(win);
        }
        if (windowApi.TryGetWindowRect(snappedWindowId, out DesktopRect tr))
            return IsStillNearWindowTop(new DesktopWindowInfo { Id = snappedWindowId, Rect = tr });
        return false;
    }

    bool IsStillNearWindowTop(DesktopWindowInfo win)
    {
        bool sitting = animator != null && animator.GetBool("isWindowSit");
        bool useSeatAnchor = sitting && seatCalibrated;
        if (useSeatAnchor)
        {
            if (!ComputeSeatDesktop(out float pxSeat, out float pySeat)) return true;
            return IsNearWindowTop(win, pxSeat, pySeat) && IsDragStillVerticallyNearSnap();
        }

        if (!ComputeZoneDesktop(out float px, out float py)) return true;
        if (!IsNearWindowTop(win, px, py)) return false;
        return IsDragStillVerticallyNearSnap();
    }

    bool IsClickHoldWithoutDrag()
    {
        if (controller == null || !controller.isDragging) return false;
        if (animator == null || !animator.GetBool("isWindowSit")) return false;
        if (!windowApi.TryGetCursorPosition(out DesktopPoint cp)) return true;
        int threshold = Mathf.Max(1, minDragPixelsToSnap);
        return Mathf.Abs(cp.X - _dragStartCursorX) < threshold && Mathf.Abs(cp.Y - _dragStartCursorY) < threshold;
    }

    bool IsNearWindowTop(DesktopWindowInfo win, float px, float py)
    {
        int left = win.Rect.Left, right = win.Rect.Right, top = win.Rect.Top;
        bool hitHoriz = px >= left && px <= right;
        bool hitVert = Mathf.Abs(py - top) <= Mathf.Max(unsnapVerticalBand, ScaledProbeRadiusI());
        return hitHoriz && hitVert;
    }

    bool IsDragStillVerticallyNearSnap()
    {
        if (controller == null || !controller.isDragging) return true;
        if (animator == null || !animator.GetBool("isWindowSit")) return true;
        if (!windowApi.TryGetCursorPosition(out DesktopPoint cp)) return true;
        int vBand = Mathf.Max(unsnapVerticalBand, ScaledProbeRadiusI());
        return Mathf.Abs(cp.Y - _snapCursorY) <= vBand;
    }
    bool IsOccludedByHigherWindowsAtPoint(IntPtr hwnd, int x, int y)
    {
        for (int i = 0; i < cachedWindows.Count; i++)
        {
            var window = cachedWindows[i];
            if (window.Id == hwnd) return false;
            if (!window.Rect.Contains(x, y)) continue;
            if (IsSameProcessWindow(window) || IsEffectivelyTransparentWindow(window)) continue;
            return true;
        }
        return windowApi.IsPointOccludedByHigherWindow(hwnd, x, y, w => IsSameProcessWindow(w) || IsEffectivelyTransparentWindow(w));
    }
    Vector3 GetSeatWorldCurrent()
    {
        if (!seatCalibrated) return GetHipWorld();
        float yFrac = Mathf.Clamp(seatNormY + windowSitYOffset, -0.5f, 1.5f);
        float yLocal = boundsMinSnapLocal.y + yFrac * boundsSizeSnapLocal.y;
        Vector3 localSeat = new Vector3(seatLocalAtSnap.x, yLocal, seatLocalAtSnap.z);
        return transform.localToWorldMatrix.MultiplyPoint3x4(localSeat);
    }
    Vector3 SeatWorldGuess()
    {
        if (animator != null && animator.isHuman)
        {
            Vector3 pelvis = boneHips != null ? boneHips.position : transform.position;
            Vector3 thighAvg = (boneLUL != null && boneRUL != null) ? (boneLUL.position + boneRUL.position) * 0.5f : pelvis;
            float headY = boneHead != null ? boneHead.position.y : pelvis.y + 0.5f;
            float footY = pelvis.y;
            if (boneLFoot != null) footY = boneLFoot.position.y;
            if (boneRFoot != null) footY = Mathf.Min(footY, boneRFoot.position.y);
            float h = Mathf.Max(0.1f, headY - footY);
            float down = Mathf.Clamp(h * 0.12f, 0.01f, h * 0.5f);
            return thighAvg + Vector3.down * down;
        }
        Bounds b = GetCombinedWorldBounds();
        return new Vector3(b.center.x, Mathf.Lerp(b.min.y, b.center.y, 0.2f), b.center.z);
    }
    Bounds GetCombinedWorldBounds()
    {
        Bounds b = new Bounds(transform.position, Vector3.zero);
        bool has = false;
        if (!_skinnedCached || skinned == null || skinned.Length == 0) { skinned = GetComponentsInChildren<SkinnedMeshRenderer>(true); _skinnedCached = true; }
        if (skinned != null)
        {
            for (int i = 0; i < skinned.Length; i++)
            {
                var s = skinned[i];
                if (s == null || !s.enabled) continue;
                if (!has) { b = s.bounds; has = true; } else b.Encapsulate(s.bounds);
            }
        }
        if (!has)
        {
            var rs = GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rs.Length; i++)
            {
                var r = rs[i];
                if (r == null || !r.enabled) continue;
                if (!has) { b = r.bounds; has = true; } else b.Encapsulate(r.bounds);
            }
        }
        if (!has) b = new Bounds(transform.position, Vector3.one * 0.5f);
        return b;
    }
    Bounds WorldBoundsToRootLocal(Bounds wb)
    {
        Matrix4x4 inv = transform.worldToLocalMatrix;
        Vector3 min = wb.min, max = wb.max;
        Vector3[] c = new Vector3[8];
        c[0] = inv.MultiplyPoint3x4(new Vector3(min.x, min.y, min.z));
        c[1] = inv.MultiplyPoint3x4(new Vector3(max.x, min.y, min.z));
        c[2] = inv.MultiplyPoint3x4(new Vector3(min.x, max.y, min.z));
        c[3] = inv.MultiplyPoint3x4(new Vector3(min.x, min.y, max.z));
        c[4] = inv.MultiplyPoint3x4(new Vector3(max.x, max.y, min.z));
        c[5] = inv.MultiplyPoint3x4(new Vector3(max.x, min.y, max.z));
        c[6] = inv.MultiplyPoint3x4(new Vector3(min.x, max.y, max.z));
        c[7] = inv.MultiplyPoint3x4(new Vector3(max.x, max.y, max.z));
        Vector3 lmin = c[0], lmax = c[0];
        for (int i = 1; i < 8; i++) { lmin = Vector3.Min(lmin, c[i]); lmax = Vector3.Max(lmax, c[i]); }
        return new Bounds((lmin + lmax) * 0.5f, lmax - lmin);
    }
    void UpdateOccluderQuadsFrameSync()
    {
        if (_occluderSharedMat == null || targetCamera == null || snappedWindowId == IntPtr.Zero) { SetTargetQuadActive(false); SetOtherQuadsActive(0); return; }
        if (!GetUnityClientRect(out _lastUnityCli)) { SetTargetQuadActive(false); SetOtherQuadsActive(0); return; }
        _haveUnityCli = true;

        DesktopRect uCli = _lastUnityCli;
        Rect unityClient = new Rect(uCli.Left, uCli.Top, uCli.Right - uCli.Left, uCli.Bottom - uCli.Top);

        DesktopRect tr;
        bool haveTargetRect = _haveCurrentSnapRect;
        if (haveTargetRect) tr = _currentSnapRect;
        else haveTargetRect = windowApi.TryGetWindowRect(snappedWindowId, out tr);

        if (haveTargetRect)
        {
            bool targetSizeChanged = !_haveTargetQuadScreenRect ||
                Mathf.Abs(tr.Width - _targetQuadWindowWidth) > 1 ||
                Mathf.Abs(tr.Height - _targetQuadWindowHeight) > 1;

            if (targetSizeChanged)
            {
                Rect tInter = Intersect(new Rect(tr.Left, tr.Top, tr.Right - tr.Left, tr.Bottom - tr.Top), unityClient);
                if (tInter.width > 0 && tInter.height > 0)
                {
                    _targetQuadScreenRect = DesktopToScreenRect(tInter, unityClient);
                    _targetQuadWindowWidth = tr.Width;
                    _targetQuadWindowHeight = tr.Height;
                    _haveTargetQuadScreenRect = true;
                }
                else
                {
                    _haveTargetQuadScreenRect = false;
                }
            }

            if (_haveTargetQuadScreenRect)
            {
                EnsureTargetQuad();
                float z = GetTargetOccluderZOffset();
                UpdateQuadScreenFast(_targetQuadScreenRect, z, targetMesh, targetQuadGO, verts4);
                SetTargetQuadActive(true);
            }
            else SetTargetQuadActive(false);
        }
        else SetTargetQuadActive(false);

        int outCount = 0;
        for (int i = 0; i < activeOccluders.Count && outCount < maxOtherQuads; i++)
        {
            var w = activeOccluders[i];
            DesktopRect wrct = w.Rect;
            if (wrct.IsEmpty) continue;
            Rect inter = Intersect(new Rect(wrct.Left, wrct.Top, wrct.Right - wrct.Left, wrct.Bottom - wrct.Top), unityClient);
            if (inter.width <= 0 || inter.height <= 0) continue;
            EnsureOtherQuad(outCount);
            UpdateQuadLocalFast(inter, unityClient, GetOtherOccluderZOffset(), otherMeshes[outCount], otherQuadGOs[outCount], verts4Other);
            outCount++;
        }
        SetOtherQuadsActive(outCount);
    }
    float GetTargetOccluderZOffset()
    {
        if (useSeatDepthTargetOccluder)
        {
            if (!_haveTargetOccluderDepthOffset) CaptureTargetOccluderDepth();
            if (_haveTargetOccluderDepthOffset) return _targetOccluderDepthOffset;
        }
        if (forceScreenSpaceOccluders) return Mathf.Max(0.0001f, screenSpaceTargetZOffset);
        return autoScaleTargetZ ? GetAutoTargetZ() : targetQuadZOffset;
    }
    float GetOtherOccluderZOffset()
    {
        if (forceScreenSpaceOccluders) return Mathf.Max(0.0001f, screenSpaceOtherZOffset);
        return othersQuadZOffset;
    }
    void CaptureTargetOccluderDepth()
    {
        _haveTargetOccluderDepthOffset = false;
        if (targetCamera == null) return;

        Vector3 seatScreen = targetCamera.WorldToScreenPoint(GetSeatWorldCurrent());
        if (seatScreen.z <= targetCamera.nearClipPlane) return;

        float minDepth = targetCamera.nearClipPlane + Mathf.Max(0.0001f, screenSpaceTargetZOffset);
        float depth = Mathf.Max(minDepth, seatScreen.z + targetSeatDepthBias);
        _targetOccluderDepthOffset = depth - targetCamera.nearClipPlane;
        _haveTargetOccluderDepthOffset = true;
    }
    float GetAutoTargetZ()
    {
        float s = Mathf.Max(0.0001f, transform.lossyScale.y);
        float z = targetZBase + (s - targetZRefScale) * targetZSensitivity;
        return Mathf.Clamp(z, targetZMin, targetZMax);
    }
    void EnsureOccluderRoot()
    {
        if (occluderRoot != null) return;
        var root = new GameObject("OccluderRoot");
        root.layer = targetCamera != null ? targetCamera.gameObject.layer : 0;
        root.transform.SetParent(targetCamera != null ? targetCamera.transform : null, false);
        occluderRoot = root.transform;
    }
    void EnsureTargetQuad()
    {
        if (targetQuadGO != null) return;
        targetQuadGO = new GameObject("TargetWindowQuad");
        targetQuadGO.layer = targetCamera.gameObject.layer;
        targetQuadGO.transform.SetParent(occluderRoot, false);
        var mf = targetQuadGO.AddComponent<MeshFilter>();
        var mr = targetQuadGO.AddComponent<MeshRenderer>();
        targetMesh = new Mesh(); targetMesh.MarkDynamic();
        mf.sharedMesh = targetMesh;
        mr.sharedMaterial = _occluderSharedMat;
        targetMesh.vertices = verts4;
        targetMesh.triangles = TRI;
        targetMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 10000f);
        targetQuadGO.SetActive(false);
    }
    void EnsureOtherQuad(int index)
    {
        while (otherQuadGOs.Count <= index)
        {
            var go = new GameObject("OtherWindowQuad_" + otherQuadGOs.Count);
            go.layer = targetCamera.gameObject.layer;
            go.transform.SetParent(occluderRoot, false);
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            var mesh = new Mesh(); mesh.MarkDynamic();
            mf.sharedMesh = mesh;
            mr.sharedMaterial = _occluderSharedMat;
            mesh.vertices = verts4Other;
            mesh.triangles = TRI;
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 10000f);
            otherQuadGOs.Add(go);
            otherMeshes.Add(mesh);
            go.SetActive(false);
        }
    }
    void SetTargetQuadActive(bool on) { if (targetQuadGO != null && targetQuadGO.activeSelf != on) targetQuadGO.SetActive(on); }
    void SetOtherQuadsActive(int activeCount)
    {
        for (int i = 0; i < otherQuadGOs.Count; i++)
        {
            bool on = i < activeCount;
            if (otherQuadGOs[i].activeSelf != on) otherQuadGOs[i].SetActive(on);
        }
    }
    void CleanupOccluderArtifacts()
    {
        if (targetQuadGO) { Destroy(targetQuadGO); targetQuadGO = null; targetMesh = null; }
        for (int i = 0; i < otherQuadGOs.Count; i++) if (otherQuadGOs[i]) Destroy(otherQuadGOs[i]);
        otherQuadGOs.Clear(); otherMeshes.Clear(); activeOccluders.Clear(); _haveUnityCli = false;
        if (_occluderSharedMat) { Destroy(_occluderSharedMat); _occluderSharedMat = null; }
    }
    void UpdateQuadLocalFast(Rect desktopRect, Rect unityDesktopRect, float zOffset, Mesh mesh, GameObject go, Vector3[] buffer)
    {
        UpdateQuadScreenFast(DesktopToScreenRect(desktopRect, unityDesktopRect), zOffset, mesh, go, buffer);
    }

    Rect DesktopToScreenRect(Rect desktopRect, Rect unityDesktopRect)
    {
        float clientW = Mathf.Max(1f, unityDesktopRect.width);
        float clientH = Mathf.Max(1f, unityDesktopRect.height);
        float pxW = Mathf.Max(1, targetCamera.pixelWidth);
        float pxH = Mathf.Max(1, targetCamera.pixelHeight);
        float sx0 = (desktopRect.xMin - unityDesktopRect.xMin) * (pxW / clientW);
        float sx1 = (desktopRect.xMax - unityDesktopRect.xMin) * (pxW / clientW);
        float sy0 = pxH - (desktopRect.yMax - unityDesktopRect.yMin) * (pxH / clientH);
        float sy1 = pxH - (desktopRect.yMin - unityDesktopRect.yMin) * (pxH / clientH);
        return Rect.MinMaxRect(sx0, sy0, sx1, sy1);
    }

    void UpdateQuadScreenFast(Rect screenRect, float zOffset, Mesh mesh, GameObject go, Vector3[] buffer)
    {
        float z = targetCamera.nearClipPlane + zOffset;

        Vector3 blW = targetCamera.ScreenToWorldPoint(new Vector3(screenRect.xMin, screenRect.yMin, z));
        Vector3 tlW = targetCamera.ScreenToWorldPoint(new Vector3(screenRect.xMin, screenRect.yMax, z));
        Vector3 trW = targetCamera.ScreenToWorldPoint(new Vector3(screenRect.xMax, screenRect.yMax, z));
        Vector3 brW = targetCamera.ScreenToWorldPoint(new Vector3(screenRect.xMax, screenRect.yMin, z));
        buffer[0] = targetCamera.transform.InverseTransformPoint(blW);
        buffer[1] = targetCamera.transform.InverseTransformPoint(tlW);
        buffer[2] = targetCamera.transform.InverseTransformPoint(trW);
        buffer[3] = targetCamera.transform.InverseTransformPoint(brW);

        mesh.vertices = buffer;
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;
    }
    static Rect Intersect(Rect a, Rect b)
    {
        float xMin = Mathf.Max(a.xMin, b.xMin);
        float yMin = Mathf.Max(a.yMin, b.yMin);
        float xMax = Mathf.Min(a.xMax, b.xMax);
        float yMax = Mathf.Max(Mathf.Min(a.yMax, b.yMax), yMin);
        if (xMax <= xMin || yMax <= yMin) return new Rect(0, 0, 0, 0);
        return new Rect(xMin, yMin, xMax - xMin, yMax - yMin);
    }
    Vector2 GetUnityWindowPosition()
    {
        if (windowApi == null) windowApi = DesktopWindowApi.Current;
        return windowApi.TryGetOwnWindowRect(out DesktopRect r) ? new Vector2(r.Left, r.Top) : Vector2.zero;
    }

    bool GetUnityClientRect(out DesktopRect r)
    {
        if (windowApi == null) windowApi = DesktopWindowApi.Current;
        return windowApi.TryGetOwnClientRect(out r);
    }

    void SetTopMost(bool en)
    {
        if (windowApi == null) windowApi = DesktopWindowApi.Current;
        windowApi.SetOwnTopmost(en);
    }

    public void ForceExitWindowSitting() { ClearSnapAndHide(); }

    void OnDrawGizmos()
    {
        if (!showProbeGizmo || targetCamera == null) return;
        Vector3 hip = GetProbeWorld();
        Vector3 sp = targetCamera.WorldToScreenPoint(hip);
        if (sp.z <= 0f) return;
        Vector3 sp2 = sp + new Vector3(ScaledProbeRadiusF(), 0f, 0f);
        Vector3 w1 = targetCamera.ScreenToWorldPoint(new Vector3(sp.x, sp.y, sp.z));
        Vector3 w2 = targetCamera.ScreenToWorldPoint(new Vector3(sp2.x, sp2.y, sp2.z));
        float worldR = Vector3.Distance(w1, w2);
        Gizmos.color = probeGizmoColor; Gizmos.DrawWireSphere(hip, worldR);
        Vector3 spg2 = sp + new Vector3(ScaledGuardRadiusF(), 0f, 0f);
        Vector3 wg2a = targetCamera.ScreenToWorldPoint(new Vector3(spg2.x, spg2.y, spg2.z));
        float worldRGuard = Vector3.Distance(w1, wg2a);
        Gizmos.color = probeGuardGizmoColor; Gizmos.DrawWireSphere(hip, worldRGuard);
    }
    public void SetBaseOffset(float v) { }
    public void SetBaseScale(float v) { }
    public float GetBaseOffset() => 0f; public float GetBaseScale() => 1f; public float GetScaleCompPx() => 0f;
}
