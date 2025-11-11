using UnityEngine;
using System;
using System.Runtime.InteropServices;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.UI;


public class AvatarBigScreenTimer : MonoBehaviour
{
    [Header("Bubble Material")]
    public Material bubbleMaterial;

    [Header("Enable BigScreen Alarm Feature")]
    public bool enableBigScreenAlarm = false;

    [Header("Allowed Animator States")]
    public bool useAllowedStatesWhitelist = false;
    public string[] allowedStates = { "Idle" };

    [Header("Click disables BigScreen completely")]
    public bool clickDisablesBoth = false;

    [Header("Audio")]
    public AudioSource audioSource;
    public List<AudioClip> alarmClips = new List<AudioClip>();

    [Header("Alarm Chat Bubble")]
    [TextArea(1, 3)]
    public string alarmText = "Wake up! This is your alarm!";
    public Transform chatContainer;
    public Sprite bubbleSprite;
    public Color bubbleColor = new Color32(255, 72, 38, 255);
    public Color fontColor = Color.white;
    public Font font;
    public int fontSize = 16;
    public int bubbleWidth = 600;
    public float textPadding = 10f;
    public float bubbleSpacing = 10f;

    [Header("Fake Stream Settings")]
    [Tooltip("Stream speed: characters per second")]
    [Range(5, 100)]
    public int streamSpeed = 35;

    [Header("Stream Audio")]
    public AudioSource streamAudioSource;

    private float alarmInputBlockUntil = 0f;
    [Header("Alarm Cooldown")]
    public float alarmInputBlockDuration = 5f; 


    [Header("Live Status (Inspector)")]
    [SerializeField] private string inspectorEvent;
    [SerializeField] private string inspectorTargetTime;
    [SerializeField] private string inspectorCurrentTime;

    private AvatarBigScreenHandler bigScreenHandler;
    private Animator avatarAnimator;
    private bool alarmActive = false;

    private LLMUnitySamples.Bubble alarmBubble;
    private Coroutine streamCoroutine;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    private readonly Queue<string> pendingEvents = new Queue<string>();

    void Start()
    {
        bigScreenHandler = GetComponent<AvatarBigScreenHandler>();
        avatarAnimator = GetComponent<Animator>();
        alarmActive = false;
        RemoveAlarmBubble();
    }

    void CheckMultiAlarms()
    {
        var d = SaveLoadHandler.Instance?.data;
        if (d == null) return;
        if (!d.alarmsEnabled) return;
        if (d.alarms == null || d.alarms.Count == 0) return;

        var now = DateTime.Now;
        int h = now.Hour;
        int m = now.Minute;
        int dow = now.DayOfWeek == DayOfWeek.Sunday ? 6 : ((int)now.DayOfWeek - 1);
        long unixMin = new DateTimeOffset(now.Year, now.Month, now.Day, h, m, 0, TimeSpan.Zero).ToUnixTimeSeconds() / 60;

        for (int i = 0; i < d.alarms.Count; i++)
        {
            var a = d.alarms[i];
            if (a == null) continue;
            if (!a.enabled) continue;
            if (a.hour != h || a.minute != m) continue;
            if (a.daysMask != 0 && (a.daysMask & (1 << dow)) == 0) continue;
            if (a.lastTriggeredUnixMinute == unixMin) continue;

            a.lastTriggeredUnixMinute = unixMin;
            SaveLoadHandler.Instance.SaveToDisk();

            a.lastTriggeredUnixMinute = unixMin;
            SaveLoadHandler.Instance.SaveToDisk();
            EnqueueOrTrigger(string.IsNullOrEmpty(a.text) ? "Alarm" : a.text);
            inspectorEvent = "Alarm queued";

        }
    }

    void Update()
    {
        enableBigScreenAlarm = SaveLoadHandler.Instance.data.alarmsEnabled;
        if (!enableBigScreenAlarm)
        {
            inspectorEvent = "Alarms disabled";
            StopAlarm();
            return;
        }

        inspectorCurrentTime = DateTime.Now.ToString("HH:mm:ss");

        DateTime nextTime;
        var next = GetNextAlarm(out nextTime);
        inspectorTargetTime = next != null ? nextTime.ToString("yyyy-MM-dd HH:mm") : "-";
        if (!alarmActive && next != null) alarmText = string.IsNullOrEmpty(next.text) ? "Alarm" : next.text;

        if (useAllowedStatesWhitelist && !IsInAllowedState())
        {
            inspectorEvent = "Alarm blocked by state";
            StopAlarm();
            RemoveAlarmBubble();
            return;
        }

        CheckMultiAlarms();
        CheckTimers();

        bool isBigScreen = avatarAnimator != null && avatarAnimator.GetBool("isBigScreen");
        bool isBigScreenAlarm = avatarAnimator != null && avatarAnimator.GetBool("isBigScreenAlarm");

        if (!isBigScreenAlarm) RemoveAlarmBubble();

        if (isBigScreen && isBigScreenAlarm && alarmActive)
        {
            if (Time.time < alarmInputBlockUntil)
            {
                inspectorEvent = "Cooldown";
                return;
            }
            inspectorEvent = "Waiting for input";
            if (IsGlobalUserInput())
            {
                inspectorEvent = "Alarm stopped by input";
                avatarAnimator.SetBool("isBigScreenAlarm", false);
                alarmActive = false;
                if (audioSource != null && audioSource.isPlaying) audioSource.Stop();
                if (clickDisablesBoth)
                {
                    avatarAnimator.SetBool("isBigScreen", false);
                    if (bigScreenHandler != null) bigScreenHandler.SendMessage("DeactivateBigScreen");
                }

                if (pendingEvents.Count > 0)
                {
                    var nextText = pendingEvents.Dequeue();
                    alarmText = nextText;
                    TriggerAlarmNow();
                    return;
                }

                RemoveAlarmBubble();
            }
        }
    }


    void PlayRandomAlarm()
    {
        if (audioSource != null && alarmClips != null && alarmClips.Count > 0)
        {
            AudioClip clip = alarmClips[UnityEngine.Random.Range(0, alarmClips.Count)];
            audioSource.clip = clip;
            audioSource.loop = true;
            audioSource.Play();
        }
    }

    void StopAlarm()
    {
        if (avatarAnimator != null)
            avatarAnimator.SetBool("isBigScreenAlarm", false);
        alarmActive = false;
        if (audioSource != null && audioSource.isPlaying)
            audioSource.Stop();
        RemoveAlarmBubble();
        pendingEvents.Clear();
    }

    bool IsInAllowedState()
    {
        if (avatarAnimator == null || allowedStates == null || allowedStates.Length == 0)
            return true;
        var current = avatarAnimator.GetCurrentAnimatorStateInfo(0);
        foreach (var s in allowedStates)
            if (!string.IsNullOrEmpty(s) && current.IsName(s)) return true;
        return false;
    }

    private bool lastGlobalMouseDown = false;
    private bool IsGlobalUserInput()
    {
        bool mouseDown = (GetAsyncKeyState(0x01) & 0x8000) != 0;
        bool mouseClick = mouseDown && !lastGlobalMouseDown;
        lastGlobalMouseDown = mouseDown;

        bool keyPressed = false;
        for (int key = 0x08; key <= 0xFE; key++)
        {
            if ((GetAsyncKeyState(key) & 0x8000) != 0)
            {
                keyPressed = true;
                break;
            }
        }
        return mouseClick || keyPressed;
    }

    public void TriggerAlarmNow()
    {
        if (avatarAnimator != null)
        {
            avatarAnimator.SetBool("isBigScreen", true);
            avatarAnimator.SetBool("isBigScreenAlarm", true);
            avatarAnimator.SetBool("isBigScreenSaver", false);
            avatarAnimator.SetBool("isWindowSit", false);
            avatarAnimator.SetBool("isSitting", false);
        }
        if (bigScreenHandler != null)
            bigScreenHandler.SendMessage("ActivateBigScreen");
        PlayRandomAlarm();
        alarmActive = true;
        alarmInputBlockUntil = Time.time + alarmInputBlockDuration;
        inspectorEvent = "Alarm triggered manually";
        StartCoroutine(ShowAlarmBubbleStreamedDelayed());
    }


    void ShowAlarmBubbleStreamed()
    {
        if (chatContainer == null) return;
        RemoveAlarmBubble();



        var ui = new LLMUnitySamples.BubbleUI
        {
            sprite = bubbleSprite,
            font = font,
            fontSize = fontSize,
            fontColor = fontColor,
            bubbleColor = bubbleColor,
            bottomPosition = 0,
            leftPosition = 1,
            textPadding = textPadding,
            bubbleOffset = bubbleSpacing,
            bubbleWidth = bubbleWidth,
            bubbleHeight = -1
        };

        alarmBubble = new LLMUnitySamples.Bubble(chatContainer, ui, "AlarmBubble", "");
        var img = alarmBubble.GetRectTransform().GetComponentInChildren<Image>(true);
        if (img != null && bubbleMaterial != null) img.material = bubbleMaterial;


        if (streamAudioSource != null)
        {
            streamAudioSource.Stop();
            streamAudioSource.Play();
        }

        if (streamCoroutine != null) StopCoroutine(streamCoroutine);
        streamCoroutine = StartCoroutine(FakeStreamAlarmText(alarmText));
    }

    IEnumerator FakeStreamAlarmText(string fullText)
    {
        if (alarmBubble == null) yield break;
        alarmBubble.SetText("");
        int length = 0;
        float delay = 1f / Mathf.Max(streamSpeed, 1);

        while (length < fullText.Length)
        {
            length++;
            alarmBubble.SetText(fullText.Substring(0, length));
            yield return new WaitForSeconds(delay);
            if (alarmBubble == null) yield break;
        }
        alarmBubble.SetText(fullText);
        if (streamAudioSource != null && streamAudioSource.isPlaying)
            streamAudioSource.Stop();
        streamCoroutine = null;
    }

    void RemoveAlarmBubble()
    {
        if (streamCoroutine != null)
        {
            StopCoroutine(streamCoroutine);
            streamCoroutine = null;
        }
        if (alarmBubble != null)
        {
            alarmBubble.Destroy();
            alarmBubble = null;
        }
        if (streamAudioSource != null && streamAudioSource.isPlaying)
            streamAudioSource.Stop();

    }

    IEnumerator ShowAlarmBubbleStreamedDelayed()
    {
        yield return new WaitForSeconds(3f); 
        ShowAlarmBubbleStreamed();
    }

    SaveLoadHandler.SettingsData.AlarmEntry GetNextAlarm(out DateTime nextTime)
    {
        nextTime = DateTime.MinValue;
        var d = SaveLoadHandler.Instance?.data;
        if (d == null || !d.alarmsEnabled || d.alarms == null || d.alarms.Count == 0) return null;

        var now = DateTime.Now;
        SaveLoadHandler.SettingsData.AlarmEntry best = null;
        DateTime bestTime = DateTime.MaxValue;

        for (int i = 0; i < d.alarms.Count; i++)
        {
            var a = d.alarms[i];
            if (a == null || !a.enabled) continue;
            var cand = ComputeNextTime(a, now);
            if (cand < bestTime) { bestTime = cand; best = a; }
        }

        if (best != null) nextTime = bestTime;
        return best;
    }

    void EnqueueOrTrigger(string text)
    {
        if (alarmActive) { pendingEvents.Enqueue(string.IsNullOrEmpty(text) ? "Alarm" : text); return; }
        alarmText = string.IsNullOrEmpty(text) ? "Alarm" : text;
        TriggerAlarmNow();
    }

    void CheckTimers()
    {
        var d = SaveLoadHandler.Instance?.data;
        if (d == null) return;
        if (!d.alarmsEnabled) return;
        if (d.timers == null || d.timers.Count == 0) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (int i = 0; i < d.timers.Count; i++)
        {
            var t = d.timers[i];
            if (t == null) continue;
            if (!t.enabled) continue;
            if (!t.running) continue;
            if (t.targetUnix <= 0) continue;
            if (now < t.targetUnix) continue;
            t.running = false;
            t.targetUnix = 0;
            SaveLoadHandler.Instance.SaveToDisk();
            var txt = string.IsNullOrEmpty(t.text) ? "Timer" : t.text;
            EnqueueOrTrigger(txt);
        }
    }


    DateTime ComputeNextTime(SaveLoadHandler.SettingsData.AlarmEntry a, DateTime now)
    {
        if (a == null) return DateTime.MaxValue;

        if (a.daysMask == 0)
        {
            var cand = new DateTime(now.Year, now.Month, now.Day, Mathf.Clamp(a.hour, 0, 23), Mathf.Clamp(a.minute, 0, 59), 0);
            if (cand <= now) cand = cand.AddDays(1);
            return cand;
        }

        int today = now.DayOfWeek == DayOfWeek.Sunday ? 6 : ((int)now.DayOfWeek - 1);
        for (int add = 0; add < 7; add++)
        {
            int idx = (today + add) % 7;
            bool dayOn = (a.daysMask & (1 << idx)) != 0;
            if (!dayOn) continue;

            var cand = new DateTime(now.Year, now.Month, now.Day, Mathf.Clamp(a.hour, 0, 23), Mathf.Clamp(a.minute, 0, 59), 0).AddDays(add);
            if (add == 0 && cand <= now) continue;
            return cand;
        }
        return now.AddDays(7);
    }

}

#if UNITY_EDITOR
[UnityEditor.CustomEditor(typeof(AvatarBigScreenTimer))]
public class AvatarBigScreenTimerEditor : UnityEditor.Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        AvatarBigScreenTimer script = (AvatarBigScreenTimer)target;
        if (GUILayout.Button("Trigger Alarm Now (Debug)"))
        {
            script.TriggerAlarmNow();
        }
    }
}
#endif