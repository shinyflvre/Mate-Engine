using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

public class AvatarBigScreenHandler : MonoBehaviour
{
    [Header("Keybinds")]
    public List<KeyCode> ToggleKeys = new List<KeyCode> { KeyCode.B };

    [Header("Animator & Bone Selection")]
    public Animator avatarAnimator;
    public HumanBodyBones attachBone = HumanBodyBones.Head;

    [Header("Camera")]
    public Camera MainCamera;
    [Tooltip("Override for Zoom: Camera FOV (Perspective) or Size (Orthographic). 0 = auto.")]
    public float TargetZoom = 0f;
    public float ZoomMoveSpeed = 10f;
    [Tooltip("Y-Offset to bone position (meters, before scaling)")]
    public float YOffset = 0.08f;

    [Header("Fade Animation")]
    public float FadeYOffset = 0.5f;
    public float FadeInDuration = 0.5f;
    public float FadeOutDuration = 0.5f;

    [Header("Canvas Blocking")]
    public GameObject moveCanvas;

    private IntPtr unityHWND = IntPtr.Zero;
    private bool isBigScreenActive = false;
    private Vector3 originalCamPos;
    private Quaternion originalCamRot;
    private float originalFOV;
    private float originalOrthoSize;
    private RECT originalWindowRect;
    private bool originalRectSet = false;
    private Transform bone;
    private AvatarAnimatorController avatarAnimatorController;
    private bool moveCanvasWasActive = false;
    private Coroutine fadeCoroutine;
    private bool isFading = false;
    private bool isInDesktopTransition = false;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    [DllImport("user32.dll")]
    private static extern bool GetClipCursor(out RECT lpRect);
    [DllImport("user32.dll")]
    private static extern bool ClipCursor(ref RECT lpRect);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")]
    private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    public static List<AvatarBigScreenHandler> ActiveHandlers = new List<AvatarBigScreenHandler>();

    void OnEnable()
    {
        if (!ActiveHandlers.Contains(this))
            ActiveHandlers.Add(this);
    }
    void OnDisable()
    {
        ActiveHandlers.Remove(this);
    }

    public void ToggleBigScreenFromUI()
    {
        if (!isBigScreenActive)
            ActivateBigScreen();
        else
            DeactivateBigScreen();
    }

    void Start()
    {
        unityHWND = Process.GetCurrentProcess().MainWindowHandle;
        if (MainCamera == null) MainCamera = Camera.main;
        if (avatarAnimator == null) avatarAnimator = GetComponent<Animator>();
        if (MainCamera != null)
        {
            originalCamPos = MainCamera.transform.position;
            originalCamRot = MainCamera.transform.rotation;
            originalFOV = MainCamera.fieldOfView;
            originalOrthoSize = MainCamera.orthographicSize;
        }
        if (unityHWND != IntPtr.Zero && GetWindowRect(unityHWND, out RECT r))
        {
            originalWindowRect = r;
            originalRectSet = true;
        }
        avatarAnimatorController = GetComponent<AvatarAnimatorController>();
    }

    public void SetAnimator(Animator a) => avatarAnimator = a;

    void Update()
    {
        foreach (var key in ToggleKeys)
        {
            if (Input.GetKeyDown(key))
            {
                if (!isBigScreenActive && !isFading)
                    ActivateBigScreen();
                else if (isBigScreenActive && !isFading)
                    DeactivateBigScreen();
                break;
            }
        }
        if (isBigScreenActive && MainCamera != null && bone != null && avatarAnimator != null && !isFading && !isInDesktopTransition)
            UpdateBigScreenCamera();
    }

    void UpdateBigScreenCamera()
    {
        var scale = avatarAnimator.transform.lossyScale.y;
        var headPos = bone.position;
        var neck = avatarAnimator.GetBoneTransform(HumanBodyBones.Neck);
        float headHeight = Mathf.Max(0.12f, neck ? Mathf.Abs(headPos.y - neck.position.y) : 0.25f) * scale;
        float buffer = 1.4f;

        Vector3 camPos = originalCamPos;
        camPos.y = headPos.y + YOffset * scale;
        MainCamera.transform.position = camPos;
        MainCamera.transform.rotation = Quaternion.identity;

        if (TargetZoom > 0f)
        {
            if (MainCamera.orthographic) MainCamera.orthographicSize = TargetZoom * scale;
            else MainCamera.fieldOfView = TargetZoom;
        }
        else
        {
            if (MainCamera.orthographic)
                MainCamera.orthographicSize = headHeight * buffer;
            else
            {
                float dist = Mathf.Abs(MainCamera.transform.position.z - headPos.z);
                MainCamera.fieldOfView = Mathf.Clamp(
                    2f * Mathf.Atan((headHeight * buffer) / (2f * dist)) * Mathf.Rad2Deg, 10f, 60f);
            }
        }
    }

    void ActivateBigScreen()
    {
        if (isBigScreenActive) return;
        SaveCameraState();

        if (moveCanvas != null)
            moveCanvasWasActive = moveCanvas.activeSelf;

        isBigScreenActive = true;
        if (avatarAnimator != null) avatarAnimator.SetBool("isBigScreen", true);
        if (avatarAnimatorController != null) avatarAnimatorController.BlockDraggingOverride = true;
        if (moveCanvas != null && moveCanvas.activeSelf) moveCanvas.SetActive(false);

        bone = avatarAnimator ? avatarAnimator.GetBoneTransform(attachBone) : null;
        if (fadeCoroutine != null) StopCoroutine(fadeCoroutine);
        fadeCoroutine = StartCoroutine(BigScreenEnterSequence());
    }

    void DeactivateBigScreen()
    {
        if (fadeCoroutine != null) StopCoroutine(fadeCoroutine);
        fadeCoroutine = StartCoroutine(BigScreenExitSequence());
    }

    void SaveCameraState()
    {
        if (MainCamera != null)
        {
            originalCamPos = MainCamera.transform.position;
            originalCamRot = MainCamera.transform.rotation;
            originalFOV = MainCamera.fieldOfView;
            originalOrthoSize = MainCamera.orthographicSize;
        }
    }

    IEnumerator FadeCameraY(bool fadeIn)
    {
        isFading = true;
        if (avatarAnimator == null || bone == null || MainCamera == null)
        { isFading = false; yield break; }

        var scale = avatarAnimator.transform.lossyScale.y;
        var headPos = bone.position;
        float baseY = headPos.y + YOffset * scale;
        float fadeY = baseY + FadeYOffset;

        Vector3 camPos = MainCamera.transform.position;
        float fromY = fadeIn ? fadeY : baseY;
        float toY = fadeIn ? baseY : fadeY;
        float duration = fadeIn ? FadeInDuration : FadeOutDuration;
        float time = 0f;

        var neck = avatarAnimator.GetBoneTransform(HumanBodyBones.Neck);
        float headHeight = Mathf.Max(0.12f, neck ? Mathf.Abs(headPos.y - neck.position.y) : 0.25f) * scale;
        float buffer = 1.4f;

        while (time < duration)
        {
            float curve = Mathf.SmoothStep(0, 1, time / duration);
            camPos.y = Mathf.Lerp(fromY, toY, curve);
            MainCamera.transform.position = camPos;
            MainCamera.transform.rotation = Quaternion.identity;
            if (TargetZoom > 0f)
            {
                if (MainCamera.orthographic) MainCamera.orthographicSize = TargetZoom * scale;
                else MainCamera.fieldOfView = TargetZoom;
            }
            else
            {
                if (MainCamera.orthographic)
                    MainCamera.orthographicSize = headHeight * buffer;
                else
                {
                    float dist = Mathf.Abs(MainCamera.transform.position.z - headPos.z);
                    MainCamera.fieldOfView = Mathf.Clamp(
                        2f * Mathf.Atan((headHeight * buffer) / (2f * dist)) * Mathf.Rad2Deg, 10f, 60f);
                }
            }
            time += Time.deltaTime;
            yield return null;
        }

        camPos.y = toY;
        MainCamera.transform.position = camPos;
        MainCamera.transform.rotation = Quaternion.identity;
        if (TargetZoom > 0f)
        {
            if (MainCamera.orthographic) MainCamera.orthographicSize = TargetZoom * scale;
            else MainCamera.fieldOfView = TargetZoom;
        }
        else
        {
            if (MainCamera.orthographic)
                MainCamera.orthographicSize = headHeight * buffer;
            else
            {
                float dist = Mathf.Abs(MainCamera.transform.position.z - headPos.z);
                MainCamera.fieldOfView = Mathf.Clamp(
                    2f * Mathf.Atan((headHeight * buffer) / (2f * dist)) * Mathf.Rad2Deg, 10f, 60f);
            }
        }
        isFading = false;

        if (!fadeIn)
        {
            isBigScreenActive = false;
            if (avatarAnimator != null) avatarAnimator.SetBool("isBigScreen", false);
            if (avatarAnimatorController != null) avatarAnimatorController.BlockDraggingOverride = false;
            if (moveCanvas != null && moveCanvasWasActive) moveCanvas.SetActive(true);
            if (unityHWND != IntPtr.Zero && originalRectSet)
            {
                int w = originalWindowRect.right - originalWindowRect.left;
                int h = originalWindowRect.bottom - originalWindowRect.top;
                GetClipCursor(out RECT backup);
                MoveWindow(unityHWND, originalWindowRect.left, originalWindowRect.top, w, h, true);
                ClipCursor(ref backup);
            }
            if (MainCamera != null)
            {
                MainCamera.transform.position = originalCamPos;
                MainCamera.transform.rotation = originalCamRot;
                MainCamera.fieldOfView = originalFOV;
                MainCamera.orthographicSize = originalOrthoSize;
            }
        }
    }

    RECT FindBestMonitorRect(RECT windowRect)
    {
        List<RECT> monitorRects = new List<RECT>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr data) =>
        { monitorRects.Add(lprcMonitor); return true; }, IntPtr.Zero);
        int idx = 0, maxArea = 0;
        for (int i = 0; i < monitorRects.Count; i++)
        {
            int overlap = OverlapArea(windowRect, monitorRects[i]);
            if (overlap > maxArea) { idx = i; maxArea = overlap; }
        }
        return monitorRects.Count > 0 ? monitorRects[idx] : new RECT { left = 0, top = 0, right = Screen.currentResolution.width, bottom = Screen.currentResolution.height };
    }
    int OverlapArea(RECT a, RECT b)
    {
        int x1 = Math.Max(a.left, b.left), x2 = Math.Min(a.right, b.right);
        int y1 = Math.Max(a.top, b.top), y2 = Math.Min(a.bottom, b.bottom);
        int w = x2 - x1, h = y2 - y1;
        return (w > 0 && h > 0) ? w * h : 0;
    }

    IEnumerator GlideAvatarDesktop(float duration, bool toFadeY)
    {
        isInDesktopTransition = true;
        if (avatarAnimator == null || bone == null || MainCamera == null)
        { isInDesktopTransition = false; yield break; }

        var scale = avatarAnimator.transform.lossyScale.y;
        var headPos = bone.position;
        float baseY = headPos.y + YOffset * scale;
        float fadeY = baseY + FadeYOffset;

        Vector3 camPos = MainCamera.transform.position;
        float fromY = toFadeY ? baseY : fadeY;
        float toY = toFadeY ? fadeY : baseY;
        float time = 0f;

        while (time < duration)
        {
            camPos.y = Mathf.Lerp(fromY, toY, Mathf.SmoothStep(0, 1, time / duration));
            MainCamera.transform.position = camPos;
            time += Time.deltaTime;
            yield return null;
        }
        camPos.y = toY;
        MainCamera.transform.position = camPos;

        if (toFadeY && unityHWND != IntPtr.Zero)
        {
            if (GetWindowRect(unityHWND, out RECT windowRect))
            {
                RECT targetScreen = FindBestMonitorRect(windowRect);
                int sw = targetScreen.right - targetScreen.left, sh = targetScreen.bottom - targetScreen.top;
                GetClipCursor(out RECT backup);
                MoveWindow(unityHWND, targetScreen.left, targetScreen.top, sw, sh, true);
                ClipCursor(ref backup);
                originalWindowRect = windowRect; originalRectSet = true;
            }
        }
        if (!toFadeY && MainCamera != null)
        {
            MainCamera.transform.position = originalCamPos;
            MainCamera.transform.rotation = originalCamRot;
            MainCamera.fieldOfView = originalFOV;
            MainCamera.orthographicSize = originalOrthoSize;
        }
        isInDesktopTransition = false;
    }

    IEnumerator BigScreenEnterSequence()
    {
        yield return StartCoroutine(GlideAvatarDesktop(0.4f, true));
        yield return StartCoroutine(FadeCameraY(true));
    }
    IEnumerator BigScreenExitSequence()
    {
        yield return StartCoroutine(FadeCameraY(false));
        yield return StartCoroutine(GlideAvatarDesktop(0.4f, false));
    }
}
