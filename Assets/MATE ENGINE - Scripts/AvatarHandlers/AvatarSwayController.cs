using System;
using UnityEngine;

public class AvatarSwayController : MonoBehaviour
{
    [Header("References")]
    public Animator animator;
    public string draggingParam = "isDragging";
    public string windowSitParam = "isWindowSit";

    [Header("Space")]
    public bool useLocalRotation = true;
    public Transform globalReference;

    [Header("Input")]
    public bool useWindowVelocity = true;
    public bool fallbackToMouse = true;
    public float mouseSensitivity = 0.6f;
    public bool invertHorizontal = false;
    public bool invertVertical = false;

    [Header("Sway Physics")]
    public float horizontalVelocityToLean = 0.25f;
    public float verticalVelocityToPitch = 0.15f;
    public float maxLeanZ = 25f;
    public float maxLeanX = 12f;
    public float springFrequency = 2.6f;
    public float dampingRatio = 0.35f;
    public float blendSpeed = 8f;

    [Header("Limb Additive")]
    [Range(0f, 1f)] public float armsAdditive = 0f;
    [Range(0f, 1f)] public float legsAdditive = 0f;
    public bool invertArms = false;
    public bool invertLegs = false;
    public float armsMaxZ = 18f;
    public float armsMaxX = 8f;
    public float legsMaxZ = 12f;
    public float legsMaxX = 6f;
    public float limbLag = 6f;

    [Header("State Whitelist")]
    public bool useAllowedStatesWhitelist = false;
    public string[] allowedStates = { "Drag" };
    public int stateLayerIndex = 0;

    [Header("Stability")]
    public bool neutralizeEveryUpdate = true;
    public bool disableWhileWindowSit = true;

    Animator anim;
    int draggingHash;
    int windowSitHash;
    Animator cachedForBones;

    Transform hips;
    Transform leftUpperArm;
    Transform rightUpperArm;
    Transform leftUpperLeg;
    Transform rightUpperLeg;

    float leanZ;
    float leanZVel;
    float leanX;
    float leanXVel;
    float effectWeight;

    float limbZ;
    float limbX;

    Vector2 filteredDelta;
    Vector2 prevMousePos;

    Quaternion lastHipAddLocal = Quaternion.identity;
    Quaternion lastArmLAddLocal = Quaternion.identity;
    Quaternion lastArmRAddLocal = Quaternion.identity;
    Quaternion lastLegLAddLocal = Quaternion.identity;
    Quaternion lastLegRAddLocal = Quaternion.identity;

    Quaternion lastHipAddWorld = Quaternion.identity;
    Quaternion lastArmLAddWorld = Quaternion.identity;
    Quaternion lastArmRAddWorld = Quaternion.identity;
    Quaternion lastLegLAddWorld = Quaternion.identity;
    Quaternion lastLegRAddWorld = Quaternion.identity;

    IDesktopWindowApi windowApi;
    Vector2Int prevWinPos;

    void Awake()
    {
        draggingHash = Animator.StringToHash(draggingParam);
        windowSitHash = Animator.StringToHash(windowSitParam);
        prevMousePos = Input.mousePosition;
        windowApi = DesktopWindowApi.Current;
        if (windowApi.IsSupported && windowApi.TryGetOwnWindowRect(out DesktopRect rect))
            prevWinPos = new Vector2Int(rect.Left, rect.Top);
    }

    void OnDisable()
    {
        ClearPreviousAdditivesIfAny();
    }

    void Update()
    {
        EnsureAnimatorAndBones();
        if (!anim || !hips) return;

        if (neutralizeEveryUpdate) ClearPreviousAdditivesIfAny();

        bool dragging = anim.GetBool(draggingHash);
        bool sitting = anim.GetBool(windowSitHash);
        bool whitelisted = IsInAllowedState();
        bool active = dragging && whitelisted && !(disableWhileWindowSit && sitting);

        float dt = Time.deltaTime;
        Vector2 delta = Vector2.zero;

        if (useWindowVelocity && windowApi != null && windowApi.IsSupported && active)
        {
            if (!windowApi.TryGetOwnWindowRect(out DesktopRect rect)) rect = new DesktopRect(prevWinPos.x, prevWinPos.y, prevWinPos.x, prevWinPos.y);
            Vector2Int wp = new Vector2Int(rect.Left, rect.Top);
            Vector2Int d = wp - prevWinPos;
            prevWinPos = wp;
            delta = new Vector2(d.x, d.y);
        }
        if (delta == Vector2.zero && fallbackToMouse && dragging)
        {
            Vector2 m = Input.mousePosition;
            Vector2 md = (m - prevMousePos) * mouseSensitivity;
            prevMousePos = m;
            delta = md;
        }
        else
        {
            prevMousePos = Input.mousePosition;
        }

        filteredDelta = Vector2.Lerp(filteredDelta, delta, 1f - Mathf.Exp(-12f * dt));

        float signH = invertHorizontal ? 1f : -1f;
        float signV = invertVertical ? -1f : 1f;

        float targetLeanZ = Mathf.Clamp(signH * filteredDelta.x * horizontalVelocityToLean, -maxLeanZ, maxLeanZ);
        float targetLeanX = Mathf.Clamp(signV * filteredDelta.y * verticalVelocityToPitch, -maxLeanX, maxLeanX);

        Spring(ref leanZ, ref leanZVel, active ? targetLeanZ : 0f, springFrequency, dampingRatio, dt);
        Spring(ref leanX, ref leanXVel, active ? targetLeanX : 0f, springFrequency, dampingRatio, dt);

        limbZ = Mathf.Lerp(limbZ, -leanZ, 1f - Mathf.Exp(-limbLag * dt));
        limbX = Mathf.Lerp(limbX, -leanX, 1f - Mathf.Exp(-limbLag * dt));

        float outSpeed = active ? blendSpeed : blendSpeed * 2f;
        effectWeight = Mathf.MoveTowards(effectWeight, active ? 1f : 0f, outSpeed * dt);
    }

    void LateUpdate()
    {
        if (!anim || !hips) return;
        if (effectWeight <= 0.0001f) { ClearPreviousAdditivesIfAny(); return; }

        float xH = leanX * effectWeight;
        float zH = leanZ * effectWeight;

        if (useLocalRotation)
        {
            Quaternion addLocal = Quaternion.Euler(xH, 0f, zH);
            hips.localRotation = hips.localRotation * addLocal;
            lastHipAddLocal = addLocal;
            if (armsAdditive > 0f)
            {
                float sA = invertArms ? -1f : 1f;
                float xA = Mathf.Clamp(limbX * armsAdditive, -armsMaxX, armsMaxX) * sA * effectWeight;
                float zA = Mathf.Clamp(limbZ * armsAdditive, -armsMaxZ, armsMaxZ) * sA * effectWeight;
                Quaternion addA = Quaternion.Euler(xA, 0f, zA);
                if (leftUpperArm) { leftUpperArm.localRotation = leftUpperArm.localRotation * addA; lastArmLAddLocal = addA; } else lastArmLAddLocal = Quaternion.identity;
                if (rightUpperArm) { rightUpperArm.localRotation = rightUpperArm.localRotation * addA; lastArmRAddLocal = addA; } else lastArmRAddLocal = Quaternion.identity;
            }
            else
            {
                lastArmLAddLocal = Quaternion.identity;
                lastArmRAddLocal = Quaternion.identity;
            }

            if (legsAdditive > 0f)
            {
                float sL = invertLegs ? -1f : 1f;
                float xL = Mathf.Clamp(limbX * legsAdditive, -legsMaxX, legsMaxX) * sL * effectWeight;
                float zL = Mathf.Clamp(limbZ * legsAdditive, -legsMaxZ, legsMaxZ) * sL * effectWeight;
                Quaternion addL = Quaternion.Euler(xL, 0f, zL);
                if (leftUpperLeg) { leftUpperLeg.localRotation = leftUpperLeg.localRotation * addL; lastLegLAddLocal = addL; } else lastLegLAddLocal = Quaternion.identity;
                if (rightUpperLeg) { rightUpperLeg.localRotation = rightUpperLeg.localRotation * addL; lastLegRAddLocal = addL; } else lastLegRAddLocal = Quaternion.identity;
            }
            else
            {
                lastLegLAddLocal = Quaternion.identity;
                lastLegRAddLocal = Quaternion.identity;
            }

            lastHipAddWorld = Quaternion.identity;
            lastArmLAddWorld = Quaternion.identity;
            lastArmRAddWorld = Quaternion.identity;
            lastLegLAddWorld = Quaternion.identity;
            lastLegRAddWorld = Quaternion.identity;
        }
        else
        {
            Transform space = globalReference ? globalReference : transform;
            Quaternion addWorldH = Quaternion.AngleAxis(xH, space.right) * Quaternion.AngleAxis(zH, space.forward);
            hips.rotation = addWorldH * hips.rotation;
            lastHipAddWorld = addWorldH;
            if (armsAdditive > 0f)
            {
                float sA = invertArms ? -1f : 1f;
                float xA = Mathf.Clamp(limbX * armsAdditive, -armsMaxX, armsMaxX) * sA * effectWeight;
                float zA = Mathf.Clamp(limbZ * armsAdditive, -armsMaxZ, armsMaxZ) * sA * effectWeight;
                Quaternion addWorldA = Quaternion.AngleAxis(xA, space.right) * Quaternion.AngleAxis(zA, space.forward);
                if (leftUpperArm) { leftUpperArm.rotation = addWorldA * leftUpperArm.rotation; lastArmLAddWorld = addWorldA; } else lastArmLAddWorld = Quaternion.identity;
                if (rightUpperArm) { rightUpperArm.rotation = addWorldA * rightUpperArm.rotation; lastArmRAddWorld = addWorldA; } else lastArmRAddWorld = Quaternion.identity;
            }
            else
            {
                lastArmLAddWorld = Quaternion.identity;
                lastArmRAddWorld = Quaternion.identity;
            }

            if (legsAdditive > 0f)
            {
                float sL = invertLegs ? -1f : 1f;
                float xL = Mathf.Clamp(limbX * legsAdditive, -legsMaxX, legsMaxX) * sL * effectWeight;
                float zL = Mathf.Clamp(limbZ * legsAdditive, -legsMaxZ, legsMaxZ) * sL * effectWeight;
                Quaternion addWorldL = Quaternion.AngleAxis(xL, space.right) * Quaternion.AngleAxis(zL, space.forward);
                if (leftUpperLeg) { leftUpperLeg.rotation = addWorldL * leftUpperLeg.rotation; lastLegLAddWorld = addWorldL; } else lastLegLAddWorld = Quaternion.identity;
                if (rightUpperLeg) { rightUpperLeg.rotation = addWorldL * rightUpperLeg.rotation; lastLegRAddWorld = addWorldL; } else lastLegRAddWorld = Quaternion.identity;
            }
            else
            {
                lastLegLAddWorld = Quaternion.identity;
                lastLegRAddWorld = Quaternion.identity;
            }

            lastHipAddLocal = Quaternion.identity;
            lastArmLAddLocal = Quaternion.identity;
            lastArmRAddLocal = Quaternion.identity;
            lastLegLAddLocal = Quaternion.identity;
            lastLegRAddLocal = Quaternion.identity;
        }
    }

    void EnsureAnimatorAndBones()
    {
        if (!animator)
        {
            Animator p = GetComponentInParent<Animator>();
            if (p) animator = p;
        }
        if (anim != animator)
        {
            anim = animator;
            cachedForBones = null;
        }
        if (!anim) return;
        if (cachedForBones != anim || !hips)
        {
            hips = anim.GetBoneTransform(HumanBodyBones.Hips);
            leftUpperArm = anim.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            rightUpperArm = anim.GetBoneTransform(HumanBodyBones.RightUpperArm);
            leftUpperLeg = anim.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            rightUpperLeg = anim.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            cachedForBones = anim;
            ClearPreviousAdditivesIfAny();
        }
    }

    bool IsInAllowedState()
    {
        if (!useAllowedStatesWhitelist) return true;
        if (!anim) return false;
        if (allowedStates == null || allowedStates.Length == 0) return true;
        AnimatorStateInfo current = anim.GetCurrentAnimatorStateInfo(Mathf.Clamp(stateLayerIndex, 0, Mathf.Max(0, anim.layerCount - 1)));
        for (int i = 0; i < allowedStates.Length; i++)
        {
            string s = allowedStates[i];
            if (!string.IsNullOrEmpty(s) && current.IsName(s)) return true;
        }
        return false;
    }

    static void Spring(ref float x, ref float v, float xt, float f, float z, float dt)
    {
        float w = Mathf.Max(0.01f, f) * 2f * Mathf.PI;
        float a = w * w * (xt - x) - 2f * z * w * v;
        v += a * dt;
        x += v * dt;
    }

    void ClearPreviousAdditivesIfAny()
    {
        if (useLocalRotation)
        {
            if (hips && lastHipAddLocal != Quaternion.identity) hips.localRotation = hips.localRotation * Quaternion.Inverse(lastHipAddLocal);
            if (leftUpperArm && lastArmLAddLocal != Quaternion.identity) leftUpperArm.localRotation = leftUpperArm.localRotation * Quaternion.Inverse(lastArmLAddLocal);
            if (rightUpperArm && lastArmRAddLocal != Quaternion.identity) rightUpperArm.localRotation = rightUpperArm.localRotation * Quaternion.Inverse(lastArmRAddLocal);
            if (leftUpperLeg && lastLegLAddLocal != Quaternion.identity) leftUpperLeg.localRotation = leftUpperLeg.localRotation * Quaternion.Inverse(lastLegLAddLocal);
            if (rightUpperLeg && lastLegRAddLocal != Quaternion.identity) rightUpperLeg.localRotation = rightUpperLeg.localRotation * Quaternion.Inverse(lastLegRAddLocal);
            lastHipAddLocal = Quaternion.identity;
            lastArmLAddLocal = Quaternion.identity;
            lastArmRAddLocal = Quaternion.identity;
            lastLegLAddLocal = Quaternion.identity;
            lastLegRAddLocal = Quaternion.identity;
        }
        else
        {
            if (hips && lastHipAddWorld != Quaternion.identity) hips.rotation = Quaternion.Inverse(lastHipAddWorld) * hips.rotation;
            if (leftUpperArm && lastArmLAddWorld != Quaternion.identity) leftUpperArm.rotation = Quaternion.Inverse(lastArmLAddWorld) * leftUpperArm.rotation;
            if (rightUpperArm && lastArmRAddWorld != Quaternion.identity) rightUpperArm.rotation = Quaternion.Inverse(lastArmRAddWorld) * rightUpperArm.rotation;
            if (leftUpperLeg && lastLegLAddWorld != Quaternion.identity) leftUpperLeg.rotation = Quaternion.Inverse(lastLegLAddWorld) * leftUpperLeg.rotation;
            if (rightUpperLeg && lastLegRAddWorld != Quaternion.identity) rightUpperLeg.rotation = Quaternion.Inverse(lastLegRAddWorld) * rightUpperLeg.rotation;
            lastHipAddWorld = Quaternion.identity;
            lastArmLAddWorld = Quaternion.identity;
            lastArmRAddWorld = Quaternion.identity;
            lastLegLAddWorld = Quaternion.identity;
            lastLegRAddWorld = Quaternion.identity;
        }
    }
}
