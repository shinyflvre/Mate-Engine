using Kirurobo;
using UnityEngine;

public static class MateEngineWindowSize
{
    private static readonly Vector2 SmallSize = new Vector2(768f, 512f);

#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
    private static readonly Vector2 NormalSize = new Vector2(960f, 640f);
    private static readonly Vector2 BigSize = new Vector2(1152f, 768f);
#else
    private static readonly Vector2 NormalSize = new Vector2(1536f, 1024f);
    private static readonly Vector2 BigSize = new Vector2(2048f, 1536f);
#endif

    public static SaveLoadHandler.SettingsData.WindowSizeState Next(SaveLoadHandler.SettingsData.WindowSizeState state)
    {
        switch (state)
        {
            case SaveLoadHandler.SettingsData.WindowSizeState.Normal:
                return SaveLoadHandler.SettingsData.WindowSizeState.Big;
            case SaveLoadHandler.SettingsData.WindowSizeState.Big:
                return SaveLoadHandler.SettingsData.WindowSizeState.Small;
            default:
                return SaveLoadHandler.SettingsData.WindowSizeState.Normal;
        }
    }

    public static Vector2 GetSize(SaveLoadHandler.SettingsData.WindowSizeState state)
    {
        switch (state)
        {
            case SaveLoadHandler.SettingsData.WindowSizeState.Small:
                return SmallSize;
            case SaveLoadHandler.SettingsData.WindowSizeState.Big:
                return BigSize;
            default:
                return NormalSize;
        }
    }

    public static bool Apply(UniWindowController controller, SaveLoadHandler.SettingsData.WindowSizeState state)
    {
        if (controller == null) return false;

        Vector2 currentSize = controller.windowSize;
        if (!IsValidSize(currentSize)) return false;

        controller.windowSize = GetSize(state);
        return true;
    }

    public static bool TryApplyToExistingController(SaveLoadHandler.SettingsData.WindowSizeState state)
    {
        var controller = Object.FindFirstObjectByType<UniWindowController>();
        return Apply(controller, state);
    }

    private static bool IsValidSize(Vector2 size)
    {
        return size.x > 16f && size.y > 16f &&
            IsFinite(size.x) && IsFinite(size.y);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
