﻿using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using VRM;
using UniGLTF;
using SFB;

public class VRMLoader : MonoBehaviour
{
    public Button loadVRMButton;
    public GameObject injectModelHere;
    public RuntimeAnimatorController animatorController;
    public FixedPosition fixedPositionScript;
    public AvatarControllerHeadTracking headTrackingScript;
    public AvatarAnimatorController avatarAnimatorScript;

    private GameObject currentModel;
    private bool isLoading = false;

    void Start()
    {
        EnsureVRMDependencies();
        EnsureShadersAreIncluded();

        if (loadVRMButton != null)
        {
            loadVRMButton.onClick.RemoveAllListeners();
            loadVRMButton.onClick.AddListener(OpenFileDialogAndLoadVRM);
        }
    }

    private void EnsureVRMDependencies()
    {
        var importer = new VRMImporterContext(new VRMData(new GlbBinaryParser(new byte[0], "").Parse()));
    }

    private void EnsureShadersAreIncluded()
    {
        Shader.Find("VRM/MToon");
    }

    public void OpenFileDialogAndLoadVRM()
    {
        if (!isLoading)
        {
            isLoading = true;
            OpenFileExplorer();
            isLoading = false;
        }
    }

    void OpenFileExplorer()
    {
        var extensions = new[] { new ExtensionFilter("VRM Files", "vrm") };
        string[] paths = StandaloneFileBrowser.OpenFilePanel("Select VRM Model", "", extensions, false);

        if (paths.Length > 0 && !string.IsNullOrEmpty(paths[0]))
        {
            LoadVRM(paths[0]);
        }
    }

    public async void LoadVRM(string path)
    {
        if (!File.Exists(path)) return;

        try
        {
            byte[] vrmData = await Task.Run(() => File.ReadAllBytes(path));
            if (vrmData == null || vrmData.Length == 0) return;

            using var gltfData = new GlbBinaryParser(vrmData, path).Parse();
            var vrmDataObj = new VRMData(gltfData);
            var importer = new VRMImporterContext(vrmDataObj);
            var instance = await importer.LoadAsync(new ImmediateCaller());

            if (instance.Root == null) return;

            if (injectModelHere != null)
            {
                foreach (Transform child in injectModelHere.transform)
                {
                    Destroy(child.gameObject);
                }
            }

            instance.Root.transform.SetParent(injectModelHere.transform, false);
            instance.Root.transform.localPosition = Vector3.zero;
            instance.Root.transform.localRotation = Quaternion.identity;
            instance.Root.transform.localScale = Vector3.one;

            currentModel = instance.Root;

            EnableSkinnedMeshRenderers(currentModel);
            FixMaterials(currentModel);
            AssignAnimatorController(currentModel);
            AddRequiredComponents(currentModel);
            RenameHeadBone(currentModel);
        }
        catch (System.Exception) { }
    }

    private void EnableSkinnedMeshRenderers(GameObject model)
    {
        SkinnedMeshRenderer[] skinnedMeshes = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        foreach (var skinnedMesh in skinnedMeshes)
        {
            skinnedMesh.enabled = true;
        }
    }

    private void FixMaterials(GameObject model)
    {
        Renderer[] renderers = model.GetComponentsInChildren<Renderer>();
        foreach (var renderer in renderers)
        {
            foreach (var mat in renderer.sharedMaterials)
            {
                if (mat != null && mat.shader.name != "VRM/MToon")
                {
                    mat.shader = Shader.Find("VRM/MToon");
                }
            }
        }
    }

    private void AssignAnimatorController(GameObject model)
    {
        Animator animator = model.GetComponentInChildren<Animator>();
        if (animator != null && animatorController != null)
        {
            animator.runtimeAnimatorController = animatorController;
        }
    }

    private void AddRequiredComponents(GameObject model)
    {
        if (fixedPositionScript != null && model.GetComponent<FixedPosition>() == null)
        {
            model.AddComponent<FixedPosition>();
        }

        if (headTrackingScript != null && model.GetComponent<AvatarControllerHeadTracking>() == null)
        {
            model.AddComponent<AvatarControllerHeadTracking>();
        }

        if (avatarAnimatorScript != null && model.GetComponent<AvatarAnimatorController>() == null)
        {
            model.AddComponent<AvatarAnimatorController>();
        }
    }

    private void RenameHeadBone(GameObject model)
    {
        Animator animator = model.GetComponent<Animator>();
        if (animator == null || !animator.isHuman) return;

        Transform headBone = animator.GetBoneTransform(HumanBodyBones.Head);
        if (headBone != null && headBone.name != "HEAD")
        {
            headBone.name = "HEAD";
        }
    }
}
