using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Debug = UnityEngine.Debug;

public class SystemStartHandler : MonoBehaviour
{
#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
    private const string DefaultMacLaunchAgentLabel = "com.Shinymoon.MateEngineX.autostart";
#endif

    [Header("UI (Optional)")]
    public Toggle autoStartToggle;
    public TMP_Text checkmarkText;

    [Header("Settings")]
    public string runKeyName = "MateEngine";
#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
    public string macLaunchAgentLabel = DefaultMacLaunchAgentLabel;
#endif
    public string commandLineArgs = "";

    private bool _isApplyingUI;

    private void Awake()
    {
        if (SaveLoadHandler.Instance == null)
        {
            Debug.LogError("[SystemStartHandler] SaveLoadHandler.Instance is null. Place SaveLoadHandler in the scene first.");
            enabled = false;
            return;
        }
    }

    private void Start()
    {
        if (autoStartToggle != null)
            autoStartToggle.onValueChanged.AddListener(OnUIToggleChanged);

        LoadFromSaveWithoutNotify();
        TryApplyAutostart(SaveLoadHandler.Instance.data.startWithWindows);
    }

    private void OnDestroy()
    {
        if (autoStartToggle != null)
            autoStartToggle.onValueChanged.RemoveListener(OnUIToggleChanged);
    }

    private void OnUIToggleChanged(bool isOn)
    {
        if (_isApplyingUI) return;

        SaveLoadHandler.Instance.data.startWithWindows = isOn;
        SaveLoadHandler.Instance.SaveToDisk();

        TryApplyAutostart(isOn);
        UpdateCheckmarkText(isOn);
    }

    public void OnCheckmarkClicked()
    {
        bool newState = !GetSavedState();
        SetStateFromCode(newState);
    }

    public void SetStateFromCode(bool isOn)
    {
        SaveLoadHandler.Instance.data.startWithWindows = isOn;
        SaveLoadHandler.Instance.SaveToDisk();
        TryApplyAutostart(isOn);
        ApplyToUIWithoutNotify(isOn);
    }

    private void LoadFromSaveWithoutNotify()
    {
        ApplyToUIWithoutNotify(GetSavedState());
    }

    private bool GetSavedState()
    {
        return SaveLoadHandler.Instance.data != null && SaveLoadHandler.Instance.data.startWithWindows;
    }

    private void ApplyToUIWithoutNotify(bool isOn)
    {
        _isApplyingUI = true;
        try
        {
            if (autoStartToggle != null)
                autoStartToggle.SetIsOnWithoutNotify(isOn);
            UpdateCheckmarkText(isOn);
        }
        finally
        {
            _isApplyingUI = false;
        }
    }

    private void UpdateCheckmarkText(bool isOn)
    {
        if (checkmarkText != null)
            checkmarkText.text = isOn ? "☑ Start with System" : "☐ Start with System";
    }

    // ---------------- Autostart Handling ----------------

    private void TryApplyAutostart(bool enable)
    {
#if UNITY_STANDALONE_WIN
        TryApplyWindowsRegistry(enable);
#elif UNITY_STANDALONE_OSX
        TryApplyMacLaunchAgent(enable);
#else
        Debug.Log("[SystemStartHandler] Autostart disabled on this platform.");
#endif
    }

    private void TryApplyWindowsRegistry(bool enable)
    {
#if UNITY_STANDALONE_WIN
        if (Application.platform != RuntimePlatform.WindowsPlayer &&
            Application.platform != RuntimePlatform.WindowsEditor)
        {
            Debug.Log("[SystemStartHandler] Skipping registry (not on Windows).");
            return;
        }

        try
        {
            string exePath = GetCurrentExecutablePathQuoted();
            if (string.IsNullOrEmpty(exePath))
            {
                Debug.LogWarning("[SystemStartHandler] Executable path empty. Skipping registry write.");
                return;
            }

            string value = string.IsNullOrWhiteSpace(commandLineArgs)
                ? exePath
                : exePath + " " + commandLineArgs;

            using (var key = global::Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                       @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true))
            {
                if (key == null)
                {
                    Debug.LogError("[SystemStartHandler] HKCU Run key not found.");
                    return;
                }

                if (enable)
                {
                    key.SetValue(runKeyName, value);
                    Debug.Log($"[SystemStartHandler] Enabled autostart (HKCU) as '{runKeyName}' → {value}");
                }
                else
                {
                    key.DeleteValue(runKeyName, false);
                    Debug.Log($"[SystemStartHandler] Disabled autostart (HKCU) for '{runKeyName}'.");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[SystemStartHandler] Registry write failed: " + ex.Message);
        }
#else
        Debug.Log("[SystemStartHandler] Registry disabled on this platform.");
#endif
    }

#if UNITY_STANDALONE_OSX
    private void TryApplyMacLaunchAgent(bool enable)
    {
#if UNITY_EDITOR
        Debug.Log("[SystemStartHandler] macOS LaunchAgent is skipped in the Unity Editor.");
        return;
#else
        try
        {
            string plistPath = GetMacLaunchAgentPath();
            if (string.IsNullOrEmpty(plistPath))
            {
                Debug.LogWarning("[SystemStartHandler] LaunchAgent path empty. Skipping autostart write.");
                return;
            }

            if (!enable)
            {
                if (File.Exists(plistPath)) File.Delete(plistPath);
                Debug.Log("[SystemStartHandler] Disabled autostart (LaunchAgent): " + plistPath);
                return;
            }

            string appPath = GetMacAppBundlePath();
            if (string.IsNullOrEmpty(appPath) || !Directory.Exists(appPath))
            {
                Debug.LogWarning("[SystemStartHandler] App bundle path empty or missing. Skipping LaunchAgent write: " + appPath);
                return;
            }

            string dir = Path.GetDirectoryName(plistPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(plistPath, BuildMacLaunchAgentPlist(appPath), Encoding.UTF8);
            Debug.Log("[SystemStartHandler] Enabled autostart (LaunchAgent): " + plistPath + " -> " + appPath);
        }
        catch (Exception ex)
        {
            Debug.LogError("[SystemStartHandler] LaunchAgent write failed: " + ex.Message);
        }
#endif
    }

    private string GetMacLaunchAgentPath()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        if (string.IsNullOrEmpty(home)) return string.Empty;
        string label = GetMacLaunchAgentLabel();
        return Path.Combine(home, "Library", "LaunchAgents", label + ".plist");
    }

    private string GetMacAppBundlePath()
    {
#if UNITY_EDITOR
        return string.Empty;
#else
        string dataPath = Application.dataPath;
        if (!string.IsNullOrEmpty(dataPath))
        {
            var dir = new DirectoryInfo(dataPath);
            while (dir != null)
            {
                if (dir.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }

        try
        {
            string proc = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(proc))
            {
                var dir = new FileInfo(proc).Directory;
                while (dir != null)
                {
                    if (dir.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                        return dir.FullName;
                    dir = dir.Parent;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SystemStartHandler] Failed to resolve app bundle path: " + ex.Message);
        }
        return string.Empty;
#endif
    }

    private string BuildMacLaunchAgentPlist(string appBundlePath)
    {
        var args = SplitCommandLineArgs(commandLineArgs);
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">");
        sb.AppendLine("<plist version=\"1.0\">");
        sb.AppendLine("<dict>");
        sb.AppendLine("  <key>Label</key>");
        sb.AppendLine("  <string>" + EscapePlistString(GetMacLaunchAgentLabel()) + "</string>");
        sb.AppendLine("  <key>LimitLoadToSessionType</key>");
        sb.AppendLine("  <string>Aqua</string>");
        sb.AppendLine("  <key>ProgramArguments</key>");
        sb.AppendLine("  <array>");
        sb.AppendLine("    <string>/usr/bin/open</string>");
        sb.AppendLine("    <string>" + EscapePlistString(appBundlePath) + "</string>");
        if (args.Count > 0)
        {
            sb.AppendLine("    <string>--args</string>");
            for (int i = 0; i < args.Count; i++)
                sb.AppendLine("    <string>" + EscapePlistString(args[i]) + "</string>");
        }
        sb.AppendLine("  </array>");
        sb.AppendLine("  <key>RunAtLoad</key>");
        sb.AppendLine("  <true/>");
        sb.AppendLine("</dict>");
        sb.AppendLine("</plist>");
        return sb.ToString();
    }

    private string GetMacLaunchAgentLabel()
    {
        if (!string.IsNullOrWhiteSpace(macLaunchAgentLabel))
            return SanitizeLaunchAgentLabel(macLaunchAgentLabel);

        string appId = Application.identifier;
        if (!string.IsNullOrWhiteSpace(appId))
            return SanitizeLaunchAgentLabel(appId + ".autostart");

        return DefaultMacLaunchAgentLabel;
    }

    private static string SanitizeLaunchAgentLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DefaultMacLaunchAgentLabel;
        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool valid = char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_';
            sb.Append(valid ? c : '.');
        }
        string label = sb.ToString().Trim('.');
        return string.IsNullOrEmpty(label) ? DefaultMacLaunchAgentLabel : label;
    }

    private static string EscapePlistString(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    private static List<string> SplitCommandLineArgs(string args)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(args)) return result;

        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < args.Length; i++)
        {
            char c = args[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (sb.Length > 0)
                {
                    result.Add(sb.ToString());
                    sb.Length = 0;
                }
                continue;
            }

            sb.Append(c);
        }

        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }
#endif

    private string GetCurrentExecutablePathQuoted()
    {
#if UNITY_EDITOR
        return string.Empty;
#else
        try
        {
            // Safer way in builds: Application.dataPath → go up one folder
            string exe = Path.Combine(Directory.GetParent(Application.dataPath).FullName,
                                      Application.productName + ".exe");
            if (File.Exists(exe))
                return $"\"{exe}\"";

            // Fallback: try Process API
            string proc = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            return string.IsNullOrEmpty(proc) ? string.Empty : $"\"{proc}\"";
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SystemStartHandler] Failed to get exe path: " + ex.Message);
            return string.Empty;
        }
#endif
    }
}
