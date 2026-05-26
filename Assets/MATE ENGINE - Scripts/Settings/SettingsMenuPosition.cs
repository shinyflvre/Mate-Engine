using UnityEngine;
using System;
using System.Collections.Generic;

public class SettingsMenuPosition : MonoBehaviour
{
    [Serializable]
    public class MenuEntry
    {
        public RectTransform settingsMenu;
        [HideInInspector] public float originalX;
        [HideInInspector] public float originalY;
        [HideInInspector] public Vector2 lastApplied;
    }

    [Header("Menus to track")]
    public List<MenuEntry> menus = new List<MenuEntry>();

    [Header("Edge margin in Pixels")]
    public float edgeMargin = 50f;

    [Header("Checks per second")]
    public float checkFPS = 20f;

    [Header("Monitor refresh (sec)")]
    public float monitorRefreshInterval = 2f;

    private IDesktopWindowApi windowApi;
    private readonly List<DesktopRect> monitorRects = new List<DesktopRect>();
    private float checkTimer;
    private float monitorTimer;
    private bool lastAtRightEdge;
    private bool initedEdge;

    void Start()
    {
        windowApi = DesktopWindowApi.Current;
        windowApi.RefreshOwnWindow();
        RefreshMonitors();
        foreach (var menu in menus)
        {
            if (!menu.settingsMenu) continue;
            menu.originalX = menu.settingsMenu.anchoredPosition.x;
            menu.originalY = menu.settingsMenu.anchoredPosition.y;
            menu.lastApplied = menu.settingsMenu.anchoredPosition;
        }
    }

    void Update()
    {
        if (windowApi == null) windowApi = DesktopWindowApi.Current;
        if (!windowApi.IsSupported || !windowApi.RefreshOwnWindow()) return;

        monitorTimer += Time.unscaledDeltaTime;
        if (monitorTimer >= Mathf.Max(0.1f, monitorRefreshInterval))
        {
            monitorTimer = 0f;
            RefreshMonitors();
        }

        checkTimer += Time.unscaledDeltaTime;
        float step = 1f / Mathf.Max(1f, checkFPS);
        if (checkTimer < step) return;
        checkTimer = 0f;

        if (!windowApi.TryGetOwnWindowRect(out DesktopRect winRect)) return;

        DesktopRect screen = monitorRects.Count > 0 ? GetBestMonitor(winRect) : new DesktopRect(0, 0, Screen.currentResolution.width, Screen.currentResolution.height);

        bool atRightEdge = winRect.Right >= (screen.Right - edgeMargin);
        if (!initedEdge) { lastAtRightEdge = atRightEdge; initedEdge = true; }

        if (atRightEdge != lastAtRightEdge)
        {
            lastAtRightEdge = atRightEdge;
            for (int i = 0; i < menus.Count; i++)
            {
                var m = menus[i];
                if (!m.settingsMenu) continue;
                Vector2 target = new Vector2(atRightEdge ? -m.originalX : m.originalX, m.originalY);
                if (m.lastApplied != target)
                {
                    m.settingsMenu.anchoredPosition = target;
                    m.lastApplied = target;
                }
            }
        }
    }

    void RefreshMonitors()
    {
        monitorRects.Clear();
        var monitors = windowApi.GetMonitors();
        for (int i = 0; i < monitors.Count; i++)
            monitorRects.Add(monitors[i].Rect);
    }

    DesktopRect GetBestMonitor(DesktopRect win)
    {
        int idx = 0, maxArea = 0;
        for (int i = 0; i < monitorRects.Count; i++)
        {
            int a = OverlapArea(win, monitorRects[i]);
            if (a > maxArea) { maxArea = a; idx = i; }
        }
        return monitorRects[idx];
    }

    int OverlapArea(DesktopRect a, DesktopRect b)
    {
        int x1 = System.Math.Max(a.Left, b.Left);
        int x2 = System.Math.Min(a.Right, b.Right);
        int y1 = System.Math.Max(a.Top, b.Top);
        int y2 = System.Math.Min(a.Bottom, b.Bottom);
        int w = x2 - x1;
        int h = y2 - y1;
        return (w > 0 && h > 0) ? w * h : 0;
    }
}
