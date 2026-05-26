using System;
using UnityEngine;

[ExecuteAlways]
public class AvatarTaskbarController : MonoBehaviour
{
    [Header("Animator")]
    public Animator avatarAnimator;

    [Header("Detection Settings")]
    public Vector2 snapZoneOffset = new Vector2(0, -5);
    public Vector2 snapZoneSize = new Vector2(100, 10);

    [Header("Attach Settings")]
    public GameObject attachTarget;
    public HumanBodyBones attachBone = HumanBodyBones.Head;
    public bool keepOriginalRotation = false;

    [Header("Spawn / Despawn Animation")]
    public float spawnScaleTime = 0.2f;
    public float despawnScaleTime = 0.2f;

    [Header("Debug")]
    public bool showDebugGizmo = true;
    public Color taskbarGizmoColor = Color.green;
    public Color pinkZoneGizmoColor = Color.magenta;

    private IDesktopWindowApi windowApi;
    private Vector2 unityPos;
    private Rect taskbarRect;
    private Rect pinkZoneDesktopRect;

    private Animator animator;
    private Transform attachBoneTransform;
    private Transform originalAttachParent;

    private Vector3 originalScale = Vector3.one;
    private float scaleLerpT = 0f;
    private bool isScaling = false;
    private bool scalingUp = false;

    private bool wasAllowSpawn = false;


    private static readonly int IsSitting = Animator.StringToHash("isSitting");

    void Start()
    {
        windowApi = DesktopWindowApi.Current;
        windowApi.RefreshOwnWindow();
        animator = avatarAnimator ?? GetComponent<Animator>();

        if (attachTarget != null)
        {
            originalScale = attachTarget.transform.localScale;
            originalAttachParent = attachTarget.transform.parent;
            attachTarget.SetActive(false);
        }

        UpdateTaskbarRect();
    }

    public void SetAnimator(Animator newAnimator)
    {
        avatarAnimator = newAnimator;
    }

    void Update()
    {
        if (windowApi == null) windowApi = DesktopWindowApi.Current;
        if (!windowApi.IsSupported || !windowApi.RefreshOwnWindow() || animator == null) return;

        UpdateUnityWindowPosition();
        UpdateTaskbarRect();
        UpdatePinkZone();

        Rect topBar = new Rect(taskbarRect.x, taskbarRect.y, taskbarRect.width, 5);
        bool isNearTaskbar = pinkZoneDesktopRect.Overlaps(topBar);

        animator.SetBool(IsSitting, isNearTaskbar);

        bool allowSpawn = isNearTaskbar && animator
            .GetCurrentAnimatorStateInfo(0)
            .IsName("Sitting");

        if (attachBoneTransform == null && attachTarget != null)
            attachBoneTransform = animator.GetBoneTransform(attachBone);

        if (attachTarget != null)
        {
            if (allowSpawn && !keepOriginalRotation && attachBoneTransform != null)
                attachTarget.transform.SetParent(attachBoneTransform, false);
            else if (!allowSpawn && !keepOriginalRotation &&
                     attachTarget.transform.parent != originalAttachParent)
                attachTarget.transform.SetParent(originalAttachParent, false);
        }

        if (attachTarget != null && allowSpawn && !wasAllowSpawn)
        {
            attachTarget.SetActive(true);
            attachTarget.transform.localScale = Vector3.zero;
            scaleLerpT = 0f;
            scalingUp = true;
            isScaling = true;
        }

        if (attachTarget != null && !allowSpawn && attachTarget.activeSelf && (!isScaling || scalingUp))
        {
            scalingUp = false;
            isScaling = true;
            scaleLerpT = 0f;
        }

        if (attachTarget != null && isScaling && attachTarget.activeSelf)
        {
            float duration = scalingUp ? spawnScaleTime : despawnScaleTime;
            scaleLerpT += Time.deltaTime / Mathf.Max(duration, 0.0001f);
            float t = Mathf.Clamp01(scaleLerpT);
            Vector3 from = scalingUp ? Vector3.zero : originalScale;
            Vector3 to = scalingUp ? originalScale : Vector3.zero;
            attachTarget.transform.localScale = Vector3.Lerp(from, to, t);

            if (t >= 1f)
            {
                isScaling = false;
                if (!scalingUp)
                {
                    attachTarget.SetActive(false);
                    attachTarget.transform.localScale = originalScale;
                }
            }
        }

        if (attachTarget != null && attachTarget.activeSelf && keepOriginalRotation && attachBoneTransform != null)
            attachTarget.transform.position = attachBoneTransform.position;

        wasAllowSpawn = allowSpawn;
    }

    void UpdatePinkZone()
    {
        if (!windowApi.TryGetOwnWindowRect(out DesktopRect rect)) return;
        int unityWidth = rect.Width;
        int unityHeight = rect.Height;

        float centerX = unityPos.x + unityWidth / 2f + snapZoneOffset.x;
        float bottomY = unityPos.y + unityHeight + snapZoneOffset.y;


        pinkZoneDesktopRect = new Rect(centerX - snapZoneSize.x / 2f, bottomY, snapZoneSize.x, snapZoneSize.y);
    }

    void UpdateUnityWindowPosition()
    {
        if (!windowApi.TryGetOwnWindowRect(out DesktopRect rect)) return;
        unityPos = new Vector2(rect.Left, rect.Top);
    }

    void UpdateTaskbarRect()
    {
        DesktopRect rect = windowApi.GetTaskbarRectForOwnWindow();
        taskbarRect = rect.IsEmpty ? new Rect(0, 0, 0, 0) : rect.ToUnityRect();
    }



    void OnDrawGizmos()
    {
        if (!Application.isPlaying || !showDebugGizmo) return;
        float basePixel = 1000f;
        Rect bar = new Rect(taskbarRect.x, taskbarRect.y, taskbarRect.width, 5);
        Gizmos.color = taskbarGizmoColor;
        DrawDesktopRect(bar, basePixel);
        Gizmos.color = pinkZoneGizmoColor;
        DrawDesktopRect(pinkZoneDesktopRect, basePixel);
    }

    void DrawDesktopRect(Rect desktopRect, float basePixel)
    {
        float cx = desktopRect.x + desktopRect.width / 2f;
        float cy = desktopRect.y + desktopRect.height / 2f;
        int screenWidth = Display.main.systemWidth;
        int screenHeight = Display.main.systemHeight;

        float unityX = (cx - screenWidth / 2f) / basePixel;
        float unityY = -(cy - screenHeight / 2f) / basePixel;

        Vector3 worldPos = new Vector3(unityX, unityY, 0);
        Vector3 worldSize = new Vector3(desktopRect.width / basePixel, desktopRect.height / basePixel, 0);

        Gizmos.DrawWireCube(worldPos, worldSize);
    }
}
