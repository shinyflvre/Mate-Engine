using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using System;
using System.Linq;
using System.Reflection;
public class CustomAvatarSettings : MonoBehaviour
{
    [Serializable]
    public class CustomParam
    {
        public string label = "Parameter";
        public ParamType type = ParamType.Slider;
        public string componentType = "";
        public string field = "";
        public float min = 0f, max = 1f, defaultValue = 0f;
        public bool defaultToggle = false;
        public List<string> options = new List<string>();
        public int defaultDropdown = 0;
        public GameObject uiObject;
    }
    public enum ParamType { Slider, Toggle, Dropdown, Button }

    public List<CustomParam> parameters = new();

    private Dictionary<string, float> sliderValues = new();
    private Dictionary<string, bool> toggleValues = new();
    private Dictionary<string, int> dropdownValues = new();

    private string fileName = "modded_settings.json";
    private string FilePath => MateEnginePaths.DataFilePath(fileName);

    [Serializable]
    public class SaveData
    {
        public Dictionary<string, float> sliderValues = new();
        public Dictionary<string, bool> toggleValues = new();
        public Dictionary<string, int> dropdownValues = new();
    }

    void Awake()
    {
        LoadFromDisk();
        InitDefaultsIfNeeded();
        HookAllEvents();
        ApplyAllSettingsToAllComponents();
    }

    void OnEnable()
    {
        ApplyAllSettingsToAllComponents();
        RegisterSceneCallbacks();
    }

    void OnDisable()
    {
        UnregisterSceneCallbacks();
    }

    void HookAllEvents()
    {
        foreach (var param in parameters)
        {
            if (param.type == ParamType.Slider && param.uiObject != null)
            {
                var s = param.uiObject.GetComponent<Slider>();
                if (s)
                {
                    s.minValue = param.min;
                    s.maxValue = param.max;
                    s.SetValueWithoutNotify(sliderValues[param.label]);
                    s.onValueChanged.RemoveAllListeners();
                    s.onValueChanged.AddListener(v =>
                    {
                        sliderValues[param.label] = v;
                        SaveToDisk();
                        ApplyAllSettingsToAllComponents();
                    });
                }
            }
            if (param.type == ParamType.Toggle && param.uiObject != null)
            {
                var t = param.uiObject.GetComponent<Toggle>();
                if (t)
                {
                    t.SetIsOnWithoutNotify(toggleValues[param.label]);
                    t.onValueChanged.RemoveAllListeners();
                    t.onValueChanged.AddListener(v =>
                    {
                        toggleValues[param.label] = v;
                        SaveToDisk();
                        ApplyAllSettingsToAllComponents();
                    });
                }
            }
            if (param.type == ParamType.Dropdown && param.uiObject != null)
            {
                var d = param.uiObject.GetComponent<Dropdown>();
                if (d)
                {
                    d.ClearOptions();
                    d.AddOptions(param.options);
                    d.SetValueWithoutNotify(dropdownValues[param.label]);
                    d.onValueChanged.RemoveAllListeners();
                    d.onValueChanged.AddListener(v =>
                    {
                        dropdownValues[param.label] = v;
                        SaveToDisk();
                        ApplyAllSettingsToAllComponents();
                    });
                }
            }
            if (param.type == ParamType.Button && param.uiObject != null)
            {
                var b = param.uiObject.GetComponent<Button>();
                if (b)
                {
                    b.onClick.RemoveAllListeners();
                    b.onClick.AddListener(() =>
                    {
                        ApplyAllSettingsToAllComponents();
                    });
                }
            }
        }
    }

    void InitDefaultsIfNeeded()
    {
        foreach (var param in parameters)
        {
            if (param.type == ParamType.Slider && !sliderValues.ContainsKey(param.label))
                sliderValues[param.label] = param.defaultValue;
            if (param.type == ParamType.Toggle && !toggleValues.ContainsKey(param.label))
                toggleValues[param.label] = param.defaultToggle;
            if (param.type == ParamType.Dropdown && !dropdownValues.ContainsKey(param.label))
                dropdownValues[param.label] = param.defaultDropdown;
        }
        SaveToDisk();
    }

    void ApplyAllSettingsToAllComponents()
    {
        foreach (var param in parameters)
        {
            foreach (var c in FindAllComponentsOfType(param.componentType))
            {
                var field = c.GetType().GetField(param.field, BindingFlags.Public | BindingFlags.Instance);
                if (field == null) continue;

                if (param.type == ParamType.Slider && sliderValues.TryGetValue(param.label, out float val))
                {
                    if (field.FieldType == typeof(float)) field.SetValue(c, val);
                    if (field.FieldType == typeof(int)) field.SetValue(c, Mathf.RoundToInt(val));
                }
                if (param.type == ParamType.Toggle && toggleValues.TryGetValue(param.label, out bool bval))
                {
                    if (field.FieldType == typeof(bool)) field.SetValue(c, bval);
                }
                if (param.type == ParamType.Dropdown && dropdownValues.TryGetValue(param.label, out int ival))
                {
                    if (field.FieldType == typeof(int)) field.SetValue(c, ival);
                    if (field.FieldType == typeof(string) && param.options.Count > ival) field.SetValue(c, param.options[ival]);
                }
            }
            if (param.type == ParamType.Slider && param.uiObject != null)
            {
                var s = param.uiObject.GetComponent<Slider>();
                if (s) s.SetValueWithoutNotify(sliderValues[param.label]);
            }
            if (param.type == ParamType.Toggle && param.uiObject != null)
            {
                var t = param.uiObject.GetComponent<Toggle>();
                if (t) t.SetIsOnWithoutNotify(toggleValues[param.label]);
            }
            if (param.type == ParamType.Dropdown && param.uiObject != null)
            {
                var d = param.uiObject.GetComponent<Dropdown>();
                if (d) d.SetValueWithoutNotify(dropdownValues[param.label]);
            }
        }
    }

    /*
    IEnumerable<Component> FindAllComponentsOfType(string typeName)
    {
        foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            if (!go.scene.IsValid()) continue;
            foreach (var c in go.GetComponents<Component>())
                if (c.GetType().Name == typeName)
                    yield return c;
        }
    }
    */

    IEnumerable<Component> FindAllComponentsOfType(string typeName)
    {
        foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            if (!go.scene.IsValid()) continue;
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                if (c.GetType().Name == typeName)
                    yield return c;
            }
        }
    }


    void SaveToDisk()
    {
        var data = new SaveData()
        {
            sliderValues = sliderValues,
            toggleValues = toggleValues,
            dropdownValues = dropdownValues
        };
        string json = JsonConvert.SerializeObject(data, Formatting.Indented);
        File.WriteAllText(FilePath, json);
    }

    void LoadFromDisk()
    {
        if (File.Exists(FilePath))
        {
            try
            {
                string json = File.ReadAllText(FilePath);
                var data = JsonConvert.DeserializeObject<SaveData>(json);
                sliderValues = data.sliderValues ?? new();
                toggleValues = data.toggleValues ?? new();
                dropdownValues = data.dropdownValues ?? new();
            }
            catch
            {
                sliderValues = new(); toggleValues = new(); dropdownValues = new();
            }
        }
        else
        {
            sliderValues = new(); toggleValues = new(); dropdownValues = new();
        }
    }
    void RegisterSceneCallbacks()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.hierarchyChanged += OnHierarchyChanged;
#endif
    }

    void UnregisterSceneCallbacks()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.hierarchyChanged -= OnHierarchyChanged;
#endif
    }

    void OnHierarchyChanged()
    {
        ApplyAllSettingsToAllComponents();
    }
    void OnTransformChildrenChanged()
    {
        ApplyAllSettingsToAllComponents();
    }
    void OnApplicationFocus(bool focus)
    {
        if (focus)
            ApplyAllSettingsToAllComponents();
    }
}
