using UnityEngine;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System.Runtime.InteropServices;
#endif

public class AvatarBigScreenScreenSaver : MonoBehaviour
{
    [Header("Enable BigScreen Screensaver Feature")]
    public bool enableBigScreenScreenSaver = false;

    [Header("Timeout Step (Slider 0-10, set by SettingsMenu)")]
    public int timeoutStep = 0;

    [Header("Mouse movement threshold (pixels)")]
    public float minMoveDistance = 2f;

    [Header("Allowed Animator States (Whitelist)")]
    public string[] allowedStates = { "Idle" };

    [Header("Click disables BigScreen completely")]
    public bool clickDisablesBoth = false;

    [Header("Live Status (Inspector)")]
    [SerializeField] private float inspectorTime;
    public string inspectorEvent;
    [SerializeField] private string inspectorTimeoutLabel;

    private static readonly int[] TimeoutSteps = { 30, 60, 300, 900, 1800, 2700, 3600, 5400, 7200, 9000, 10800 };
    private static readonly string[] TimeoutLabels = {
        "30s", "1 min", "5 min", "15 min", "30 min", "45 min", "1 h", "1.5 h", "2 h", "2.5 h", "3 h"
    };

    private AvatarBigScreenHandler bigScreenHandler;
    private Animator avatarAnimator;
    private Vector2 lastMousePos;
    private float idleTimer = 0f;

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }
#endif

    void Start()
    {
        bigScreenHandler = GetComponent<AvatarBigScreenHandler>();
        avatarAnimator = GetComponent<Animator>();
        lastMousePos = GetGlobalMousePosition();
        LoadSettings();
        ResetTimer();
    }

    void Update()
    {
        LoadSettings();

        if (MenuActions.IsAnyMenuOpen())
        {
            inspectorEvent = "Screensaver blocked by menu";
            inspectorTime = 0f;
            idleTimer = 0f;
            UpdateInspectorTimeoutLabel();
            return;
        }

        if (!enableBigScreenScreenSaver)
        {
            inspectorEvent = "Screensaver disabled";
            inspectorTime = 0f;
            idleTimer = 0f;
            UpdateInspectorTimeoutLabel();
            return;
        }

        bool isBigScreen = avatarAnimator != null && avatarAnimator.GetBool("isBigScreen");
        bool isBigScreenSaver = avatarAnimator != null && avatarAnimator.GetBool("isBigScreenSaver");

        if (isBigScreen && isBigScreenSaver)
        {
            idleTimer = 0f;
            inspectorEvent = "Screensaver active! Timer paused";
            inspectorTime = 0f;
            UpdateInspectorTimeoutLabel();

            if (IsGlobalUserInput())
            {
                avatarAnimator.SetBool("isBigScreenSaver", false);
                inspectorEvent = "Screensaver ended by input";

                bool isBigScreenAlarm = avatarAnimator.GetBool("isBigScreenAlarm");
                if (clickDisablesBoth && !isBigScreenAlarm)
                {
                    avatarAnimator.SetBool("isBigScreen", false);
                    inspectorEvent = "Exited Screensaver & BigScreen by input";
                    if (bigScreenHandler != null)
                        bigScreenHandler.SendMessage("DeactivateBigScreen");
                }
            }

            lastMousePos = GetGlobalMousePosition();
            return;
        }


        if (!IsInAllowedState())
        {
            inspectorEvent = "Screensaver blocked by state";
            inspectorTime = 0f;
            idleTimer = 0f;
            UpdateInspectorTimeoutLabel();
            return;
        }

        Vector2 mousePos = GetGlobalMousePosition();
        bool anyKeyPressed = IsAnyKeyPressed();

        if (Vector2.Distance(mousePos, lastMousePos) >= minMoveDistance || anyKeyPressed)
        {
            idleTimer = 0f;
            lastMousePos = mousePos;
            inspectorEvent = anyKeyPressed ? "Timer reset by key" : "Timer reset by mouse";
        }
        else
        {
            idleTimer += Time.deltaTime;
            inspectorEvent = "Timer is running";
        }

        inspectorTime = idleTimer;
        UpdateInspectorTimeoutLabel();

        int timeout = TimeoutSteps[Mathf.Clamp(timeoutStep, 0, TimeoutSteps.Length - 1)];
        if (idleTimer >= timeout)
        {
            if (avatarAnimator != null)
            {
                avatarAnimator.SetBool("isBigScreen", true);
                avatarAnimator.SetBool("isBigScreenSaver", true);
                inspectorEvent = "Screensaver activated!";
                idleTimer = 0f;
            }
            if (bigScreenHandler != null)
            {
                bigScreenHandler.SendMessage("ActivateBigScreen");
            }
        }
    }

    void LoadSettings()
    {
        if (SaveLoadHandler.Instance != null && SaveLoadHandler.Instance.data != null)
        {
            timeoutStep = Mathf.Clamp(SaveLoadHandler.Instance.data.bigScreenScreenSaverTimeoutIndex, 0, TimeoutSteps.Length - 1);
            enableBigScreenScreenSaver = SaveLoadHandler.Instance.data.bigScreenScreenSaverEnabled;
        }
    }

    void ResetTimer()
    {
        idleTimer = 0f;
        inspectorTime = 0f;
        inspectorEvent = "Timer reset";
    }

    void UpdateInspectorTimeoutLabel()
    {
        int timeout = TimeoutSteps[Mathf.Clamp(timeoutStep, 0, TimeoutSteps.Length - 1)];
        inspectorTimeoutLabel = FormatTime(timeout);
    }

    static string FormatTime(int seconds)
    {
        int h = seconds / 3600;
        int m = (seconds % 3600) / 60;
        int s = seconds % 60;
        if (h > 0) return $"{h}h {m:D2}m";
        if (m > 0) return $"{m}m {s:D2}s";
        return $"{s}s";
    }

    Vector2 GetGlobalMousePosition()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        POINT point;
        GetCursorPos(out point);
        return new Vector2(point.X, point.Y);
#else
        var api = DesktopWindowApi.Current;
        if (api != null && api.TryGetCursorPosition(out DesktopPoint point))
            return new Vector2(point.X, point.Y);
        return Input.mousePosition;
#endif
    }

    bool IsAnyKeyPressed()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        for (int key = 0x08; key <= 0xFE; key++)
        {
            if ((GetAsyncKeyState(key) & 0x8000) != 0)
                return true;
        }
        return false;
#else
        return Input.anyKeyDown;
#endif
    }

    bool IsInAllowedState()
    {
        if (avatarAnimator == null || allowedStates == null || allowedStates.Length == 0)
            return true;

        AnimatorStateInfo current = avatarAnimator.GetCurrentAnimatorStateInfo(0);
        for (int i = 0; i < allowedStates.Length; i++)
            if (current.IsName(allowedStates[i])) return true;
        return false;
    }

    private bool lastGlobalMouseDown = false;
    private bool IsGlobalUserInput()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        bool mouseDown = (GetAsyncKeyState(0x01) & 0x8000) != 0;
        bool mouseClick = mouseDown && !lastGlobalMouseDown;
        lastGlobalMouseDown = mouseDown;

        bool keyPressed = false;
        for (int key = 0x08; key <= 0xFE; key++)
        {
            if ((GetAsyncKeyState(key) & 0x8000) != 0)
            {
                keyPressed = true;
                break;
            }
        }
        return mouseClick || keyPressed;
#else
        bool mouseDown = Input.GetMouseButton(0);
        bool mouseClick = mouseDown && !lastGlobalMouseDown;
        lastGlobalMouseDown = mouseDown;
        return mouseClick || Input.anyKeyDown;
#endif
    }

}

#if UNITY_EDITOR
[UnityEditor.CustomEditor(typeof(AvatarBigScreenScreenSaver))]
public class AvatarBigScreenScreenSaverEditor : UnityEditor.Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        AvatarBigScreenScreenSaver script = (AvatarBigScreenScreenSaver)target;
        if (GUILayout.Button("Trigger Screensaver Now (Debug)"))
        {
            if (script.TryGetComponent<Animator>(out var anim))
            {
                anim.SetBool("isBigScreen", true);
                anim.SetBool("isBigScreenSaver", true);
            }
            if (script.TryGetComponent<AvatarBigScreenHandler>(out var handler))
            {
                handler.SendMessage("ActivateBigScreen");
            }
            script.inspectorEvent = "Screensaver triggered (Debug)";
        }
    }
}
#endif
