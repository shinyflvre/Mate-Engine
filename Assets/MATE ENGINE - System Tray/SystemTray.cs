using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Utils;
using System.Reflection;

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

    void Awake()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        TrayIcon.OnBuildMenu = BuildMenu;
        TrayIcon.Init("App", iconName, icon, BuildMenu());
#endif
    }

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
        var app = FindObjectOfType<RemoveTaskbarApp>();
        bool hidden = app != null && app.IsHidden;
        string toggleLabel = hidden ? "✖ Show App in Taskbar" : "✔ Hide App from Taskbar";
        context.Add((toggleLabel, () =>
        {
            if (app != null) app.ToggleAppMode();
        }
        ));

        context.Add(("Quit MateEngine", QuitApp));
        return context;
    }

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
