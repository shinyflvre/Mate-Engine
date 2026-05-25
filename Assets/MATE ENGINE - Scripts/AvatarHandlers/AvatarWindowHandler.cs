using UnityEngine;
using System;
using System.Collections.Generic;
public class AvatarWindowHandler : MonoBehaviour
{
    [Header("Snap Safety")]
    public float minDragHoldSecondsToSit = 1f;
    public float unsnapCooldownSeconds = 0.3f;
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
    bool _snapSmoothingActive;
    float _snapVelX, _snapVelY;
    bool _havePrevSnapRect;
    DesktopRect _prevSnapRect;
    Vector3 _prevLossyScale;
    [Header("Snap Trigger")]
    public int minDragPixelsToSnap = 4;
    int _dragStartCursorX, _dragStartCursorY;
    bool _postSettleRecalib;
    int _postSettleFrames;
    [Header("Snap Guard")]
    public int snapGuardFrames = 8;
    public int snapLatchFrames = 18;
    public int unsnapVerticalBand = 16;
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
    bool _recentUnsnap;
    int _lastSnapTopY;
    uint _currentPid;
    float _guardRadiusSq;
    void Start()
    {
        windowApi = DesktopWindowApi.Current;
        windowApi.RefreshOwnWindow();
        _currentPid = windowApi.CurrentProcessId;
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
        if (!SaveLoadHandler.Instance.data.enableWindowSitting) { ClearSnapAndHide(); return; }
        if (IsSitBlocked()) { if (snappedWindowId != IntPtr.Zero) ClearSnapAndHide(); return; }

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

        if (_recentUnsnap)
        {
            if (!controller.isDragging) _recentUnsnap = false;
            else if (ComputeZoneDesktop(out _, out float py))
            {
                int vBand = Mathf.Max(unsnapVerticalBand, ScaledProbeRadiusI());
                if (Mathf.Abs(py - _lastSnapTopY) >= vBand) _recentUnsnap = false;
            }
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
            if (!handled && (!windowApi.IsWindowAlive(snappedWindowId) || windowApi.IsWindowMinimized(snappedWindowId))) { ClearSnapAndHide(); }
        }
        if (controller.isDragging)
        {
            if (snappedWindowId == IntPtr.Zero) { if (_canSitHold && DraggedPastSnapThreshold()) TrySnap(); }
            else if (!IsStillNearSnappedWindow()) { SetGuardZoneFromCurrent(); ClearSnapAndHide(true); }
            else FollowSnapped(true);
        }
        else if (!controller.isDragging && snappedWindowId != IntPtr.Zero) FollowSnapped(false);
        if (animator.GetBool("isBigScreenAlarm"))
        {
            if (isWindowSitNow) animator.SetBool("isWindowSit", false);
            ClearSnapAndHide();
        }

        if (snappedWindowId != IntPtr.Zero && _postSettleRecalib)
        {
            if (_postSettleFrames > 0) _postSettleFrames--;
            else
            {
                if (windowApi.TryGetWindowRect(snappedWindowId, out DesktopRect tr))
                {
                    CalibrateSeatAnchorToDesktopY(tr.Top + seatOffsetPx);
                    if (ComputeSeatDesktop(out float px2, out _))
                    {
                        float w = Mathf.Max(1, tr.Right - tr.Left);
                        snapFraction = Mathf.Clamp01((px2 - tr.Left) / w);
                    }
                    _snapSmoothingActive = enableSnapSmoothing;
                    _snapVelX = _snapVelY = 0f;
                    _havePrevSnapRect = false;
                    PinToTarget(tr);
                }
                _postSettleRecalib = false;
            }
        }
        wasDragging = controller.isDragging;
    }
    void LateUpdate() { UpdateOccluderQuadsFrameSync(); }
    bool DraggedPastSnapThreshold()
    {
        if (!windowApi.TryGetCursorPosition(out DesktopPoint cp)) return true;
        return Mathf.Abs(cp.X - _dragStartCursorX) >= minDragPixelsToSnap || Mathf.Abs(cp.Y - _dragStartCursorY) >= minDragPixelsToSnap;
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
        px = uCli.Left + Mathf.Clamp(sp.x, 0, targetCamera.pixelWidth) * (clientW / Mathf.Max(1, targetCamera.pixelWidth));
        py = uCli.Top + (targetCamera.pixelHeight - Mathf.Clamp(sp.y, 0, targetCamera.pixelHeight)) * (clientH / Mathf.Max(1, targetCamera.pixelHeight));
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
        _havePrevSnapRect = false;
        _snapSmoothingActive = false;
        _snapVelX = _snapVelY = 0f;
        if (controller != null && controller.isDragging) _recentUnsnap = true;
        if (fromUnsnap) _unsnapCooldownUntil = Time.unscaledTime + Mathf.Max(0f, unsnapCooldownSeconds);
        snappedWindowId = IntPtr.Zero;
        seatCalibrated = false;
        if (animator != null) { animator.SetBool("isWindowSit", false); animator.SetBool("isTaskbarSit", false); }
        SetTopMost(SaveLoadHandler.Instance != null ? SaveLoadHandler.Instance.data.isTopmost : true);
        SetTargetQuadActive(false); SetOtherQuadsActive(0);
        _guard = _latch = 0;
        activeOccluders.Clear();
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
        for (int i = 0; i < cachedWindows.Count && activeOccluders.Count < maxOtherQuads; i++)
        {
            var w = cachedWindows[i];
            if (w.Id == snappedWindowId || IsSameProcessWindow(w)) continue;
            if (IsEffectivelyTransparentWindow(w)) continue;
            if (!(w.IsTaskbarLike || windowApi.IsAbove(w.Id, snappedWindowId))) continue;
            activeOccluders.Add(w);
        }
    }

    void TrySnap()
    {
        if (Time.unscaledTime < _unsnapCooldownUntil) return;
        if (IsSitBlocked()) return;
        if (useGuardZone && _guardZoneActive && ComputeZoneDesktop(out float gx, out float gy))
        {
            float dx = gx - _guardCenterDesktop.x;
            float dy = gy - _guardCenterDesktop.y;
            if (dx * dx + dy * dy < _guardRadiusSq) return;
            _guardZoneActive = false;
        }
        if (!ComputeZoneDesktop(out float px, out float py)) return;
        if (_recentUnsnap)
        {
            int vBlock = Mathf.Max(unsnapVerticalBand, ScaledProbeRadiusI());
            if (Mathf.Abs(py - _lastSnapTopY) < vBlock) return;
        }

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

            lastDesktopPosition = GetUnityWindowPosition();
            snappedWindowId = win.Id;
            _guardZoneActive = false;

            animator.SetBool("isWindowSit", true);
            animator.SetBool("isTaskbarSit", win.IsTaskbarLike);
            animator.Update(0f);
            CalibrateSeatAnchorToDesktopY(top + seatOffsetPx);

            _postSettleFrames = 1; _postSettleRecalib = true;

            if (ComputeSeatDesktop(out float px2, out _))
            {
                float w = Mathf.Max(1, right - left);
                snapFraction = Mathf.Clamp01((px2 - left) / w);
            }

            _lastSnapTopY = top;
            _recentUnsnap = false;
            SetTopMost(true);

            if (windowApi.TryGetCursorPosition(out DesktopPoint cp)) _snapCursorY = cp.Y;
            _guard = Mathf.Max(1, snapGuardFrames);
            _latch = Mathf.Max(1, snapLatchFrames);

            _snapSmoothingActive = enableSnapSmoothing;
            _snapVelX = _snapVelY = 0f;
            _havePrevSnapRect = false;

            RebuildActiveOccluders(); UpdateOccluderQuadsFrameSync();
            if (windowApi.TryGetWindowRect(win.Id, out DesktopRect tr)) PinToTarget(tr); else PinToTarget(win.Rect);
            return;
        }
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
    bool CalibrateSeatAnchorToDesktopY(float targetDesktopY)
    {
        if (targetCamera == null || !GetUnityClientRect(out DesktopRect uCli)) return false;

        Matrix4x4 inv = transform.worldToLocalMatrix;
        float yMinW = float.PositiveInfinity, yMaxW = float.NegativeInfinity;

        if (animator != null && animator.isHuman)
        {
            if (boneHead != null) { var p = boneHead.position.y; if (p < yMinW) yMinW = p; if (p > yMaxW) yMaxW = p; }
            if (boneHips != null) { var p = boneHips.position.y; if (p < yMinW) yMinW = p; if (p > yMaxW) yMaxW = p; }
            if (boneLUL != null) { var p = boneLUL.position.y; if (p < yMinW) yMinW = p; if (p > yMaxW) yMaxW = p; }
            if (boneRUL != null) { var p = boneRUL.position.y; if (p < yMinW) yMinW = p; if (p > yMaxW) yMaxW = p; }
            if (boneLFoot != null) { var p = boneLFoot.position.y; if (p < yMinW) yMinW = p; if (p > yMaxW) yMaxW = p; }
            if (boneRFoot != null) { var p = boneRFoot.position.y; if (p < yMinW) yMinW = p; if (p > yMaxW) yMaxW = p; }
        }
        float low, high;
        if (float.IsInfinity(yMinW) || float.IsInfinity(yMaxW))
        {
            Bounds lb = WorldBoundsToRootLocal(GetCombinedWorldBounds());
            float h = Mathf.Max(0.0001f, lb.size.y);
            low = lb.min.y - 0.5f * h - 0.25f;
            high = lb.max.y + 0.5f * h + 0.25f;
            boundsMinSnapLocal = lb.min;
            boundsSizeSnapLocal = lb.size;
        }
        else
        {
            Vector3 lmin = inv.MultiplyPoint3x4(new Vector3(transform.position.x, yMinW, transform.position.z));
            Vector3 lmax = inv.MultiplyPoint3x4(new Vector3(transform.position.x, yMaxW, transform.position.z));
            float ymin = Mathf.Min(lmin.y, lmax.y), ymax = Mathf.Max(lmin.y, lmax.y);
            float pad = Mathf.Max(0.05f, (ymax - ymin) * 0.2f);
            low = ymin - pad; high = ymax + pad;
            Bounds worldB = GetCombinedWorldBounds();
            Bounds localB = WorldBoundsToRootLocal(worldB);
            boundsMinSnapLocal = localB.min;
            boundsSizeSnapLocal = localB.size;
        }
        Vector3 guessL = transform.worldToLocalMatrix.MultiplyPoint3x4(SeatWorldGuess());
        float bestY = guessL.y, bestErr = float.MaxValue;

        for (int i = 0; i < 20; i++)
        {
            float mid = 0.5f * (low + high);
            Vector3 lp = new Vector3(guessL.x, mid, guessL.z);
            Vector3 sp = targetCamera.WorldToScreenPoint(transform.localToWorldMatrix.MultiplyPoint3x4(lp));
            if (sp.z < 0.01f) break;
            float clientH = Mathf.Max(1f, uCli.Bottom - uCli.Top);
            float py = uCli.Top + (targetCamera.pixelHeight - Mathf.Clamp(sp.y, 0, targetCamera.pixelHeight)) * (clientH / Mathf.Max(1, targetCamera.pixelHeight));
            float err = py - targetDesktopY;
            if (Mathf.Abs(err) < Mathf.Abs(bestErr)) { bestErr = err; bestY = mid; }
            if (err > 0f) high = mid; else low = mid;
        }
        seatLocalAtSnap = new Vector3(guessL.x, bestY, guessL.z);
        float denom = Mathf.Max(0.0001f, boundsSizeSnapLocal.y);
        seatNormY = Mathf.Clamp01((bestY - boundsMinSnapLocal.y) / denom);
        seatCalibrated = true;
        return true;
    }
    void FollowSnapped(bool dragging)
    {
        if (snappedWindowId == IntPtr.Zero || !windowApi.TryGetWindowRect(snappedWindowId, out DesktopRect tr)) { ClearSnapAndHide(); return; }
        CancelSnapSmoothingIfTargetMoved(tr);
        if (dragging && ComputeSeatDesktop(out float px, out _))
        {
            float ww = Mathf.Max(1, tr.Right - tr.Left);
            snapFraction = Mathf.Clamp01((px - tr.Left) / ww);
        }
        PinToTarget(tr); SetTopMost(true);
    }
    void PinToTarget(DesktopRect r)
    {
        if (!ComputeSeatDesktop(out float px, out float py)) return;
        int left = r.Left, right = r.Right, top = r.Top;
        float desiredPX = left + snapFraction * Mathf.Max(1, right - left);
        float desiredPY = top + seatOffsetPx;
        int dx = Mathf.RoundToInt(desiredPX - px);
        int dy = Mathf.RoundToInt(desiredPY - py);

        if (!windowApi.TryGetOwnWindowRect(out DesktopRect ur)) return;
        int w = ur.Right - ur.Left, h = ur.Bottom - ur.Top;
        int targetX = ur.Left + dx, targetY = ur.Top + dy;

        if (!_snapSmoothingActive || !enableSnapSmoothing)
        {
            if (dx != 0 || dy != 0) windowApi.TryMoveOwnWindow(targetX, targetY, w, h, true);
            return;
        }
        float dt = Time.unscaledDeltaTime;
        float nextX = Mathf.SmoothDamp(ur.Left, targetX, ref _snapVelX, snapSmoothingTime, snapSmoothingMaxSpeed, dt);
        float nextY = Mathf.SmoothDamp(ur.Top, targetY, ref _snapVelY, snapSmoothingTime, snapSmoothingMaxSpeed, dt);

        if (controller != null && controller.isDragging)
        {
            float predictedSeatY = py + (nextY - ur.Top);
            float afterError = predictedSeatY - desiredPY;
            if (afterError > 0f)
            {
                float maxStep = snapSmoothingMaxSpeed * dt;
                float need = Mathf.Max(0f, afterError - 1f);
                nextY -= Mathf.Min(maxStep, need);
            }
        }

        int nx = Mathf.RoundToInt(nextX), ny = Mathf.RoundToInt(nextY);
        if (Mathf.Abs(targetX - nx) <= 1 && Mathf.Abs(targetY - ny) <= 1) { nx = targetX; ny = targetY; _snapSmoothingActive = false; _snapVelX = _snapVelY = 0f; }
        if (nx != ur.Left || ny != ur.Top) windowApi.TryMoveOwnWindow(nx, ny, w, h, true);
    }
    bool IsStillNearSnappedWindow()
    {
        if (_latch > 0) { _latch--; return true; }
        if (_guard > 0) { _guard--; return true; }

        for (int i = 0; i < cachedWindows.Count; i++)
        {
            var win = cachedWindows[i];
            if (win.Id != snappedWindowId) continue;
            if (!ComputeZoneDesktop(out float px, out float py)) return true;
            int left = win.Rect.Left, right = win.Rect.Right, top = win.Rect.Top;
            bool hitHoriz = px >= left && px <= right;
            bool hitVert = Mathf.Abs(py - top) <= Mathf.Max(unsnapVerticalBand, ScaledProbeRadiusI());
            if (!hitHoriz || !hitVert) return false;

            if (controller.isDragging && animator.GetBool("isWindowSit"))
            {
                if (!windowApi.TryGetCursorPosition(out DesktopPoint cp)) return true;
                int vBand = Mathf.Max(unsnapVerticalBand, ScaledProbeRadiusI());
                if (Mathf.Abs(cp.Y - _snapCursorY) > vBand) return false;
            }
            return true;
        }
        return false;
    }
    bool IsOccludedByHigherWindowsAtPoint(IntPtr hwnd, int x, int y)
    {
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
        if (!_haveUnityCli && !GetUnityClientRect(out _lastUnityCli)) { SetTargetQuadActive(false); SetOtherQuadsActive(0); return; }

        DesktopRect uCli = _lastUnityCli;
        Rect unityClient = new Rect(uCli.Left, uCli.Top, uCli.Right - uCli.Left, uCli.Bottom - uCli.Top);

        if (windowApi.TryGetWindowRect(snappedWindowId, out DesktopRect tr))
        {
            Rect tInter = Intersect(new Rect(tr.Left, tr.Top, tr.Right - tr.Left, tr.Bottom - tr.Top), unityClient);
            if (tInter.width > 0 && tInter.height > 0)
            {
                EnsureTargetQuad();
                float z = autoScaleTargetZ ? GetAutoTargetZ() : targetQuadZOffset;
                UpdateQuadLocalFast(tInter, unityClient, z, targetMesh, targetQuadGO, verts4);
                SetTargetQuadActive(true);
            }
            else SetTargetQuadActive(false);
        }
        else SetTargetQuadActive(false);

        int outCount = 0;
        for (int i = 0; i < activeOccluders.Count && outCount < maxOtherQuads; i++)
        {
            var w = activeOccluders[i];
            if (!windowApi.TryGetWindowRect(w.Id, out DesktopRect wrct)) continue;
            Rect inter = Intersect(new Rect(wrct.Left, wrct.Top, wrct.Right - wrct.Left, wrct.Bottom - wrct.Top), unityClient);
            if (inter.width <= 0 || inter.height <= 0) continue;
            EnsureOtherQuad(outCount);
            UpdateQuadLocalFast(inter, unityClient, othersQuadZOffset, otherMeshes[outCount], otherQuadGOs[outCount], verts4Other);
            outCount++;
        }
        SetOtherQuadsActive(outCount);
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
        float clientW = Mathf.Max(1f, unityDesktopRect.width);
        float clientH = Mathf.Max(1f, unityDesktopRect.height);
        float pxW = Mathf.Max(1, targetCamera.pixelWidth);
        float pxH = Mathf.Max(1, targetCamera.pixelHeight);
        float sx0 = (desktopRect.xMin - unityDesktopRect.xMin) * (pxW / clientW);
        float sx1 = (desktopRect.xMax - unityDesktopRect.xMin) * (pxW / clientW);
        float sy0 = pxH - (desktopRect.yMax - unityDesktopRect.yMin) * (pxH / clientH);
        float sy1 = pxH - (desktopRect.yMin - unityDesktopRect.yMin) * (pxH / clientH);
        float z = targetCamera.nearClipPlane + zOffset;

        Vector3 blW = targetCamera.ScreenToWorldPoint(new Vector3(sx0, sy0, z));
        Vector3 tlW = targetCamera.ScreenToWorldPoint(new Vector3(sx0, sy1, z));
        Vector3 trW = targetCamera.ScreenToWorldPoint(new Vector3(sx1, sy1, z));
        Vector3 brW = targetCamera.ScreenToWorldPoint(new Vector3(sx1, sy0, z));
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
