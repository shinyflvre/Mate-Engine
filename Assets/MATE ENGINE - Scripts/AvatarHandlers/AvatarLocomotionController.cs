using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;

public sealed class AvatarLocomotionController : MonoBehaviour
{
    [Header("Enable")]
    public bool EnableLocomotion = true;

    [Header("Animator (optional, will auto-find)")]
    public Animator Animator;

    [Header("Debug")]
    public bool DebugTriggerWalkNow = false;

    [Header("Locomotion Timing")]
    [Range(0f, 60f)] public float Randomizer = 10f;
    [Range(10f, 4000f)] public float MinWalkCycle = 250f;
    [Range(10f, 4000f)] public float MaxWalkCycle = 550f;

    [Header("Window Movement")]
    [Range(0f, 10f)] public float WindowSpeed = 2f;

    [Header("Animator Wiring")]
    public string BaseLayerName = "Base Layer";
    public string BaseIdleStateName = "Idle";
    public string WalkLeftParam = "WalkLeft";
    public string WalkRightParam = "WalkRight";

    [Header("Optional")]
    public bool OnlyMoveWhenFocused = false;

    [Header("Avatar Bounds Based Blocking (Left/Right only)")]
    public bool UseAvatarBoundsBlocking = true;

    [Tooltip("Root transform whose child Renderers define the avatar bounds. If null, this GameObject is used.")]
    public Transform AvatarBoundsRoot;

    [Tooltip("Camera used to project avatar bounds into Unity pixels. If null, Camera.main is used.")]
    public Camera BoundsCamera;

    [Header("Blocking Box Fine Tuning (Unity pixels)")]
    [Min(0f)] public float BoundsInsetLeft = 0f;
    [Min(0f)] public float BoundsInsetRight = 0f;

    [Tooltip("If the avatar box is within this many Unity pixels to a screen edge, the next walk cycle is forced away from that edge.")]
    [Min(0f)] public float EdgeThresholdUnityPixels = 12f;

    [Header("Visual Debug (Game + Gizmos)")]
    public bool DrawBlockingDebug = true;
    [Range(0f, 1f)] public float DebugOverlayAlpha = 0.55f;

    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_NOACTIVATE = 0x0010;

    const uint GW_OWNER = 4;

    const uint MONITOR_DEFAULTTONEAREST = 2;

    const int SM_XVIRTUALSCREEN = 76;
    const int SM_CXVIRTUALSCREEN = 78;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)] static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")] static extern bool GetClipCursor(out RECT lpRect);
    [DllImport("user32.dll")] static extern bool ClipCursor(ref RECT lpRect);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    int _baseLayerIndex = 0;
    IntPtr _hwnd = IntPtr.Zero;

    bool _walking;
    int _dir;
    float _remainingPixels;
    float _nextDecisionTime;
    float _pauseUntil;

    float _nextAnimatorResolveTime;

    Renderer[] _boundsRenderers;
    float _nextBoundsResolveTime;

    int _forcedNextDir;

    bool _wasBaseIdle;

    static readonly Vector3[] _boundsCorners = new Vector3[8];

    void OnEnable()
    {
        Application.runInBackground = true;
        ResolveAnimatorSmart(true);
        CacheWindowHandle();
        ResolveBoundsRenderersSmart(true);
        ScheduleNextDecision(true);
        _wasBaseIdle = true;
    }

    void Update()
    {
#if !(UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN)
        return;
#else
        if (!EnableLocomotion)
        {
            StopWalking();
            return;
        }

        ResolveAnimatorSmart(false);
        ResolveBoundsRenderersSmart(false);

        if (Animator == null) return;

        if (_hwnd == IntPtr.Zero) CacheWindowHandle();
        if (_hwnd == IntPtr.Zero) return;

        if (OnlyMoveWhenFocused)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg != _hwnd)
            {
                if (_walking) StopWalking();
                return;
            }
        }

        if (DebugTriggerWalkNow)
        {
            DebugTriggerWalkNow = false;
            ForceStartWalk();
        }

        float t = Time.unscaledTime;

        bool baseIdle = IsBaseIdle();
        if (!baseIdle)
        {
            if (_walking) StopWalking();
            if (_wasBaseIdle)
            {
                _pauseUntil = t + UnityEngine.Random.Range(0.08f, 0.22f);
                _nextDecisionTime = t + UnityEngine.Random.Range(0.25f, 0.75f);
            }
            _wasBaseIdle = false;
            return;
        }

        if (!_wasBaseIdle)
        {
            _pauseUntil = t + UnityEngine.Random.Range(0.08f, 0.22f);
            _nextDecisionTime = Mathf.Max(_nextDecisionTime, t + UnityEngine.Random.Range(0.25f, 0.75f));
            _wasBaseIdle = true;
        }

        if (t < _pauseUntil)
            return;

        if (!_walking)
        {
            if (Randomizer <= 0f) return;

            if (t >= _nextDecisionTime)
                StartWalk(false);

            return;
        }

        StepWalk();
#endif
    }

    void OnGUI()
    {
        if (!DrawBlockingDebug) return;
        if (!UseAvatarBoundsBlocking) return;
        if (!EnableLocomotion) return;

        if (!TryGetBlockingInfo(out BlockingInfo bi))
            return;

        Color prev = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(DebugOverlayAlpha));

        float xL = bi.effectiveMinXUnity;
        float xR = bi.effectiveMaxXUnity;

        DrawVLine(xL, 0f, Screen.height, 2f);
        DrawVLine(xR, 0f, Screen.height, 2f);

        float thr = bi.edgeThresholdUnity;
        float xLT = Mathf.Clamp(thr, 0f, Screen.width);
        float xRT = Mathf.Clamp(Screen.width - thr, 0f, Screen.width);

        DrawVLine(xLT, 0f, Screen.height, 1f);
        DrawVLine(xRT, 0f, Screen.height, 1f);

        GUI.color = prev;
    }

    void DrawVLine(float x, float yTop, float height, float width)
    {
        Rect r = new Rect(x - (width * 0.5f), yTop, width, height);
        GUI.DrawTexture(r, Texture2D.whiteTexture);
    }

    void OnDrawGizmos()
    {
        if (!DrawBlockingDebug) return;
        if (!UseAvatarBoundsBlocking) return;

        Camera cam = BoundsCamera != null ? BoundsCamera : Camera.main;
        if (cam == null) return;

        if (!TryGetBlockingInfo(out BlockingInfo bi))
            return;

        float z = cam.nearClipPlane + 0.05f;

        Vector3 p0 = cam.ScreenToWorldPoint(new Vector3(bi.effectiveMinXUnity, 0f, z));
        Vector3 p1 = cam.ScreenToWorldPoint(new Vector3(bi.effectiveMinXUnity, Screen.height, z));
        Vector3 p2 = cam.ScreenToWorldPoint(new Vector3(bi.effectiveMaxXUnity, 0f, z));
        Vector3 p3 = cam.ScreenToWorldPoint(new Vector3(bi.effectiveMaxXUnity, Screen.height, z));

        Gizmos.color = new Color(1f, 1f, 1f, 0.75f);
        Gizmos.DrawLine(p0, p1);
        Gizmos.DrawLine(p2, p3);

        float thr = bi.edgeThresholdUnity;
        Vector3 t0 = cam.ScreenToWorldPoint(new Vector3(thr, 0f, z));
        Vector3 t1 = cam.ScreenToWorldPoint(new Vector3(thr, Screen.height, z));
        Vector3 t2 = cam.ScreenToWorldPoint(new Vector3(Screen.width - thr, 0f, z));
        Vector3 t3 = cam.ScreenToWorldPoint(new Vector3(Screen.width - thr, Screen.height, z));

        Gizmos.color = new Color(1f, 1f, 1f, 0.35f);
        Gizmos.DrawLine(t0, t1);
        Gizmos.DrawLine(t2, t3);
    }

    public void SetAnimator(Animator a)
    {
        Animator = a;
        RefreshLayerIndex();
    }

    void ResolveAnimatorSmart(bool immediate)
    {
        float t = Time.unscaledTime;
        if (!immediate && t < _nextAnimatorResolveTime) return;
        _nextAnimatorResolveTime = t + 0.75f;

        if (Animator != null && Animator.isActiveAndEnabled) return;

        Animator found = null;

        found = GetComponent<Animator>();
        if (found != null && found.isActiveAndEnabled)
        {
            Animator = found;
            RefreshLayerIndex();
            return;
        }

        var controller = FindFirstObjectByType<AvatarAnimatorController>();
        if (controller != null && controller.animator != null && controller.animator.isActiveAndEnabled)
        {
            Animator = controller.animator;
            RefreshLayerIndex();
            return;
        }

        var voice = FindFirstObjectByType<PetVoiceReactionHandler>();
        if (voice != null && voice.avatarAnimator != null && voice.avatarAnimator.isActiveAndEnabled)
        {
            Animator = voice.avatarAnimator;
            RefreshLayerIndex();
            return;
        }

        var bubble = FindFirstObjectByType<AvatarBubbleHandler>();
        if (bubble != null && bubble.avatarAnimator != null && bubble.avatarAnimator.isActiveAndEnabled)
        {
            Animator = bubble.avatarAnimator;
            RefreshLayerIndex();
            return;
        }

        var all = Resources.FindObjectsOfTypeAll<Animator>();
        for (int i = 0; i < all.Length; i++)
        {
            var a = all[i];
            if (a == null) continue;
            if (!a.isActiveAndEnabled) continue;
            if (!a.gameObject.activeInHierarchy) continue;
            if (a.runtimeAnimatorController == null) continue;
            found = a;
            break;
        }

        Animator = found;
        RefreshLayerIndex();
    }

    void ResolveBoundsRenderersSmart(bool immediate)
    {
        float t = Time.unscaledTime;
        if (!immediate && t < _nextBoundsResolveTime) return;
        _nextBoundsResolveTime = t + 1.0f;

        Transform root = AvatarBoundsRoot != null ? AvatarBoundsRoot : transform;
        if (root == null)
        {
            _boundsRenderers = null;
            return;
        }

        _boundsRenderers = root.GetComponentsInChildren<Renderer>(true);
    }

    void RefreshLayerIndex()
    {
        if (Animator == null) return;
        int idx = Animator.GetLayerIndex(BaseLayerName);
        _baseLayerIndex = idx >= 0 ? idx : 0;
    }

    bool IsBaseIdle()
    {
        AnimatorStateInfo s = Animator.GetCurrentAnimatorStateInfo(_baseLayerIndex);
        return s.IsName(BaseIdleStateName);
    }

    void ForceStartWalk()
    {
        StopWalking();
        _pauseUntil = 0f;
        _nextDecisionTime = 0f;
        StartWalk(true);
    }

    void StartWalk(bool forced)
    {
        float min = Mathf.Max(0f, MinWalkCycle);
        float max = Mathf.Max(min, MaxWalkCycle);
        if (max <= 0.01f)
        {
            StopWalking();
            ScheduleNextDecision(false);
            return;
        }

        int chosenDir = 0;

        if (_forcedNextDir != 0)
        {
            chosenDir = _forcedNextDir;
            _forcedNextDir = 0;
        }
        else
        {
            chosenDir = PickDirectionByEdges();
        }

        if (chosenDir == 0)
            chosenDir = UnityEngine.Random.value < 0.5f ? -1 : 1;

        _dir = chosenDir;
        _remainingPixels = UnityEngine.Random.Range(min, max);
        _walking = true;

        Animator.SetBool(WalkLeftParam, _dir < 0);
        Animator.SetBool(WalkRightParam, _dir > 0);
    }

    int PickDirectionByEdges()
    {
        if (!UseAvatarBoundsBlocking) return 0;

        if (!TryGetBlockingInfo(out BlockingInfo bi))
            return 0;

        float leftDist = bi.effectiveMinGlobalX - bi.monitorLeft;
        float rightDist = bi.monitorRight - bi.effectiveMaxGlobalX;

        if (leftDist <= bi.edgeThresholdGlobal && rightDist <= bi.edgeThresholdGlobal)
        {
            if (leftDist < rightDist) return 1;
            if (rightDist < leftDist) return -1;
            return UnityEngine.Random.value < 0.5f ? -1 : 1;
        }

        if (rightDist <= bi.edgeThresholdGlobal) return -1;
        if (leftDist <= bi.edgeThresholdGlobal) return 1;

        return 0;
    }

    void StepWalk()
    {
        if (!_walking) return;

        if (!GetWindowRect(_hwnd, out RECT r))
        {
            StopWalking();
            ScheduleNextDecision(false);
            return;
        }

        int w = r.Right - r.Left;

        if (!TryGetMonitorBounds(_hwnd, out int monitorLeft, out int monitorRight))
        {
            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            monitorLeft = vx;
            monitorRight = vx + vw;
        }

        int minX;
        int maxX;

        if (UseAvatarBoundsBlocking && TryGetBlockingInfo(out BlockingInfo bi))
        {
            minX = bi.minWindowX;
            maxX = bi.maxWindowX;
        }
        else
        {
            int minWinX = monitorLeft;
            int maxWinX = monitorRight - w;
            if (maxWinX < minWinX) maxWinX = minWinX;
            minX = minWinX;
            maxX = maxWinX;
        }

        float speedPxPerSecond = Mathf.Max(0f, WindowSpeed) * 100f;
        if (speedPxPerSecond <= 0.01f)
        {
            EndWalk();
            return;
        }

        float step = speedPxPerSecond * Time.unscaledDeltaTime;
        float move = Mathf.Min(step, _remainingPixels);

        int targetX = r.Left + Mathf.RoundToInt(move * _dir);
        int clampedX = Mathf.Clamp(targetX, minX, maxX);

        int actualMoved = Mathf.Abs(clampedX - r.Left);
        _remainingPixels -= actualMoved;

        int yKeep = r.Top;

        GetClipCursor(out RECT backup);
        bool ok = SetWindowPos(_hwnd, IntPtr.Zero, clampedX, yKeep, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        ClipCursor(ref backup);
        if (!ok)
        {
            StopWalking();
            ScheduleNextDecision(false);
            return;
        }

        if (actualMoved <= 0)
        {
            _remainingPixels = 0f;
            _forcedNextDir = -_dir;
        }

        if (_remainingPixels <= 0.01f)
            EndWalk();
    }

    void EndWalk()
    {
        StopWalking();
        _pauseUntil = Time.unscaledTime + UnityEngine.Random.Range(0.4f, 1.2f);
        ScheduleNextDecision(false);
    }

    void StopWalking()
    {
        if (Animator != null)
        {
            Animator.SetBool(WalkLeftParam, false);
            Animator.SetBool(WalkRightParam, false);
        }

        _walking = false;
        _remainingPixels = 0f;
        _dir = 0;
    }

    void ScheduleNextDecision(bool immediate)
    {
        float t = Time.unscaledTime;

        if (immediate)
        {
            _nextDecisionTime = t + UnityEngine.Random.Range(0.2f, 0.8f);
            return;
        }

        float baseDelay = Mathf.Max(0.1f, Randomizer);
        _nextDecisionTime = t + UnityEngine.Random.Range(baseDelay, baseDelay * 2f);
    }

    void CacheWindowHandle()
    {
#if !(UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN)
        _hwnd = IntPtr.Zero;
#else
        uint pid = (uint)Process.GetCurrentProcess().Id;
        IntPtr best = IntPtr.Zero;
        long bestArea = -1;

        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero) return true;

            GetWindowThreadProcessId(hWnd, out uint wp);
            if (wp != pid) return true;

            int len = GetWindowTextLength(hWnd);
            if (len <= 0) return true;

            if (!GetWindowRect(hWnd, out RECT rr)) return true;

            long area = (long)(rr.Right - rr.Left) * (long)(rr.Bottom - rr.Top);
            if (area > bestArea)
            {
                bestArea = area;
                best = hWnd;
            }

            return true;
        }, IntPtr.Zero);

        _hwnd = best;
#endif
    }

    bool TryGetMonitorBounds(IntPtr hwnd, out int left, out int right)
    {
        left = 0;
        right = 0;

        IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (mon == IntPtr.Zero) return false;

        MONITORINFO mi = new MONITORINFO();
        mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));

        if (!GetMonitorInfo(mon, ref mi))
            return false;

        left = mi.rcMonitor.Left;
        right = mi.rcMonitor.Right;
        return true;
    }

    struct BlockingInfo
    {
        public int monitorLeft;
        public int monitorRight;
        public float effectiveMinXUnity;
        public float effectiveMaxXUnity;
        public int effectiveMinGlobalX;
        public int effectiveMaxGlobalX;
        public float edgeThresholdUnity;
        public int edgeThresholdGlobal;
        public int minWindowX;
        public int maxWindowX;
    }

    bool TryGetBlockingInfo(out BlockingInfo bi)
    {
        bi = default;

        if (_hwnd == IntPtr.Zero) return false;

        Camera cam = BoundsCamera != null ? BoundsCamera : Camera.main;
        if (cam == null) return false;

        if (!GetWindowRect(_hwnd, out RECT winRect))
            return false;

        if (!GetClientRect(_hwnd, out RECT clientRect))
            return false;

        POINT pt = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(_hwnd, ref pt))
            return false;

        int clientX = pt.X;

        int clientW = clientRect.Right - clientRect.Left;
        if (clientW <= 0) return false;
        if (Screen.width <= 0 || Screen.height <= 0) return false;

        float scaleX = clientW / (float)Screen.width;

        if (!TryGetMonitorBounds(_hwnd, out int monitorLeft, out int monitorRight))
        {
            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            monitorLeft = vx;
            monitorRight = vx + vw;
        }

        if (!TryGetAvatarScreenBoundsUnity(cam, out float minXU, out float maxXU))
        {
            minXU = 0f;
            maxXU = Screen.width;
        }

        float effMinXU = Mathf.Clamp(minXU + BoundsInsetLeft, 0f, Screen.width);
        float effMaxXU = Mathf.Clamp(maxXU - BoundsInsetRight, 0f, Screen.width);

        if (effMaxXU < effMinXU)
        {
            float mid = (effMinXU + effMaxXU) * 0.5f;
            effMinXU = mid;
            effMaxXU = mid;
        }

        int borderLeft = clientX - winRect.Left;

        int effMinGlobalX = clientX + Mathf.RoundToInt(effMinXU * scaleX);
        int effMaxGlobalX = clientX + Mathf.RoundToInt(effMaxXU * scaleX);

        int thrGlobal = Mathf.RoundToInt(EdgeThresholdUnityPixels * scaleX);

        int minWindowX = monitorLeft - borderLeft - Mathf.RoundToInt(effMinXU * scaleX);
        int maxWindowX = monitorRight - borderLeft - Mathf.RoundToInt(effMaxXU * scaleX);

        if (maxWindowX < minWindowX) maxWindowX = minWindowX;

        bi.monitorLeft = monitorLeft;
        bi.monitorRight = monitorRight;
        bi.effectiveMinXUnity = effMinXU;
        bi.effectiveMaxXUnity = effMaxXU;
        bi.effectiveMinGlobalX = effMinGlobalX;
        bi.effectiveMaxGlobalX = effMaxGlobalX;
        bi.edgeThresholdUnity = EdgeThresholdUnityPixels;
        bi.edgeThresholdGlobal = Mathf.Max(0, thrGlobal);
        bi.minWindowX = minWindowX;
        bi.maxWindowX = maxWindowX;

        return true;
    }

    bool TryGetAvatarScreenBoundsUnity(Camera cam, out float minX, out float maxX)
    {
        minX = float.PositiveInfinity;
        maxX = float.NegativeInfinity;

        if (_boundsRenderers == null || _boundsRenderers.Length == 0)
            return false;

        bool any = false;

        for (int i = 0; i < _boundsRenderers.Length; i++)
        {
            Renderer rr = _boundsRenderers[i];
            if (rr == null) continue;

            Bounds b = rr.bounds;
            Vector3 c = b.center;
            Vector3 e = b.extents;

            _boundsCorners[0] = new Vector3(c.x - e.x, c.y - e.y, c.z - e.z);
            _boundsCorners[1] = new Vector3(c.x - e.x, c.y - e.y, c.z + e.z);
            _boundsCorners[2] = new Vector3(c.x - e.x, c.y + e.y, c.z - e.z);
            _boundsCorners[3] = new Vector3(c.x - e.x, c.y + e.y, c.z + e.z);
            _boundsCorners[4] = new Vector3(c.x + e.x, c.y - e.y, c.z - e.z);
            _boundsCorners[5] = new Vector3(c.x + e.x, c.y - e.y, c.z + e.z);
            _boundsCorners[6] = new Vector3(c.x + e.x, c.y + e.y, c.z - e.z);
            _boundsCorners[7] = new Vector3(c.x + e.x, c.y + e.y, c.z + e.z);

            for (int k = 0; k < 8; k++)
            {
                Vector3 sp = cam.WorldToScreenPoint(_boundsCorners[k]);
                if (sp.z <= 0.0001f) continue;

                any = true;
                if (sp.x < minX) minX = sp.x;
                if (sp.x > maxX) maxX = sp.x;
            }
        }

        if (!any) return false;

        minX = Mathf.Clamp(minX, 0f, Screen.width);
        maxX = Mathf.Clamp(maxX, 0f, Screen.width);

        if (maxX < minX)
        {
            float mid = (minX + maxX) * 0.5f;
            minX = mid;
            maxX = mid;
        }

        return true;
    }
}