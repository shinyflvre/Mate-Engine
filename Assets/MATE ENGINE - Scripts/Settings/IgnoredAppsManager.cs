using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using NAudio.CoreAudioApi;
#endif

public class AllowedAppsManager : MonoBehaviour
{
    public TMP_Dropdown runningAppsDropdown;
    public Button addToAllowedListButton;
    public Transform allowedAppsListContent;
    public GameObject allowedAppItemPrefab;

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    private MMDeviceEnumerator enumerator;
    private MMDevice defaultDevice;
#endif

    private List<string> currentRunningAppNames = new List<string>();
    private List<string> allowedApps => SaveLoadHandler.Instance.data.allowedApps;

    private void Start()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        enumerator = new MMDeviceEnumerator();
        UpdateDefaultDevice();
#endif

        addToAllowedListButton.onClick.AddListener(() =>
        {
            if (runningAppsDropdown.options.Count == 0) return;

            string selectedApp = runningAppsDropdown.options[runningAppsDropdown.value].text;
            if (!allowedApps.Contains(selectedApp))
            {
                allowedApps.Add(selectedApp);
                UpdateAllowedListUI();
                RefreshRunningAppsDropdown(); // ← this is the key fix
                SaveLoadHandler.Instance.SaveToDisk();
                SaveLoadHandler.SyncAllowedAppsToAllAvatars();
            }

        });

        RefreshRunningAppsDropdown();
        UpdateAllowedListUI();
        SaveLoadHandler.SyncAllowedAppsToAllAvatars(); // Initial sync on load
    }

    private void UpdateDefaultDevice()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        defaultDevice?.Dispose();
        defaultDevice = enumerator?.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
#endif
    }

    private void RefreshRunningAppsDropdown()
    {
        UpdateDefaultDevice(); // Ensure defaultDevice is fresh

        currentRunningAppNames = GetRunningAudioAppNames();

        var filteredAppNames = currentRunningAppNames
            .Where(app => !allowedApps.Contains(app))
            .OrderBy(app => app)
            .ToList();

        runningAppsDropdown.ClearOptions();
        runningAppsDropdown.AddOptions(
            filteredAppNames.Select(app => new TMP_Dropdown.OptionData(app)).ToList()
        );

        // Reset dropdown index if empty
        if (filteredAppNames.Count == 0)
            runningAppsDropdown.value = 0;
    }

    public void OnDropdownOpened()
    {
        RefreshRunningAppsDropdown();
    }

    private void UpdateAllowedListUI()
    {
        foreach (Transform child in allowedAppsListContent)
            Destroy(child.gameObject);

        foreach (var app in allowedApps)
        {
            var item = Instantiate(allowedAppItemPrefab, allowedAppsListContent);

            var label = item.GetComponentsInChildren<TextMeshProUGUI>()
                            .FirstOrDefault(t => t.transform.parent == item.transform);
            if (label != null) label.text = app;

            var button = item.transform.Find("Button")?.GetComponent<Button>();
            if (button != null)
            {
                button.onClick.AddListener(() =>
                {
                    allowedApps.Remove(app);
                    UpdateAllowedListUI();
                    SaveLoadHandler.Instance.SaveToDisk();
                    SaveLoadHandler.SyncAllowedAppsToAllAvatars();
                });
            }
        }
    }

    private List<string> GetRunningAudioAppNames()
    {
        var appNames = new HashSet<string>();
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        try
        {
            if (defaultDevice == null) return appNames.OrderBy(n => n).ToList();
            var sessions = defaultDevice.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                int processId = (int)session.GetProcessID;
                if (processId == 0) continue;

                try
                {
                    var process = Process.GetProcessById(processId);
                    string name = process.ProcessName.ToLowerInvariant();
                    appNames.Add(name);
                }
                catch { continue; }
            }
        }
        catch { }
#endif

        return appNames.OrderBy(n => n).ToList();
    }

    private void OnDestroy()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        enumerator?.Dispose();
        defaultDevice?.Dispose();
#endif
    }

    public void RefreshAppListOnMenuOpen()
    {
        RefreshRunningAppsDropdown();
        UpdateAllowedListUI();
        SaveLoadHandler.SyncAllowedAppsToAllAvatars();
    }

    public void RefreshUI()
    {
        UpdateDefaultDevice();
        RefreshRunningAppsDropdown();
        UpdateAllowedListUI();
        SaveLoadHandler.SyncAllowedAppsToAllAvatars();
    }
}
