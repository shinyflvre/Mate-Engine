using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using Utils;
using System.Reflection;
#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
using System.Runtime.InteropServices;
#endif

public class SystemTray : MonoBehaviour
{
    [Serializable]
    public class TrayAction
    {
        public string label;
        public TrayActionType type;
        public GameObject handlerObject;
        public string toggleField;
        public string methodName;
    }

    public enum TrayActionType { Toggle, Button, Method }

    [SerializeField] private Texture2D icon;
    [SerializeField] private string iconName;
    [SerializeField] public List<TrayAction> actions = new();

#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
    private const int MacMenuLabelStride = 256;
    private const float MacMenuRefreshInterval = 0.5f;
    private readonly List<Action> macMenuActions = new();
    private bool macStatusMenuInitialized;
    private float nextMacMenuRefreshTime;
#endif

    void Awake()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        TrayIcon.OnBuildMenu = BuildMenu;
        TrayIcon.Init("App", iconName, icon, BuildMenu());
#elif UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
        InitMacStatusMenu();
#endif
    }

#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
    void Update()
    {
        if (!macStatusMenuInitialized) return;

        int selectedIndex = MacStatusMenu.PollSelectedIndex();
        if (selectedIndex >= 0 && selectedIndex < macMenuActions.Count)
        {
            try { macMenuActions[selectedIndex]?.Invoke(); }
            catch (Exception e) { Debug.LogException(e); }
            RefreshMacStatusMenu();
            return;
        }

        if (!MacStatusMenu.IsOpen() && Time.unscaledTime >= nextMacMenuRefreshTime)
            RefreshMacStatusMenu();
    }

    void OnDestroy()
    {
        DisposeMacStatusMenu();
    }

    void OnApplicationQuit()
    {
        DisposeMacStatusMenu();
    }
#endif

    private List<(string, Action)> BuildMenu()
    {
        var context = new List<(string, Action)>();
        foreach (var action in actions)
        {
            if (action.type == TrayActionType.Toggle)
            {
                bool state = GetToggleState(action);
                string label = (state ? "✔ " : "✖ ") + action.label;
                context.Add((label, () => { ToggleAction(action); }));
            }
            else if (action.type == TrayActionType.Button || action.type == TrayActionType.Method)
            {
                context.Add((action.label, () => ButtonAction(action)));
            }
        }
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        var app = FindObjectOfType<RemoveTaskbarApp>();
        bool hidden = app != null && app.IsHidden;
        string toggleLabel = hidden ? "✖ Show App in Taskbar" : "✔ Hide App from Taskbar";
        context.Add((toggleLabel, () =>
        {
            if (app != null) app.ToggleAppMode();
        }
        ));
#endif

        context.Add(("Quit MateEngine", QuitApp));
        return context;
    }

#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
    private void InitMacStatusMenu()
    {
        try
        {
            macStatusMenuInitialized = MacStatusMenu.Init();
            if (macStatusMenuInitialized) RefreshMacStatusMenu();
        }
        catch (Exception e)
        {
            macStatusMenuInitialized = false;
            Debug.LogWarning($"Failed to initialize macOS status menu: {e.Message}");
        }
    }

    private void RefreshMacStatusMenu()
    {
        if (!macStatusMenuInitialized) return;

        var entries = BuildMenu();
        macMenuActions.Clear();

        int count = entries != null ? entries.Count : 0;
        byte[] labels = new byte[Mathf.Max(0, count) * MacMenuLabelStride];
        for (int i = 0; i < count; i++)
        {
            string label = entries[i].Item1 ?? string.Empty;
            byte[] labelBytes = Encoding.UTF8.GetBytes(label);
            int copyLength = Mathf.Min(labelBytes.Length, MacMenuLabelStride - 1);
            Buffer.BlockCopy(labelBytes, 0, labels, i * MacMenuLabelStride, copyLength);
            macMenuActions.Add(entries[i].Item2);
        }

        MacStatusMenu.SetItems(labels, count, MacMenuLabelStride);
        nextMacMenuRefreshTime = Time.unscaledTime + MacMenuRefreshInterval;
    }

    private void DisposeMacStatusMenu()
    {
        if (!macStatusMenuInitialized) return;
        macStatusMenuInitialized = false;
        try { MacStatusMenu.Dispose(); }
        catch { }
    }

    private static class MacStatusMenu
    {
        private const string Lib = "MateDesktopWindowMac";

        [DllImport(Lib, EntryPoint = "MateDWStatusMenuInit")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool Init();

        [DllImport(Lib, EntryPoint = "MateDWStatusMenuSetItems")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SetItems([In] byte[] labels, int count, int stride);

        [DllImport(Lib, EntryPoint = "MateDWStatusMenuPollSelectedIndex")]
        public static extern int PollSelectedIndex();

        [DllImport(Lib, EntryPoint = "MateDWStatusMenuIsOpen")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool IsOpen();

        [DllImport(Lib, EntryPoint = "MateDWStatusMenuDispose")]
        public static extern void Dispose();
    }
#endif

    private bool GetToggleState(TrayAction action)
    {
        if (action.handlerObject == null || string.IsNullOrEmpty(action.toggleField)) return false;

        var monos = action.handlerObject.GetComponents<MonoBehaviour>();
        foreach (var mono in monos)
        {
            if (mono == null) continue;
            var type = mono.GetType();
            var field = type.GetField(action.toggleField, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null && field.FieldType == typeof(Toggle))
            {
                var toggle = field.GetValue(mono) as Toggle;
                if (toggle != null)
                    return toggle.isOn;
            }
        }
        return false;
    }

    private void ToggleAction(TrayAction action)
    {
        if (action.handlerObject == null || string.IsNullOrEmpty(action.toggleField)) return;

        var monos = action.handlerObject.GetComponents<MonoBehaviour>();
        foreach (var mono in monos)
        {
            if (mono == null) continue;
            var type = mono.GetType();
            var field = type.GetField(action.toggleField, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null && field.FieldType == typeof(Toggle))
            {
                var toggle = field.GetValue(mono) as Toggle;
                if (toggle != null)
                {
                    toggle.isOn = !toggle.isOn;
                    return;
                }
            }
        }
    }

    private void ButtonAction(TrayAction action)
    {
        if (action.handlerObject == null || string.IsNullOrEmpty(action.methodName)) return;

        var monos = action.handlerObject.GetComponents<MonoBehaviour>();
        foreach (var mono in monos)
        {
            if (mono == null) continue;
            var type = mono.GetType();
            var method = type.GetMethod(action.methodName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (method != null && method.GetParameters().Length == 0)
            {
                method.Invoke(mono, null);
                return;
            }
        }
    }

    private void QuitApp()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}
