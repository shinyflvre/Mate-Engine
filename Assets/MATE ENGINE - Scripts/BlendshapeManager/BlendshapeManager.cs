using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Newtonsoft.Json;

public class BlendshapeManager : MonoBehaviour
{
    [Header("UI Targets")]
    public Transform contentParent;                
    public GameObject blendshapeBlockPrefab;        

    [Header("Behaviour")]
    public float rescanInterval = 0.75f;           
    public bool onlyUnderActiveAvatar = true;       

    private readonly List<GameObject> activeBlocks = new List<GameObject>();
    private List<BlendRef> currentRefs = new List<BlendRef>();
    private string currentSignature = "";
    private string currentAvatarName = "";
    public Button resetAllButton;

    private Dictionary<string, float> blendValues = new Dictionary<string, float>();
    private class BlendRef
    {
        public SkinnedMeshRenderer smr;
        public int index;
        public string shapeName;
        public string uniqueKey;   
    }

    private void OnEnable()
    {
        StartCoroutine(ScanLoop());
    }

    private IEnumerator ScanLoop()
    {
        while (true)
        {
            try
            {
                BuildRefsIfChanged();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[BlendshapeManager] ScanLoop error: " + e.Message);
            }
            yield return new WaitForSeconds(rescanInterval);
        }
    }

    void Start()
    {
        if (resetAllButton != null)
        {
            resetAllButton.onClick.RemoveAllListeners();
            resetAllButton.onClick.AddListener(ResetAllBlendshapes);
        }
    }

    public void ResetAllBlendshapes()
    {
        for (int i = 0; i < currentRefs.Count; i++)
        {
            var r = currentRefs[i];
            blendValues[r.uniqueKey] = 0f;
        }
        ApplyAllToAvatar();
        SaveToDisk();
    }


    private void BuildRefsIfChanged()
    {
        var root = ResolveActiveAvatarRoot(out string avatarNameGuess);
        if (string.IsNullOrEmpty(avatarNameGuess))
            avatarNameGuess = "DefaultAvatar";

        var smrs = (onlyUnderActiveAvatar && root != null)
            ? root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            : FindObjectsOfType<SkinnedMeshRenderer>(true);

        var newRefs = new List<BlendRef>();

        foreach (var smr in smrs)
        {
            if (smr == null || smr.sharedMesh == null) continue;
            if (!smr.gameObject.activeInHierarchy || !smr.enabled) continue;
            int count = smr.sharedMesh.blendShapeCount;
            if (count <= 0) continue;

            for (int i = 0; i < count; i++)
            {
                string name = smr.sharedMesh.GetBlendShapeName(i);
                if (string.IsNullOrEmpty(name)) continue;

                string path = GetTransformPath(smr.transform, root);
                string key = path + ":" + name;

                newRefs.Add(new BlendRef
                {
                    smr = smr,
                    index = i,
                    shapeName = name,
                    uniqueKey = key
                });
            }
        }

        newRefs = newRefs
            .OrderBy(r => r.uniqueKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string newSignature = string.Join("|", newRefs.Select(r => r.uniqueKey));
        bool avatarChanged = !string.Equals(avatarNameGuess, currentAvatarName, StringComparison.Ordinal);
        bool setChanged = !string.Equals(newSignature, currentSignature, StringComparison.Ordinal);

        if (avatarChanged || setChanged)
        {
            currentAvatarName = avatarNameGuess;
            currentSignature = newSignature;
            currentRefs = newRefs;
            RebuildUIBlocks();
            LoadFromDisk();
            ApplyAllToAvatar();
        }
        else
        {
        }
    }

    private void RebuildUIBlocks()
    {
        foreach (var go in activeBlocks)
            if (go) Destroy(go);
        activeBlocks.Clear();

        blendValues.Clear();

        if (currentRefs.Count == 0) return;

        int neededBlocks = Mathf.CeilToInt(currentRefs.Count / 9f);

        int refIdx = 0;
        for (int b = 0; b < neededBlocks; b++)
        {
            var blockGO = Instantiate(blendshapeBlockPrefab, contentParent);
            var block = blockGO.GetComponent<BlendshapeUIBlock>();
            if (block == null)
            {
                Debug.LogError("[BlendshapeManager] Prefab lacks BlendshapeUIBlock component.");
                Destroy(blockGO);
                continue;
            }
            for (int slot = 0; slot < 9; slot++)
            {
                if (refIdx >= currentRefs.Count)
                {
                    block.ClearUnusedFrom(slot);
                    break;
                }

                var r = currentRefs[refIdx++];
                float startVal = 0f;
                try
                {
                    startVal = r.smr.GetBlendShapeWeight(r.index);
                }
                catch { }
                if (!blendValues.ContainsKey(r.uniqueKey))
                    blendValues[r.uniqueKey] = startVal;

                string displayName = r.shapeName; 
                block.SetupSlot(slot, displayName, startVal, v =>
                {
                    blendValues[r.uniqueKey] = v;
                    try { r.smr.SetBlendShapeWeight(r.index, v); } catch { }
                    SaveToDisk(); 
                });
            }

            activeBlocks.Add(blockGO);
        }

        if (activeBlocks.Count > 0)
        {
            int remaining = currentRefs.Count % 9;
            if (remaining == 0) remaining = 9; 
            var lastBlock = activeBlocks[activeBlocks.Count - 1].GetComponent<BlendshapeUIBlock>();
            if (lastBlock != null)
                lastBlock.ClearUnusedFrom(remaining);
        }
    }

    private void ApplyAllToAvatar()
    {
        foreach (var r in currentRefs)
        {
            if (blendValues.TryGetValue(r.uniqueKey, out float v))
            {
                try { r.smr.SetBlendShapeWeight(r.index, v); } catch { }
            }
        }
        int idx = 0;
        foreach (var go in activeBlocks)
        {
            var block = go.GetComponent<BlendshapeUIBlock>();
            if (block == null) continue;

            for (int slot = 0; slot < 9; slot++)
            {
                if (idx >= currentRefs.Count)
                {
                    block.SetSlotActive(slot, false);
                    continue;
                }
                var r = currentRefs[idx++];
                float v = blendValues.TryGetValue(r.uniqueKey, out float vv) ? vv : 0f;
                if (block.sliders[slot] != null)
                    block.sliders[slot].SetValueWithoutNotify(v);
            }
        }
    }

    private string GetSaveFolder()
    {
        return MateEnginePaths.BlendshapesDir;
    }

    private string GetSavePath()
    {
        string safeName = MakeFileNameSafe(string.IsNullOrEmpty(currentAvatarName) ? "DefaultAvatar" : currentAvatarName);
        return Path.Combine(GetSaveFolder(), $"{safeName}_Blendshapes.json");
    }

    private static string MakeFileNameSafe(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    private void SaveToDisk()
    {
        try
        {
            var json = JsonConvert.SerializeObject(blendValues, Formatting.Indented);
            File.WriteAllText(GetSavePath(), json);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[BlendshapeManager] Save failed: " + e.Message);
        }
    }

    private void LoadFromDisk()
    {
        string path = GetSavePath();
        if (!File.Exists(path))
            return;
        try
        {
            var json = File.ReadAllText(path);
            var dict = JsonConvert.DeserializeObject<Dictionary<string, float>>(json);
            if (dict != null)
                blendValues = dict;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[BlendshapeManager] Load failed: " + e.Message);
        }
    }

    private Transform ResolveActiveAvatarRoot(out string avatarName)
    {
        avatarName = null;

        var loader = FindFirstObjectByType<VRMLoader>();
        if (loader != null)
        {
            string key = "SavedPathModel";
            if (PlayerPrefs.HasKey(key))
            {
                string savedPath = PlayerPrefs.GetString(key);
                if (!string.IsNullOrEmpty(savedPath))
                    avatarName = Path.GetFileNameWithoutExtension(savedPath);
            }

            var loaderType = typeof(VRMLoader);
            var customModelOutputField = loaderType.GetField("customModelOutput", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            var mainModelField = loaderType.GetField("mainModel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

            Transform root = null;

            var customObj = customModelOutputField?.GetValue(loader) as GameObject;
            var mainObj = mainModelField?.GetValue(loader) as GameObject;

            if (customObj != null)
            {
                foreach (Transform child in customObj.transform)
                {
                    if (child.gameObject.activeInHierarchy)
                    {
                        root = child;
                        break;
                    }
                }
            }

            if (root == null && mainObj != null && mainObj.activeInHierarchy)
                root = mainObj.transform;

            if (root != null && string.IsNullOrEmpty(avatarName))
                avatarName = root.name;

            return root;
        }

        var allSmrs = FindObjectsOfType<SkinnedMeshRenderer>(true)
                      .Where(s => s != null && s.gameObject.activeInHierarchy && s.enabled && s.sharedMesh != null && s.sharedMesh.blendShapeCount > 0)
                      .ToList();
        if (allSmrs.Count > 0)
        {
            Transform bestRoot = null;
            int bestCount = -1;

            foreach (var candidate in allSmrs.Select(s => s.rootBone != null ? s.rootBone : s.transform))
            {
                int cnt = allSmrs.Count(s => IsAncestor(candidate, s.transform));
                if (cnt > bestCount)
                {
                    bestCount = cnt;
                    bestRoot = candidate;
                }
            }

            if (bestRoot != null)
            {
                avatarName = bestRoot.root?.name ?? bestRoot.name;
                return bestRoot;
            }
        }

        avatarName = "DefaultAvatar";
        return null;
    }

    private static bool IsAncestor(Transform ancestor, Transform child)
    {
        var t = child;
        while (t != null)
        {
            if (t == ancestor) return true;
            t = t.parent;
        }
        return false;
    }

    private static string GetTransformPath(Transform t, Transform stopAt)
    {
        var stack = new List<string>();
        var cur = t;
        while (cur != null && cur != stopAt)
        {
            stack.Add(cur.name);
            cur = cur.parent;
        }
        stack.Reverse();
        return string.Join("/", stack);
    }
}
