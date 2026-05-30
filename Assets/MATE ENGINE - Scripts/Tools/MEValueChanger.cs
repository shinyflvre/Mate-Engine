using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Globalization;
using System.IO;
using UnityEngine.Networking;

[Serializable]
public class MEValueRuntimeEntry
{
    public GameObject targetObject;
    [NonSerialized] public bool foldout;
    [NonSerialized] public Component[] comps;
    [NonSerialized] public string[] compNames;
    [NonSerialized] public Dictionary<Component, List<MemberEntry>> membersPerComp = new Dictionary<Component, List<MemberEntry>>();
    [NonSerialized] public Dictionary<Component, bool> compFoldouts = new Dictionary<Component, bool>();
    [NonSerialized] public Vector2 scrollMembers;
    [NonSerialized] public bool showAnimatorParams;
}

public class MemberEntry
{
    public MemberInfo member;
    public string displayName;
    public Type type;

    public string editCache;
    public bool isColor, isColor32, isEnum, isAudioClip, isV2, isV3, isV4, isQuat, isRect;
    public string cr, cg, cb, ca;
    public string v2x, v2y, v3x, v3y, v3z, v4x, v4y, v4z, v4w, qx, qy, qz, qw, rx, ry, rw, rh;

    public string[] enumNames;
    public Array enumValues;
    public int enumIndex;

    public bool hasRange;
    public float rangeMin, rangeMax;

    public string audioPathCache;
    public bool audioLoading;
    public string audioStatus;
}

[Serializable]
public class ProfileFile
{
    public List<PerTarget> targets = new List<PerTarget>();
}

[Serializable]
public class PerTarget
{
    public int targetIndex;
    public int compIndex;
    public string compTypeName;
    public List<MemberKV> members = new List<MemberKV>();
}

[Serializable]
public class MemberKV
{
    public string memberName;
    public bool isProperty;
    public string kind;

    public float f;
    public int i;
    public bool b;
    public string s;

    public float rf, gf, bf, af;
    public int r8, g8, b8, a8;

    public float v2x, v2y;
    public float v3x, v3y, v3z;
    public float v4x, v4y, v4z, v4w;
    public float qx, qy, qz, qw;
    public float rx, ry, rw, rh;

    public string enumType;
    public string enumName;

    public string audioPath;
}

[Serializable]
public class SettingsData
{
    public string profileName = "Default";
    public bool autoLoadOnStart = false;
}

public class MEValueChanger : MonoBehaviour
{
    [Header("Targets")]
    [SerializeField] private List<MEValueRuntimeEntry> targets = new List<MEValueRuntimeEntry>();

    [Header("Runtime UI")]
    [SerializeField] private bool showUIOnStart = false;
    [SerializeField] private KeyCode toggleKey = KeyCode.F8;
    [SerializeField] private Rect windowRect = new Rect(40, 40, 720, 720);

    [Header("Profiles")]
    [SerializeField] private string profileName = "Default";
    [SerializeField] private bool autoLoadOnStart = false;

    [Header("Deactivate While Open")]
    [SerializeField] private List<GameObject> deactivateWhileOpen = new List<GameObject>();

    private bool showUI;
    private Vector2 mainScroll;
    private string status;
    private float statusUntil;

    private static Texture2D whiteTex;

    private readonly Dictionary<GameObject, bool> prevActive = new Dictionary<GameObject, bool>();

    void Start()
    {
        showUI = showUIOnStart;
        LoadSettings();
        if (autoLoadOnStart) TryAutoLoad();
        TryAttachCustomVRM();
        if (showUI) ApplyDeactivateWhileOpen(true);
    }

    void TryAttachCustomVRM()
    {
        var loader = FindFirstObjectByType<VRMLoader>();
        if (loader != null)
        {
            var clone = loader.GetCurrentModel();
            if (clone != null && !targets.Exists(t => t.targetObject == clone))
            {
                var newEntry = new MEValueRuntimeEntry { targetObject = clone };
                targets.Add(newEntry);
                RefreshTargetLists(newEntry);
            }
        }
    }

    void Update()
    {
        if (Input.GetKeyDown(toggleKey))
        {
            showUI = !showUI;
            ApplyDeactivateWhileOpen(showUI);
        }
    }

    void OnGUI()
    {
        if (!Application.isPlaying) return;
        if (!showUI) return;
        windowRect = GUI.Window(GetInstanceID(), windowRect, DrawWindow, "ME Value Changer (Runtime)");
    }

    void DrawWindow(int id)
    {
        GUILayout.Label("Playmode only. Changes are not saved unless you Save a profile.", GUI.skin.box);

        GUILayout.BeginHorizontal();
        GUILayout.Label("Profile", GUILayout.Width(60));
        profileName = GUILayout.TextField(profileName, GUILayout.MinWidth(160));
        if (GUILayout.Button("Save", GUILayout.Width(80))) { SaveCurrentProfile(); SaveSettings(); }
        if (GUILayout.Button("Load", GUILayout.Width(80))) { LoadProfile(profileName, true); SaveSettings(); }
        bool newAuto = GUILayout.Toggle(autoLoadOnStart, "Auto Load on Start", GUILayout.Width(180));
        if (newAuto != autoLoadOnStart) { autoLoadOnStart = newAuto; SaveSettings(); }
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Refresh All", GUILayout.Width(110)))
        {
            for (int i = 0; i < targets.Count; i++) RefreshTargetLists(targets[i]);
        }
        GUILayout.EndHorizontal();

        if (!string.IsNullOrEmpty(status) && Time.realtimeSinceStartup < statusUntil)
            GUILayout.Label(status, GUI.skin.label);

        mainScroll = GUILayout.BeginScrollView(mainScroll);

        for (int i = 0; i < targets.Count; i++)
        {
            var e = targets[i];
            GUILayout.BeginVertical(GUI.skin.window);
            e.foldout = Foldout(e.foldout, e.targetObject ? e.targetObject.name : "(None)");
            if (e.foldout)
            {
                GUILayout.Space(4);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Target", GUILayout.Width(80));
                GUILayout.Label(e.targetObject ? e.targetObject.name : "None", GUI.skin.textField);
                GUILayout.EndHorizontal();

                if (e.targetObject == null)
                {
                    GUILayout.Label("Assign a GameObject in the Inspector.");
                }
                else
                {
                    if (e.comps == null || e.compNames == null) BuildComponentList(e);

                    if (e.comps != null)
                    {
                        for (int c = 0; c < e.comps.Length; c++)
                        {
                            var comp = e.comps[c];
                            if (!e.compFoldouts.ContainsKey(comp)) e.compFoldouts[comp] = false;
                            e.compFoldouts[comp] = Foldout(e.compFoldouts[comp], comp ? comp.GetType().Name : "(Missing)");

                            if (e.compFoldouts[comp])
                            {
                                if (!e.membersPerComp.ContainsKey(comp) || e.membersPerComp[comp] == null)
                                    BuildMemberList(e, comp);

                                GUILayout.Space(6);
                                GUILayout.Label("Editable Fields / Properties", GUI.skin.box);

                                e.scrollMembers = GUILayout.BeginScrollView(e.scrollMembers, GUILayout.MinHeight(240));
                                var list = e.membersPerComp.ContainsKey(comp) ? e.membersPerComp[comp] : null;
                                if (list != null && list.Count > 0)
                                {
                                    for (int m = 0; m < list.Count; m++)
                                    {
                                        DrawMemberRow(comp, list[m]);
                                    }
                                }
                                else
                                {
                                    GUILayout.Label("No editable members found.");
                                }
                                GUILayout.EndScrollView();

                                var anim = comp as Animator;
                                if (anim != null)
                                {
                                    GUILayout.Space(6);
                                    e.showAnimatorParams = Foldout(e.showAnimatorParams, "Animator Parameters");
                                    if (e.showAnimatorParams) DrawAnimatorParams(anim);
                                }
                            }
                        }
                    }
                }
            }
            GUILayout.EndVertical();
            GUILayout.Space(6);
        }

        GUILayout.EndScrollView();

        GUI.DragWindow(new Rect(0, 0, 10000, 20));
    }

    void TryAutoLoad()
    {
        LoadProfile(profileName, false);
    }

    void RefreshTargetLists(MEValueRuntimeEntry e)
    {
        e.comps = null; e.compNames = null;
        e.membersPerComp.Clear();
        e.compFoldouts.Clear();
        if (e.targetObject != null) BuildComponentList(e);
    }

    void BuildComponentList(MEValueRuntimeEntry e)
    {
        if (e.targetObject == null) return;
        e.comps = e.targetObject.GetComponents<Component>();
        var names = new List<string>(e.comps.Length);
        for (int j = 0; j < e.comps.Length; j++) names.Add(e.comps[j] ? e.comps[j].GetType().Name : "(Missing)");
        e.compNames = names.ToArray();
    }

    void BuildMemberList(MEValueRuntimeEntry e, Component comp)
    {
        if (!comp) return;
        var list = new List<MemberEntry>();
        var t = comp.GetType();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;

        foreach (var f in t.GetFields(flags)) BuildMemberFromInfo(list, comp, f, f.FieldType);
        foreach (var p in t.GetProperties(flags))
        {
            if (!p.CanWrite || !p.CanRead) continue;
            if (p.GetIndexParameters()?.Length > 0) continue;
            BuildMemberFromInfo(list, comp, p, p.PropertyType);
        }

        e.membersPerComp[comp] = list;
    }

    void BuildMemberFromInfo(List<MemberEntry> list, Component comp, MemberInfo mi, Type mt)
    {
        if (!IsSupportedType(mt)) return;
        var me = new MemberEntry { member = mi, type = mt, displayName = (mi is FieldInfo ? "F: " : "P: ") + mi.Name + " (" + SimpleTypeName(mt) + ")" };
        var attrs = mi is FieldInfo fiA ? fiA.GetCustomAttributes(true) : (mi as PropertyInfo).GetCustomAttributes(true);
        for (int i = 0; i < attrs.Length; i++)
        {
            var ra = attrs[i] as RangeAttribute;
            if (ra != null)
            {
                me.hasRange = true;
                me.rangeMin = ra.min;
                me.rangeMax = ra.max;
                break;
            }
        }

        if (mt == typeof(Color) || mt == typeof(Color32))
        {
            me.isColor = mt == typeof(Color);
            me.isColor32 = mt == typeof(Color32);
            if (me.isColor)
            {
                var c = (Color)GetMemberValue(mi, comp);
                me.cr = c.r.ToString("0.###", CultureInfo.InvariantCulture);
                me.cg = c.g.ToString("0.###", CultureInfo.InvariantCulture);
                me.cb = c.b.ToString("0.###", CultureInfo.InvariantCulture);
                me.ca = c.a.ToString("0.###", CultureInfo.InvariantCulture);
            }
            else
            {
                var c32 = (Color32)GetMemberValue(mi, comp);
                me.cr = c32.r.ToString(CultureInfo.InvariantCulture);
                me.cg = c32.g.ToString(CultureInfo.InvariantCulture);
                me.cb = c32.b.ToString(CultureInfo.InvariantCulture);
                me.ca = c32.a.ToString(CultureInfo.InvariantCulture);
            }
        }
        else if (mt == typeof(string))
        {
            me.editCache = (string)GetMemberValue(mi, comp) ?? "";
        }
        else if (mt == typeof(Vector2))
        {
            me.isV2 = true;
            var v = (Vector2)GetMemberValue(mi, comp);
            me.v2x = v.x.ToString("0.###", CultureInfo.InvariantCulture);
            me.v2y = v.y.ToString("0.###", CultureInfo.InvariantCulture);
        }
        else if (mt == typeof(Vector3))
        {
            me.isV3 = true;
            var v = (Vector3)GetMemberValue(mi, comp);
            me.v3x = v.x.ToString("0.###", CultureInfo.InvariantCulture);
            me.v3y = v.y.ToString("0.###", CultureInfo.InvariantCulture);
            me.v3z = v.z.ToString("0.###", CultureInfo.InvariantCulture);
        }
        else if (mt == typeof(Vector4))
        {
            me.isV4 = true;
            var v = (Vector4)GetMemberValue(mi, comp);
            me.v4x = v.x.ToString("0.###", CultureInfo.InvariantCulture);
            me.v4y = v.y.ToString("0.###", CultureInfo.InvariantCulture);
            me.v4z = v.z.ToString("0.###", CultureInfo.InvariantCulture);
            me.v4w = v.w.ToString("0.###", CultureInfo.InvariantCulture);
        }
        else if (mt == typeof(Quaternion))
        {
            me.isQuat = true;
            var q = (Quaternion)GetMemberValue(mi, comp);
            me.qx = q.x.ToString("0.###", CultureInfo.InvariantCulture);
            me.qy = q.y.ToString("0.###", CultureInfo.InvariantCulture);
            me.qz = q.z.ToString("0.###", CultureInfo.InvariantCulture);
            me.qw = q.w.ToString("0.###", CultureInfo.InvariantCulture);
        }
        else if (mt == typeof(Rect))
        {
            me.isRect = true;
            var r = (Rect)GetMemberValue(mi, comp);
            me.rx = r.x.ToString("0.###", CultureInfo.InvariantCulture);
            me.ry = r.y.ToString("0.###", CultureInfo.InvariantCulture);
            me.rw = r.width.ToString("0.###", CultureInfo.InvariantCulture);
            me.rh = r.height.ToString("0.###", CultureInfo.InvariantCulture);
        }
        else if (mt.IsEnum)
        {
            me.isEnum = true;
            me.enumValues = Enum.GetValues(mt);
            me.enumNames = Enum.GetNames(mt);
            var cur = GetMemberValue(mi, comp);
            me.enumIndex = Array.IndexOf(me.enumValues, cur);
            if (me.enumIndex < 0) me.enumIndex = 0;
        }
        else if (mt == typeof(AudioClip))
        {
            me.isAudioClip = true;
            me.audioPathCache = "";
            me.audioStatus = "";
        }
        else
        {
            me.editCache = GetValueString(comp, mi, mt);
        }

        list.Add(me);
    }

    static bool IsSupportedType(Type tp)
    {
        if (tp == typeof(float) || tp == typeof(int) || tp == typeof(bool) || tp == typeof(string)) return true;
        if (tp == typeof(Color) || tp == typeof(Color32)) return true;
        if (tp == typeof(Vector2) || tp == typeof(Vector3) || tp == typeof(Vector4)) return true;
        if (tp == typeof(Quaternion) || tp == typeof(Rect)) return true;
        if (tp == typeof(AudioClip)) return true;
        if (tp.IsEnum) return true;
        return false;
    }

    static string SimpleTypeName(Type t)
    {
        if (t == typeof(float)) return "float";
        if (t == typeof(int)) return "int";
        if (t == typeof(bool)) return "bool";
        if (t == typeof(string)) return "string";
        if (t == typeof(Color)) return "Color";
        if (t == typeof(Color32)) return "Color32";
        if (t == typeof(Vector2)) return "Vector2";
        if (t == typeof(Vector3)) return "Vector3";
        if (t == typeof(Vector4)) return "Vector4";
        if (t == typeof(Quaternion)) return "Quaternion";
        if (t == typeof(Rect)) return "Rect";
        if (t == typeof(AudioClip)) return "AudioClip";
        if (t.IsEnum) return "Enum";
        return t.Name;
    }

    static object GetMemberValue(MemberInfo m, Component c)
    {
        if (m is FieldInfo fi) return fi.GetValue(c);
        if (m is PropertyInfo pi) return pi.GetValue(c, null);
        return null;
    }

    static void SetMemberValue(MemberInfo m, Component c, object value)
    {
        if (m is FieldInfo fi) fi.SetValue(c, value);
        else if (m is PropertyInfo pi && pi.CanWrite) pi.SetValue(c, value, null);
    }

    static string GetValueString(Component c, MemberInfo m, Type tp)
    {
        var v = GetMemberValue(m, c);
        if (tp == typeof(float)) return ((float)v).ToString("0.###", CultureInfo.InvariantCulture);
        if (tp == typeof(int)) return ((int)v).ToString(CultureInfo.InvariantCulture);
        if (tp == typeof(bool)) return ((bool)v) ? "true" : "false";
        if (tp == typeof(string)) return v as string ?? "";
        return v != null ? v.ToString() : "null";
    }

    void DrawMemberRow(Component comp, MemberEntry entry)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(entry.displayName, GUILayout.Width(300));

        if (entry.isColor)
        {
            if (whiteTex == null) whiteTex = MakeTex(Color.white);
            float.TryParse(entry.cr, NumberStyles.Float, CultureInfo.InvariantCulture, out float rr);
            float.TryParse(entry.cg, NumberStyles.Float, CultureInfo.InvariantCulture, out float gg);
            float.TryParse(entry.cb, NumberStyles.Float, CultureInfo.InvariantCulture, out float bb);
            float.TryParse(entry.ca, NumberStyles.Float, CultureInfo.InvariantCulture, out float aa);
            rr = Mathf.Clamp01(rr); gg = Mathf.Clamp01(gg); bb = Mathf.Clamp01(bb); aa = Mathf.Clamp01(aa);
            var col = new Color(rr, gg, bb, aa);
            var sw = GUILayoutUtility.GetRect(32, 18, GUILayout.Width(32));
            var old = GUI.color;
            GUI.color = col; GUI.DrawTexture(sw, whiteTex); GUI.color = old;
            entry.cr = GUILayout.TextField(rr.ToString("0.###", CultureInfo.InvariantCulture), GUILayout.Width(60));
            entry.cg = GUILayout.TextField(gg.ToString("0.###", CultureInfo.InvariantCulture), GUILayout.Width(60));
            entry.cb = GUILayout.TextField(bb.ToString("0.###", CultureInfo.InvariantCulture), GUILayout.Width(60));
            entry.ca = GUILayout.TextField(aa.ToString("0.###", CultureInfo.InvariantCulture), GUILayout.Width(60));
            SetMemberValue(entry.member, comp, col);
        }
        else if (entry.isColor32)
        {
            if (whiteTex == null) whiteTex = MakeTex(Color.white);
            int.TryParse(entry.cr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int r8);
            int.TryParse(entry.cg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int g8);
            int.TryParse(entry.cb, NumberStyles.Integer, CultureInfo.InvariantCulture, out int b8);
            int.TryParse(entry.ca, NumberStyles.Integer, CultureInfo.InvariantCulture, out int a8);
            r8 = Mathf.Clamp(r8, 0, 255); g8 = Mathf.Clamp(g8, 0, 255); b8 = Mathf.Clamp(b8, 0, 255); a8 = Mathf.Clamp(a8, 0, 255);
            var c32 = new Color32((byte)r8, (byte)g8, (byte)b8, (byte)a8);
            var sw = GUILayoutUtility.GetRect(32, 18, GUILayout.Width(32));
            var old = GUI.color;
            GUI.color = new Color(c32.r / 255f, c32.g / 255f, c32.b / 255f, 1f);
            GUI.DrawTexture(sw, whiteTex); GUI.color = old;
            entry.cr = GUILayout.TextField(r8.ToString(CultureInfo.InvariantCulture), GUILayout.Width(60));
            entry.cg = GUILayout.TextField(g8.ToString(CultureInfo.InvariantCulture), GUILayout.Width(60));
            entry.cb = GUILayout.TextField(b8.ToString(CultureInfo.InvariantCulture), GUILayout.Width(60));
            entry.ca = GUILayout.TextField(a8.ToString(CultureInfo.InvariantCulture), GUILayout.Width(60));
            SetMemberValue(entry.member, comp, c32);
        }
        else if (entry.isV2)
        {
            entry.v2x = GUILayout.TextField(entry.v2x, GUILayout.Width(70));
            entry.v2y = GUILayout.TextField(entry.v2y, GUILayout.Width(70));
            if (float.TryParse(entry.v2x, NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                float.TryParse(entry.v2y, NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
            {
                SetMemberValue(entry.member, comp, new Vector2(x, y));
            }
        }
        else if (entry.isV3)
        {
            entry.v3x = GUILayout.TextField(entry.v3x, GUILayout.Width(70));
            entry.v3y = GUILayout.TextField(entry.v3y, GUILayout.Width(70));
            entry.v3z = GUILayout.TextField(entry.v3z, GUILayout.Width(70));
            if (float.TryParse(entry.v3x, NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                float.TryParse(entry.v3y, NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                float.TryParse(entry.v3z, NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
            {
                SetMemberValue(entry.member, comp, new Vector3(x, y, z));
            }
        }
        else if (entry.isV4)
        {
            entry.v4x = GUILayout.TextField(entry.v4x, GUILayout.Width(60));
            entry.v4y = GUILayout.TextField(entry.v4y, GUILayout.Width(60));
            entry.v4z = GUILayout.TextField(entry.v4z, GUILayout.Width(60));
            entry.v4w = GUILayout.TextField(entry.v4w, GUILayout.Width(60));
            if (float.TryParse(entry.v4x, NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                float.TryParse(entry.v4y, NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                float.TryParse(entry.v4z, NumberStyles.Float, CultureInfo.InvariantCulture, out float z) &&
                float.TryParse(entry.v4w, NumberStyles.Float, CultureInfo.InvariantCulture, out float w))
            {
                SetMemberValue(entry.member, comp, new Vector4(x, y, z, w));
            }
        }
        else if (entry.isQuat)
        {
            entry.qx = GUILayout.TextField(entry.qx, GUILayout.Width(60));
            entry.qy = GUILayout.TextField(entry.qy, GUILayout.Width(60));
            entry.qz = GUILayout.TextField(entry.qz, GUILayout.Width(60));
            entry.qw = GUILayout.TextField(entry.qw, GUILayout.Width(60));
            if (float.TryParse(entry.qx, NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                float.TryParse(entry.qy, NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                float.TryParse(entry.qz, NumberStyles.Float, CultureInfo.InvariantCulture, out float z) &&
                float.TryParse(entry.qw, NumberStyles.Float, CultureInfo.InvariantCulture, out float w))
            {
                SetMemberValue(entry.member, comp, new Quaternion(x, y, z, w));
            }
        }
        else if (entry.isRect)
        {
            entry.rx = GUILayout.TextField(entry.rx, GUILayout.Width(60));
            entry.ry = GUILayout.TextField(entry.ry, GUILayout.Width(60));
            entry.rw = GUILayout.TextField(entry.rw, GUILayout.Width(60));
            entry.rh = GUILayout.TextField(entry.rh, GUILayout.Width(60));
            if (float.TryParse(entry.rx, NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                float.TryParse(entry.ry, NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                float.TryParse(entry.rw, NumberStyles.Float, CultureInfo.InvariantCulture, out float w) &&
                float.TryParse(entry.rh, NumberStyles.Float, CultureInfo.InvariantCulture, out float h))
            {
                SetMemberValue(entry.member, comp, new Rect(x, y, w, h));
            }
        }
        else if (entry.isEnum)
        {
            if (GUILayout.Button("◂", GUILayout.Width(28)))
            {
                entry.enumIndex = (entry.enumIndex - 1 + entry.enumNames.Length) % entry.enumNames.Length;
                SetMemberValue(entry.member, comp, entry.enumValues.GetValue(entry.enumIndex));
            }
            GUILayout.Label(entry.enumNames[entry.enumIndex], GUILayout.Width(160));
            if (GUILayout.Button("▸", GUILayout.Width(28)))
            {
                entry.enumIndex = (entry.enumIndex + 1) % entry.enumNames.Length;
                SetMemberValue(entry.member, comp, entry.enumValues.GetValue(entry.enumIndex));
            }
        }
        else if (entry.isAudioClip)
        {
            var curClip = GetMemberValue(entry.member, comp) as AudioClip;
            GUILayout.Label(curClip ? curClip.name : "(none)", GUILayout.Width(150));
            entry.audioPathCache = GUILayout.TextField(entry.audioPathCache ?? "", GUILayout.MinWidth(220), GUILayout.ExpandWidth(true));
            if (GUILayout.Button("Load", GUILayout.Width(70)))
            {
                if (!entry.audioLoading && !string.IsNullOrWhiteSpace(entry.audioPathCache))
                {
                    StartCoroutine(LoadAudioClipFromPath(entry.member, comp, entry.audioPathCache, s => { entry.audioStatus = s; }, clipOk =>
                    {
                        if (clipOk != null) SetMemberValue(entry.member, comp, clipOk);
                    }));
                }
            }
            if (GUILayout.Button("Clear", GUILayout.Width(70)))
            {
                SetMemberValue(entry.member, comp, null);
            }
            if (!string.IsNullOrEmpty(entry.audioStatus)) GUILayout.Label(entry.audioStatus, GUILayout.Width(160));
        }
        else if (entry.type == typeof(string))
        {
            string txt = GUILayout.TextField(entry.editCache ?? "", GUILayout.MinWidth(220), GUILayout.ExpandWidth(true));
            if (!ReferenceEquals(txt, entry.editCache))
            {
                entry.editCache = txt;
                SetMemberValue(entry.member, comp, txt);
            }
        }
        else if (entry.type == typeof(bool))
        {
            bool cur = (bool)GetMemberValue(entry.member, comp);
            bool nb = GUILayout.Toggle(cur, cur ? "true" : "false", GUILayout.Width(80));
            if (nb != cur)
            {
                SetMemberValue(entry.member, comp, nb);
                entry.editCache = nb ? "true" : "false";
            }
        }
        else if (entry.type == typeof(int))
        {
            if (entry.hasRange)
            {
                int cur = (int)GetMemberValue(entry.member, comp);
                float fcur = cur;
                float nf = GUILayout.HorizontalSlider(fcur, entry.rangeMin, entry.rangeMax, GUILayout.Width(160));
                int ni = Mathf.RoundToInt(nf);
                string txt = GUILayout.TextField(ni.ToString(CultureInfo.InvariantCulture), GUILayout.Width(80));
                if (int.TryParse(txt, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv)) ni = Mathf.Clamp(iv, (int)entry.rangeMin, (int)entry.rangeMax);
                if (ni != cur) SetMemberValue(entry.member, comp, ni);
                GUILayout.Label(ni.ToString(CultureInfo.InvariantCulture), GUILayout.Width(50));
            }
            else
            {
                string txt = GUILayout.TextField(entry.editCache ?? GetValueString(comp, entry.member, typeof(int)), GUILayout.MinWidth(120));
                if (!ReferenceEquals(txt, entry.editCache)) entry.editCache = txt;
                if (int.TryParse(entry.editCache, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv))
                {
                    SetMemberValue(entry.member, comp, iv);
                }
            }
        }
        else if (entry.type == typeof(float))
        {
            if (entry.hasRange)
            {
                float cur = (float)GetMemberValue(entry.member, comp);
                float nf = GUILayout.HorizontalSlider(cur, entry.rangeMin, entry.rangeMax, GUILayout.Width(160));
                string txt = GUILayout.TextField(nf.ToString("0.###", CultureInfo.InvariantCulture), GUILayout.Width(80));
                if (float.TryParse(txt, NumberStyles.Float, CultureInfo.InvariantCulture, out float fv)) nf = Mathf.Clamp(fv, entry.rangeMin, entry.rangeMax);
                if (Mathf.Abs(nf - cur) > 1e-6f) SetMemberValue(entry.member, comp, nf);
                GUILayout.Label(nf.ToString("0.###", CultureInfo.InvariantCulture), GUILayout.Width(70));
            }
            else
            {
                string txt = GUILayout.TextField(entry.editCache ?? GetValueString(comp, entry.member, typeof(float)), GUILayout.MinWidth(120));
                if (!ReferenceEquals(txt, entry.editCache)) entry.editCache = txt;
                if (float.TryParse(entry.editCache, NumberStyles.Float, CultureInfo.InvariantCulture, out float fv))
                {
                    SetMemberValue(entry.member, comp, fv);
                }
            }
        }

        GUILayout.EndHorizontal();
    }

    bool Foldout(bool state, string title)
    {
        return GUILayout.Toggle(state, (state ? "▼ " : "► ") + title, "Button");
    }

    Texture2D MakeTex(Color c)
    {
        var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        t.SetPixel(0, 0, c);
        t.Apply();
        return t;
    }

    void DrawAnimatorParams(Animator anim)
    {
        try
        {
            var controller = anim.runtimeAnimatorController;
            if (controller == null) { GUILayout.Label("(No RuntimeAnimatorController)"); return; }

            var parameters = anim.parameters;
            foreach (var p in parameters)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(p.name + " (" + p.type + ")", GUILayout.Width(300));
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Bool:
                        {
                            bool bv = anim.GetBool(p.nameHash);
                            bool nb = GUILayout.Toggle(bv, bv ? "true" : "false", GUILayout.Width(100));
                            if (nb != bv) anim.SetBool(p.nameHash, nb);
                        }
                        break;
                    case AnimatorControllerParameterType.Float:
                        {
                            float fv = anim.GetFloat(p.nameHash);
                            string ftxt = GUILayout.TextField(fv.ToString("0.###", CultureInfo.InvariantCulture), GUILayout.MinWidth(120));
                            if (float.TryParse(ftxt, NumberStyles.Float, CultureInfo.InvariantCulture, out float nf))
                                anim.SetFloat(p.nameHash, nf);
                        }
                        break;
                    case AnimatorControllerParameterType.Int:
                        {
                            int iv = anim.GetInteger(p.nameHash);
                            string itxt = GUILayout.TextField(iv.ToString(CultureInfo.InvariantCulture), GUILayout.MinWidth(120));
                            if (int.TryParse(itxt, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ni))
                                anim.SetInteger(p.nameHash, ni);
                        }
                        break;
                    case AnimatorControllerParameterType.Trigger:
                        {
                            if (GUILayout.Button("Trigger", GUILayout.Width(120)))
                                anim.SetTrigger(p.nameHash);
                        }
                        break;
                }
                GUILayout.EndHorizontal();
            }
        }
        catch
        {
            GUILayout.Label("(Animator parameters could not be read)");
        }
    }

    IEnumerator LoadAudioClipFromPath(MemberInfo member, Component comp, string path, Action<string> setStatus, Action<AudioClip> onLoaded)
    {
        setStatus?.Invoke("Loading...");
        string url;
        try { url = new Uri(path).AbsoluteUri; }
        catch { url = "file:///" + path.Replace("\\", "/"); }
        var type = GuessAudioType(path);
        using (var req = UnityWebRequestMultimedia.GetAudioClip(url, type))
        {
#if UNITY_2020_2_OR_NEWER
            yield return req.SendWebRequest();
            bool ok = req.result == UnityWebRequest.Result.Success;
#else
            yield return req.SendWebRequest();
            bool ok = !(req.isHttpError || req.isNetworkError);
#endif
            if (!ok)
            {
                setStatus?.Invoke("Failed");
                yield break;
            }
            var clip = DownloadHandlerAudioClip.GetContent(req);
            if (clip != null) onLoaded?.Invoke(clip);
            setStatus?.Invoke(clip != null ? "Loaded" : "Failed");
        }
    }

    AudioType GuessAudioType(string path)
    {
        string ext = Path.GetExtension(path)?.ToLowerInvariant();
        if (ext == ".wav") return AudioType.WAV;
        if (ext == ".ogg") return AudioType.OGGVORBIS;
        if (ext == ".mp3") return AudioType.MPEG;
        return AudioType.UNKNOWN;
    }

    void SaveCurrentProfile()
    {
        var pf = new ProfileFile();
        for (int ti = 0; ti < targets.Count; ti++)
        {
            var e = targets[ti];
            if (e == null || e.targetObject == null) continue;
            if (e.comps == null || e.compNames == null) BuildComponentList(e);

            for (int ci = 0; ci < (e.comps?.Length ?? 0); ci++)
            {
                var comp = e.comps[ci];
                if (!comp) continue;
                if (!e.membersPerComp.ContainsKey(comp) || e.membersPerComp[comp] == null) BuildMemberList(e, comp);

                var td = new PerTarget
                {
                    targetIndex = ti,
                    compIndex = ci,
                    compTypeName = comp.GetType().AssemblyQualifiedName
                };

                var list = e.membersPerComp[comp];
                for (int m = 0; m < list.Count; m++)
                {
                    var me = list[m];
                    string name = me.member is FieldInfo fi ? fi.Name : ((PropertyInfo)me.member).Name;
                    var kv = new MemberKV { memberName = name, isProperty = me.member is PropertyInfo };

                    if (me.isColor)
                    {
                        var c = (Color)GetMemberValue(me.member, comp);
                        kv.kind = "color"; kv.rf = c.r; kv.gf = c.g; kv.bf = c.b; kv.af = c.a;
                    }
                    else if (me.isColor32)
                    {
                        var c32 = (Color32)GetMemberValue(me.member, comp);
                        kv.kind = "color32"; kv.r8 = c32.r; kv.g8 = c32.g; kv.b8 = c32.b; kv.a8 = c32.a;
                    }
                    else if (me.isV2)
                    {
                        var v = (Vector2)GetMemberValue(me.member, comp);
                        kv.kind = "v2"; kv.v2x = v.x; kv.v2y = v.y;
                    }
                    else if (me.isV3)
                    {
                        var v = (Vector3)GetMemberValue(me.member, comp);
                        kv.kind = "v3"; kv.v3x = v.x; kv.v3y = v.y; kv.v3z = v.z;
                    }
                    else if (me.isV4)
                    {
                        var v = (Vector4)GetMemberValue(me.member, comp);
                        kv.kind = "v4"; kv.v4x = v.x; kv.v4y = v.y; kv.v4z = v.z; kv.v4w = v.w;
                    }
                    else if (me.isQuat)
                    {
                        var q = (Quaternion)GetMemberValue(me.member, comp);
                        kv.kind = "quat"; kv.qx = q.x; kv.qy = q.y; kv.qz = q.z; kv.qw = q.w;
                    }
                    else if (me.isRect)
                    {
                        var r = (Rect)GetMemberValue(me.member, comp);
                        kv.kind = "rect"; kv.rx = r.x; kv.ry = r.y; kv.rw = r.width; kv.rh = r.height;
                    }
                    else if (me.isEnum)
                    {
                        kv.kind = "enum";
                        kv.enumType = me.type.AssemblyQualifiedName;
                        var cur = GetMemberValue(me.member, comp);
                        kv.enumName = cur != null ? cur.ToString() : me.enumNames[Mathf.Clamp(me.enumIndex, 0, me.enumNames.Length - 1)];
                    }
                    else if (me.isAudioClip)
                    {
                        kv.kind = "audioclip";
                        kv.audioPath = me.audioPathCache ?? "";
                    }
                    else if (me.type == typeof(string))
                    {
                        kv.kind = "string";
                        kv.s = (string)GetMemberValue(me.member, comp) ?? "";
                    }
                    else if (me.type == typeof(float))
                    {
                        kv.kind = "float";
                        kv.f = (float)GetMemberValue(me.member, comp);
                    }
                    else if (me.type == typeof(int))
                    {
                        kv.kind = "int";
                        kv.i = (int)GetMemberValue(me.member, comp);
                    }
                    else if (me.type == typeof(bool))
                    {
                        kv.kind = "bool";
                        kv.b = (bool)GetMemberValue(me.member, comp);
                    }

                    td.members.Add(kv);
                }

                pf.targets.Add(td);
            }
        }

        var dir = GetBaseDir();
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, SanitizeFileName(profileName) + ".json");
        var json = JsonUtility.ToJson(pf, true);
        File.WriteAllText(path, json);
        ShowStatus("Saved: " + path);
    }

    void LoadProfile(string name, bool showResult)
    {
        var dir = GetBaseDir();
        var path = Path.Combine(dir, SanitizeFileName(name) + ".json");
        if (!File.Exists(path))
        {
            if (showResult) ShowStatus("Profile not found: " + path);
            return;
        }

        var json = File.ReadAllText(path);
        var pf = JsonUtility.FromJson<ProfileFile>(json);
        if (pf == null || pf.targets == null)
        {
            if (showResult) ShowStatus("Invalid profile: " + path);
            return;
        }

        for (int k = 0; k < pf.targets.Count; k++)
        {
            var td = pf.targets[k];
            if (td.targetIndex < 0 || td.targetIndex >= targets.Count) continue;
            var e = targets[td.targetIndex];
            if (e == null || e.targetObject == null) continue;

            if (e.comps == null || e.compNames == null) BuildComponentList(e);
            int cidx = td.compIndex;

            if (cidx < 0 || cidx >= (e.comps?.Length ?? 0))
            {
                cidx = -1;
                for (int i = 0; i < (e.comps?.Length ?? 0); i++)
                {
                    var ct = e.comps[i] ? e.comps[i].GetType().AssemblyQualifiedName : "";
                    if (ct == td.compTypeName) { cidx = i; break; }
                }
            }
            if (cidx < 0 || cidx >= (e.comps?.Length ?? 0)) continue;

            var comp = e.comps[cidx];
            if (!e.membersPerComp.ContainsKey(comp) || e.membersPerComp[comp] == null) BuildMemberList(e, comp);
            var memberEntries = e.membersPerComp[comp];

            for (int m = 0; m < td.members.Count; m++)
            {
                var kv = td.members[m];
                MemberEntry match = null;
                if (memberEntries != null)
                {
                    for (int r = 0; r < memberEntries.Count; r++)
                    {
                        var mr = memberEntries[r];
                        string n = mr.member is FieldInfo fi ? fi.Name : ((PropertyInfo)mr.member).Name;
                        if (n == kv.memberName) { match = mr; break; }
                    }
                }
                if (match == null) continue;

                if (kv.kind == "color" && match.isColor)
                {
                    var col = new Color(kv.rf, kv.gf, kv.bf, kv.af);
                    SetMemberValue(match.member, comp, col);
                    match.cr = kv.rf.ToString("0.###", CultureInfo.InvariantCulture);
                    match.cg = kv.gf.ToString("0.###", CultureInfo.InvariantCulture);
                    match.cb = kv.bf.ToString("0.###", CultureInfo.InvariantCulture);
                    match.ca = kv.af.ToString("0.###", CultureInfo.InvariantCulture);
                }
                else if (kv.kind == "color32" && match.isColor32)
                {
                    var c32 = new Color32((byte)kv.r8, (byte)kv.g8, (byte)kv.b8, (byte)kv.a8);
                    SetMemberValue(match.member, comp, c32);
                    match.cr = kv.r8.ToString(CultureInfo.InvariantCulture);
                    match.cg = kv.g8.ToString(CultureInfo.InvariantCulture);
                    match.cb = kv.b8.ToString(CultureInfo.InvariantCulture);
                    match.ca = kv.a8.ToString(CultureInfo.InvariantCulture);
                }
                else if (kv.kind == "v2" && match.isV2)
                {
                    var v = new Vector2(kv.v2x, kv.v2y);
                    SetMemberValue(match.member, comp, v);
                    match.v2x = kv.v2x.ToString("0.###", CultureInfo.InvariantCulture);
                    match.v2y = kv.v2y.ToString("0.###", CultureInfo.InvariantCulture);
                }
                else if (kv.kind == "v3" && match.isV3)
                {
                    var v = new Vector3(kv.v3x, kv.v3y, kv.v3z);
                    SetMemberValue(match.member, comp, v);
                    match.v3x = kv.v3x.ToString("0.###", CultureInfo.InvariantCulture);
                    match.v3y = kv.v3y.ToString("0.###", CultureInfo.InvariantCulture);
                    match.v3z = kv.v3z.ToString("0.###", CultureInfo.InvariantCulture);
                }
                else if (kv.kind == "v4" && match.isV4)
                {
                    var v = new Vector4(kv.v4x, kv.v4y, kv.v4z, kv.v4w);
                    SetMemberValue(match.member, comp, v);
                    match.v4x = kv.v4x.ToString("0.###", CultureInfo.InvariantCulture);
                    match.v4y = kv.v4y.ToString("0.###", CultureInfo.InvariantCulture);
                    match.v4z = kv.v4z.ToString("0.###", CultureInfo.InvariantCulture);
                    match.v4w = kv.v4w.ToString("0.###", CultureInfo.InvariantCulture);
                }
                else if (kv.kind == "quat" && match.isQuat)
                {
                    var q = new Quaternion(kv.qx, kv.qy, kv.qz, kv.qw);
                    SetMemberValue(match.member, comp, q);
                    match.qx = kv.qx.ToString("0.###", CultureInfo.InvariantCulture);
                    match.qy = kv.qy.ToString("0.###", CultureInfo.InvariantCulture);
                    match.qz = kv.qz.ToString("0.###", CultureInfo.InvariantCulture);
                    match.qw = kv.qw.ToString("0.###", CultureInfo.InvariantCulture);
                }
                else if (kv.kind == "rect" && match.isRect)
                {
                    var rct = new Rect(kv.rx, kv.ry, kv.rw, kv.rh);
                    SetMemberValue(match.member, comp, rct);
                    match.rx = kv.rx.ToString("0.###", CultureInfo.InvariantCulture);
                    match.ry = kv.ry.ToString("0.###", CultureInfo.InvariantCulture);
                    match.rw = kv.rw.ToString("0.###", CultureInfo.InvariantCulture);
                    match.rh = kv.rh.ToString("0.###", CultureInfo.InvariantCulture);
                }
                else if (kv.kind == "enum" && match.isEnum)
                {
                    try
                    {
                        var et = Type.GetType(kv.enumType, false);
                        if (et != null)
                        {
                            var val = Enum.Parse(et, kv.enumName);
                            SetMemberValue(match.member, comp, val);
                            match.enumIndex = Array.IndexOf(match.enumValues, val);
                            if (match.enumIndex < 0) match.enumIndex = 0;
                        }
                    }
                    catch { }
                }
                else if (kv.kind == "audioclip" && match.isAudioClip)
                {
                    match.audioPathCache = kv.audioPath ?? "";
                    if (!string.IsNullOrWhiteSpace(match.audioPathCache))
                    {
                        StartCoroutine(LoadAudioClipFromPath(match.member, comp, match.audioPathCache, s => { match.audioStatus = s; }, clipOk =>
                        {
                            if (clipOk != null) SetMemberValue(match.member, comp, clipOk);
                        }));
                    }
                }
                else if (kv.kind == "string" && match.type == typeof(string))
                {
                    SetMemberValue(match.member, comp, kv.s ?? "");
                    match.editCache = kv.s ?? "";
                }
                else if (kv.kind == "float" && match.type == typeof(float))
                {
                    SetMemberValue(match.member, comp, kv.f);
                    match.editCache = kv.f.ToString("0.###", CultureInfo.InvariantCulture);
                }
                else if (kv.kind == "int" && match.type == typeof(int))
                {
                    SetMemberValue(match.member, comp, kv.i);
                    match.editCache = kv.i.ToString(CultureInfo.InvariantCulture);
                }
                else if (kv.kind == "bool" && match.type == typeof(bool))
                {
                    SetMemberValue(match.member, comp, kv.b);
                    match.editCache = kv.b ? "true" : "false";
                }
            }
        }
        if (showResult) ShowStatus("Loaded: " + path);
    }

    string SanitizeFileName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(s)) s = "Profile";
        return s;
    }

    string GetBaseDir()
    {
        return MateEnginePaths.MEValueChangerDir;
    }

    string GetSettingsPath()
    {
        return Path.Combine(GetBaseDir(), "settings.json");
    }

    void LoadSettings()
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            var data = JsonUtility.FromJson<SettingsData>(json);
            if (data == null) return;
            if (!string.IsNullOrEmpty(data.profileName)) profileName = data.profileName;
            autoLoadOnStart = data.autoLoadOnStart;
        }
        catch { }
    }

    void SaveSettings()
    {
        try
        {
            var dir = GetBaseDir();
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var path = GetSettingsPath();
            var data = new SettingsData { profileName = profileName, autoLoadOnStart = autoLoadOnStart };
            var json = JsonUtility.ToJson(data, true);
            File.WriteAllText(path, json);
            ShowStatus("Settings saved");
        }
        catch { }
    }

    void ShowStatus(string msg)
    {
        status = msg;
        statusUntil = Time.realtimeSinceStartup + 3f;
    }

    void ApplyDeactivateWhileOpen(bool visible)
    {
        if (visible)
        {
            prevActive.Clear();
            for (int i = 0; i < deactivateWhileOpen.Count; i++)
            {
                var go = deactivateWhileOpen[i];
                if (go == null) continue;
                prevActive[go] = go.activeSelf;
                go.SetActive(false);
            }
        }
        else
        {
            foreach (var kv in prevActive)
            {
                if (kv.Key != null) kv.Key.SetActive(kv.Value);
            }
            prevActive.Clear();
        }
    }
}
