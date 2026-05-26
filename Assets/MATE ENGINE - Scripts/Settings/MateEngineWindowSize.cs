using Kirurobo;
using UnityEngine;

public static class MateEngineWindowSize
{
    private static readonly Vector2 SmallSize = new Vector2(768f, 512f);
#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
    private static readonly Vector2 MacNormalSize = new Vector2(960f, 640f);
    private static readonly Vector2 MacBigSize = new Vector2(1152f, 768f);
#else
    private static readonly Vector2 WindowsNormalSize = new Vector2(1536f, 1024f);
    private static readonly Vector2 WindowsBigSize = new Vector2(2048f, 1536f);
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
#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
        switch (state)
        {
            case SaveLoadHandler.SettingsData.WindowSizeState.Small:
                return SmallSize;
            case SaveLoadHandler.SettingsData.WindowSizeState.Big:
                return MacBigSize;
            default:
                return MacNormalSize;
        }
#else
        switch (state)
        {
            case SaveLoadHandler.SettingsData.WindowSizeState.Small:
                return SmallSize;
            case SaveLoadHandler.SettingsData.WindowSizeState.Big:
                return WindowsBigSize;
            default:
                return WindowsNormalSize;
        }
#endif
    }

    public static bool Apply(UniWindowController controller, SaveLoadHandler.SettingsData.WindowSizeState state)
    {
        return Apply(controller, state, null);
    }

    public static bool Apply(UniWindowController controller, SaveLoadHandler.SettingsData.WindowSizeState state, Vector2? position)
    {
        if (controller == null) return false;

        Vector2 currentSize = controller.windowSize;
        if (!IsValidSize(currentSize)) return false;

        Vector2 targetSize = GetSize(state);
        Vector2 targetPosition = position ?? controller.windowPosition;

        if (position.HasValue &&
            DesktopWindowApi.Current.TryMoveOwnWindow(
                Mathf.RoundToInt(targetPosition.x),
                Mathf.RoundToInt(targetPosition.y),
                Mathf.RoundToInt(targetSize.x),
                Mathf.RoundToInt(targetSize.y),
                true))
        {
            return true;
        }

        controller.windowSize = targetSize;
        controller.windowPosition = targetPosition;
        return true;
    }

    public static bool TryApplyToExistingController(SaveLoadHandler.SettingsData.WindowSizeState state)
    {
        return TryApplyToExistingController(state, null);
    }

    public static bool TryApplyToExistingController(SaveLoadHandler.SettingsData.WindowSizeState state, Vector2? position)
    {
        var controller = Object.FindFirstObjectByType<UniWindowController>();
        return Apply(controller, state, position);
    }

    public static bool IsValidSize(Vector2 size)
    {
        return size.x > 16f && size.y > 16f &&
            IsFinite(size.x) && IsFinite(size.y);
    }

    public static bool IsValidPosition(Vector2 position)
    {
        return IsFinite(position.x) && IsFinite(position.y) &&
            Mathf.Abs(position.x) < 100000f && Mathf.Abs(position.y) < 100000f;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
