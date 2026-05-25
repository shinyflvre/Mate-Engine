using UnityEngine;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using NAudio.CoreAudioApi;
#endif
using System.Collections.Generic;
using System.Diagnostics;
using System.Collections;

public class AvatarAnimatorController : MonoBehaviour
{
    [Header("State Values")]
    public Animator animator;
    public float SOUND_THRESHOLD = 0.02f;
    public List<string> allowedApps = new();
    public int totalIdleAnimations = 10;
    public float IDLE_SWITCH_TIME = 12f, IDLE_TRANSITION_TIME = 3f;
    public int DANCE_CLIP_COUNT = 5;

    [Header("Dancing")]
    public bool enableDancing = true;           
    public bool enableDanceSwitch = true;
    public float DANCE_SWITCH_TIME = 15f;
    public float DANCE_TRANSITION_TIME = 2f;       

    public bool BlockDraggingOverride = false;

    [Header("Drag Hit Test")]
    public bool restrictDraggingToAvatarBounds = true;
    [Min(0)] public int dragHitPaddingPx = 12;

    private static readonly int danceIndexParam = Animator.StringToHash("DanceIndex");
    private static readonly int isIdleParam = Animator.StringToHash("isIdle");
    private static readonly int isDraggingParam = Animator.StringToHash("isDragging");
    private static readonly int isDancingParam = Animator.StringToHash("isDancing");
    private static readonly int idleIndexParam = Animator.StringToHash("IdleIndex");

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    private MMDevice defaultDevice;
    private MMDeviceEnumerator enumerator;
#endif
    private Coroutine soundCheckCoroutine, idleTransitionCoroutine, danceTransitionCoroutine;
    private float lastSoundCheckTime, idleTimer, danceTimer;
    private int idleState, danceState;
    private float dragLockTimer;
    private bool mouseHeld;
    private Camera dragCamera;
    private Renderer[] dragRenderers;
    private bool dragRenderersCached;
    public bool isDragging, isDancing, isIdle;

    [Header("Character Mode")]
    public bool enableHusbandoMode = false;
    private static readonly int isMaleParam = Animator.StringToHash("isMale");
    private static readonly int isFemaleParam = Animator.StringToHash("isFemale");


    void OnEnable()
    {
        animator ??= GetComponent<Animator>();
        Application.runInBackground = true;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        enumerator = new MMDeviceEnumerator();
        defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
#endif

        animator.SetFloat(isFemaleParam, enableHusbandoMode ? 0f : 1f);
        animator.SetFloat(isMaleParam, enableHusbandoMode ? 1f : 0f);

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        soundCheckCoroutine = StartCoroutine(CheckSoundContinuously());
#endif
    }

    void OnDisable() => CleanupAudioResources();
    void OnDestroy() => CleanupAudioResources();
    void OnApplicationQuit() => CleanupAudioResources();

    IEnumerator CheckSoundContinuously()
    {
        var wait = new WaitForSeconds(2f);
        while (true) { CheckForSound(); yield return wait; }
    }

    void CheckForSound()
    {
        if (MenuActions.IsMovementBlocked() || !enableDancing)
        {
            if (isDancing) SetDancing(false);
            return;
        }
#if !(UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN)
        return;
#else
        if (defaultDevice == null) return;
        if (!isDragging)
        {
            bool valid = IsValidAppPlaying();
            if (valid && !isDancing) StartDancing();
            else if (!valid && isDancing) SetDancing(false);
        }
#endif
    }

    void StartDancing()
    {
        isDancing = true;
        danceTimer = 0f;
        danceState = Random.Range(0, DANCE_CLIP_COUNT);
        animator.SetBool(isDancingParam, true);
        animator.SetFloat(danceIndexParam, danceState);
    }
    void SetDancing(bool value)
    {
        isDancing = value;
        animator.SetBool(isDancingParam, value);
        if (!value && danceTransitionCoroutine != null)
        {
            StopCoroutine(danceTransitionCoroutine);
            danceTransitionCoroutine = null;
        }
    }

    bool IsValidAppPlaying()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        if (Time.time - lastSoundCheckTime < 2f) return isDancing;
        lastSoundCheckTime = Time.time;
        try
        {
            defaultDevice?.Dispose();
            defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = defaultDevice.AudioSessionManager.Sessions;
            for (int i = 0, count = sessions.Count; i < count; i++)
            {
                var s = sessions[i];
                if (s.AudioMeterInformation.MasterPeakValue > SOUND_THRESHOLD)
                {
                    int pid = (int)s.GetProcessID;
                    if (pid == 0) continue;
                    try
                    {
                        string pname = Process.GetProcessById(pid)?.ProcessName;
                        if (string.IsNullOrEmpty(pname)) continue;
                        for (int j = 0; j < allowedApps.Count; j++)
                            if (pname.StartsWith(allowedApps[j], System.StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    catch { continue; }
                }
            }
        }
        catch { defaultDevice?.Dispose(); defaultDevice = null; }
        return false;
#else
        return false;
#endif
    }

    void Update()
    {
        animator.SetFloat(isFemaleParam, enableHusbandoMode ? 0f : 1f);
        animator.SetFloat(isMaleParam, enableHusbandoMode ? 1f : 0f);

        if (BlockDraggingOverride || MenuActions.IsMovementBlocked() || TutorialMenu.IsActive)
        {
            if (isDragging) SetDragging(false);
            if (isDancing) SetDancing(false);
            return;
        }
        if (Input.GetMouseButtonDown(0))
        {
            if (CanStartDragFromPointer())
            {
                SetDragging(true);
                mouseHeld = true;
                dragLockTimer = 0.30f;
                SetDancing(false);
            }
            else
            {
                mouseHeld = false;
            }
        }
        if (Input.GetMouseButtonUp(0)) mouseHeld = false;
        if (dragLockTimer > 0f)
        {
            dragLockTimer -= Time.deltaTime;
            animator.SetBool(isDraggingParam, true);
        }
        else if (!mouseHeld && isDragging) SetDragging(false);

        idleTimer += Time.deltaTime;
        if (idleTimer > IDLE_SWITCH_TIME)
        {
            idleTimer = 0f;
            int next = (idleState + 1) % totalIdleAnimations;
            if (next == 0) animator.SetFloat(idleIndexParam, 0);
            else
            {
                if (idleTransitionCoroutine != null) StopCoroutine(idleTransitionCoroutine);
                idleTransitionCoroutine = StartCoroutine(SmoothIdleTransition(next));
            }
            idleState = next;
        }
        UpdateIdleStatus();

        if (isDancing && enableDanceSwitch)
        {
            danceTimer += Time.deltaTime;
            if (danceTimer > DANCE_SWITCH_TIME)
            {
                danceTimer = 0f;
                int nextDance = (danceState + 1) % DANCE_CLIP_COUNT;
                if (nextDance == 0) animator.SetFloat(danceIndexParam, 0);
                else
                {
                    if (danceTransitionCoroutine != null) StopCoroutine(danceTransitionCoroutine);
                    danceTransitionCoroutine = StartCoroutine(SmoothDanceTransition(nextDance));
                }
                danceState = nextDance;
            }
        }
    }

    bool CanStartDragFromPointer()
    {
        return !restrictDraggingToAvatarBounds || IsPointerOverAvatar();
    }

    bool IsPointerOverAvatar()
    {
        Camera cam = GetDragCamera();
        if (cam == null) return true;

        CacheDragRenderers();
        if (dragRenderers == null || dragRenderers.Length == 0) return true;

        Vector3 mouse = Input.mousePosition;
        bool checkedAnyRenderer = false;
        for (int i = 0; i < dragRenderers.Length; i++)
        {
            Renderer r = dragRenderers[i];
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
            if (r is ParticleSystemRenderer) continue;

            Bounds bounds = r.bounds;
            Vector3 ext = bounds.extents;
            if (ext.sqrMagnitude <= 0.000001f) continue;

            checkedAnyRenderer = true;
            if (ScreenRectContainsRenderer(cam, bounds, mouse, dragHitPaddingPx)) return true;
        }

        return !checkedAnyRenderer;
    }

    Camera GetDragCamera()
    {
        if (dragCamera != null) return dragCamera;
        dragCamera = Camera.main;
        if (dragCamera == null) dragCamera = FindFirstObjectByType<Camera>();
        return dragCamera;
    }

    void CacheDragRenderers()
    {
        if (dragRenderersCached && dragRenderers != null && dragRenderers.Length > 0) return;
        dragRenderers = GetComponentsInChildren<Renderer>(true);
        dragRenderersCached = true;
    }

    static bool ScreenRectContainsRenderer(Camera cam, Bounds bounds, Vector3 mouse, float padding)
    {
        Vector3 center = bounds.center;
        Vector3 ext = bounds.extents;
        float minX = float.PositiveInfinity;
        float minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;
        bool projected = false;

        for (int x = -1; x <= 1; x += 2)
        {
            for (int y = -1; y <= 1; y += 2)
            {
                for (int z = -1; z <= 1; z += 2)
                {
                    Vector3 screen = cam.WorldToScreenPoint(center + Vector3.Scale(ext, new Vector3(x, y, z)));
                    if (screen.z <= 0.001f) continue;

                    projected = true;
                    minX = Mathf.Min(minX, screen.x);
                    minY = Mathf.Min(minY, screen.y);
                    maxX = Mathf.Max(maxX, screen.x);
                    maxY = Mathf.Max(maxY, screen.y);
                }
            }
        }

        if (!projected) return true;
        return mouse.x >= minX - padding &&
            mouse.x <= maxX + padding &&
            mouse.y >= minY - padding &&
            mouse.y <= maxY + padding;
    }

    void SetDragging(bool value)
    {
        isDragging = value;
        animator.SetBool(isDraggingParam, value);
    }

    void UpdateIdleStatus()
    {
        bool inIdle = animator.GetCurrentAnimatorStateInfo(0).IsName("Idle");
        if (isIdle != inIdle)
        {
            isIdle = inIdle;
            animator.SetBool(isIdleParam, isIdle);
        }
    }

    IEnumerator SmoothIdleTransition(int newIdle)
    {
        float elapsed = 0f, start = animator.GetFloat(idleIndexParam);
        while (elapsed < IDLE_TRANSITION_TIME)
        {
            elapsed += Time.deltaTime;
            animator.SetFloat(idleIndexParam, Mathf.Lerp(start, newIdle, elapsed / IDLE_TRANSITION_TIME));
            yield return null;
        }
        animator.SetFloat(idleIndexParam, newIdle);
    }

    IEnumerator SmoothDanceTransition(int newDance)
    {
        float elapsed = 0f, start = animator.GetFloat(danceIndexParam);
        while (elapsed < DANCE_TRANSITION_TIME)
        {
            elapsed += Time.deltaTime;
            animator.SetFloat(danceIndexParam, Mathf.Lerp(start, newDance, elapsed / DANCE_TRANSITION_TIME));
            yield return null;
        }
        animator.SetFloat(danceIndexParam, newDance);
    }

    public bool IsInIdleState() => isIdle;

    void CleanupAudioResources()
    {
        if (soundCheckCoroutine != null) { StopCoroutine(soundCheckCoroutine); soundCheckCoroutine = null; }
        if (idleTransitionCoroutine != null) { StopCoroutine(idleTransitionCoroutine); idleTransitionCoroutine = null; }
        if (danceTransitionCoroutine != null) { StopCoroutine(danceTransitionCoroutine); danceTransitionCoroutine = null; }
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        defaultDevice?.Dispose(); defaultDevice = null;
        enumerator?.Dispose(); enumerator = null;
#endif
    }
}
