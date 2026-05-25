using System;
using UnityEngine;

public static class MonitorHelper
{
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    public static Rect GetTaskbarRectForWindow(IntPtr windowHandle)
    {
        DesktopRect r = DesktopWindowApi.Current.GetTaskbarRectForOwnWindow();
        return r.IsEmpty ? new Rect(0, 0, 0, 0) : r.ToUnityRect();
    }

    public static float GetScaleForWindow(IntPtr windowHandle)
    {
        return 1f;
    }
}
