using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using LLMUnitySamples;
using UnityEngine.EventSystems;

[DisallowMultipleComponent]
public class MEVoicePack : MonoBehaviour
{
    [Header("Drag Sounds")]
    public List<AudioClip> dragStartClip;
    public List<AudioClip> dragStopClip;

    [Header("Reaction Sounds")]
    public List<PetRegionOverride> petRegionOverrides = new();

    [Header("Macaroon Sounds")]
    public AudioClip bubbleEnableClip;
    public AudioClip bubbleDisableClip;

    [Header("Event Message Sounds")]
    public AudioClip randomStreamClip;

    [Header("Alarm Sounds")]
    public List<AudioClip> bigScreenAlarmClips = new();
    public AudioClip bigScreenStreamClip;

    [Header("Chatbot Sounds")]
    public AudioClip chatBotStreamClip;

    [Header("Minecraft Sounds")]
    public AudioClip minecraftStreamClip;

    [Header("Ui Sounds")]
    public List<AudioClip> menuStartupClips = new();
    public List<AudioClip> menuOpenClips = new();
    public List<AudioClip> menuCloseClips = new();
    public List<AudioClip> menuButtonClips = new();
    public List<AudioClip> menuToggleClips = new();
    public List<AudioClip> menuSliderClips = new();
    public List<AudioClip> menuDropdownClips = new();

    [Header("Options")]
    public bool applyOnEnable = true;
    public bool revertOnDisable = false;
    public bool autoWatchForNewHandlers = true;
    public bool autoBindAnimator = true;
    public bool fixEmptyStateWhitelist = true;
    [Range(0.1f, 5f)] public float watchInterval = 0.5f;

    private readonly List<(AudioSource src, AudioClip original)> _dragOriginals = new();
    private readonly List<PetRegionSnapshot> _petOriginals = new();
    private readonly List<(AvatarBubbleHandler h, AudioClip enable, AudioClip disable)> _bubbleOriginals = new();
    private readonly List<(AudioSource src, AudioClip clip)> _streamOriginals = new();
    private readonly List<(AvatarBigScreenTimer t, List<AudioClip> alarm)> _bigAlarmOriginals = new();
    private readonly List<(MenuAudioHandler h, List<AudioClip> startup, List<AudioClip> open, List<AudioClip> close, List<AudioClip> button, List<AudioClip> toggle, List<AudioClip> slider, List<AudioClip> dropdown)> _menuOriginals = new();

    private readonly HashSet<int> _processedDrag = new();
    private readonly HashSet<int> _processedPet = new();
    private readonly HashSet<int> _processedBubble = new();
    private readonly HashSet<int> _processedRandMsg = new();
    private readonly HashSet<int> _processedBigTimer = new();
    private readonly HashSet<int> _processedChatBot = new();
    private readonly HashSet<int> _processedMenu = new();
    private readonly HashSet<int> _processedMinecraft = new();

    private bool _applied;
    private Coroutine _watcher;

    public enum MappingMode { ReplaceAll, MatchExistingCount_Cycle }

    [System.Serializable]
    public class PetRegionOverride
    {
        public string regionName;
        public int fallbackRegionIndex = -1;
        public bool overrideVoiceClips = true;
        public MappingMode voiceMapping = MappingMode.MatchExistingCount_Cycle;
        public List<AudioClip> voiceClips = new();
        public bool overrideLayeredClips = true;
        public MappingMode layeredMapping = MappingMode.MatchExistingCount_Cycle;
        public List<AudioClip> layeredClips = new();
    }

    private class PetRegionSnapshot
    {
        public PetVoiceReactionHandler handler;
        public int regionIndex;
        public List<AudioClip> origVoice;
        public List<AudioClip> origLayered;
    }

    void OnEnable()
    {
        if (applyOnEnable) Apply();
        if (autoWatchForNewHandlers) _watcher = StartCoroutine(WatchForNewHandlers());
    }

    void OnDisable()
    {
        if (_watcher != null) { StopCoroutine(_watcher); _watcher = null; }
        _processedDrag.Clear();
        _processedPet.Clear();
        _processedBubble.Clear();
        _processedRandMsg.Clear();
        _processedBigTimer.Clear();
        _processedChatBot.Clear();
        _processedMenu.Clear();
        _processedMinecraft.Clear();
        if (revertOnDisable) Revert();
    }

    [ContextMenu("Apply Voice Pack")]
    public void Apply()
    {
#if UNITY_2023_1_OR_NEWER
        var dragHandlers = FindObjectsByType<AvatarDragSoundHandler>(FindObjectsSortMode.None);
        var petHandlers = FindObjectsByType<PetVoiceReactionHandler>(FindObjectsSortMode.None);
        var bubbleHandlers = FindObjectsByType<AvatarBubbleHandler>(FindObjectsSortMode.None);
        var randMsgHandlers = FindObjectsByType<AvatarRandomMessages>(FindObjectsSortMode.None);
        var bigTimers = FindObjectsByType<AvatarBigScreenTimer>(FindObjectsSortMode.None);
        var chatBots = FindObjectsByType<ChatBot>(FindObjectsSortMode.None);
        var menuHandlers = FindObjectsByType<MenuAudioHandler>(FindObjectsSortMode.None);
        var mcHandlers = FindObjectsByType<AvatarMinecraftMessages>(FindObjectsSortMode.None);
#else
        var dragHandlers = FindObjectsOfType<AvatarDragSoundHandler>(true);
        var petHandlers = FindObjectsOfType<PetVoiceReactionHandler>(true);
        var bubbleHandlers = FindObjectsOfType<AvatarBubbleHandler>(true);
        var randMsgHandlers = FindObjectsOfType<AvatarRandomMessages>(true);
        var bigTimers = FindObjectsOfType<AvatarBigScreenTimer>(true);
        var chatBots = FindObjectsOfType<ChatBot>(true);
        var menuHandlers = FindObjectsOfType<MenuAudioHandler>(true);
        var mcHandlers = FindObjectsOfType<AvatarMinecraftMessages>(true);
#endif
        ApplyDragOverridesTo(dragHandlers);
        ApplyPetOverridesTo(petHandlers);
        ApplyBubbleOverridesTo(bubbleHandlers);
        ApplyRandomMessagesOverridesTo(randMsgHandlers);
        ApplyBigScreenTimerOverridesTo(bigTimers);
        ApplyChatBotOverridesTo(chatBots);
        ApplyMenuAudioOverridesTo(menuHandlers);
        ApplyMinecraftOverridesTo(mcHandlers);
        _applied = true;
    }

    [ContextMenu("Revert Voice Pack")]
    public void Revert()
    {
        foreach (var (src, orig) in _dragOriginals) if (src) src.clip = orig;
        _dragOriginals.Clear();

        foreach (var snap in _petOriginals)
        {
            if (!snap.handler) continue;
            var regions = snap.handler.regions;
            if (regions == null || snap.regionIndex < 0 || snap.regionIndex >= regions.Count) continue;
            var r = regions[snap.regionIndex];
            r.voiceClips = snap.origVoice != null ? new List<AudioClip>(snap.origVoice) : new List<AudioClip>();
            r.layeredVoiceClips = snap.origLayered != null ? new List<AudioClip>(snap.origLayered) : new List<AudioClip>();
        }
        _petOriginals.Clear();

        foreach (var entry in _bubbleOriginals)
        {
            if (!entry.h) continue;
            entry.h.enableSound = entry.enable;
            entry.h.disableSound = entry.disable;
        }
        _bubbleOriginals.Clear();

        foreach (var (src, clip) in _streamOriginals) if (src) src.clip = clip;
        _streamOriginals.Clear();

        foreach (var (t, alarm) in _bigAlarmOriginals)
        {
            if (!t) continue;
            t.alarmClips = alarm != null ? new List<AudioClip>(alarm) : new List<AudioClip>();
        }
        _bigAlarmOriginals.Clear();

        foreach (var m in _menuOriginals)
        {
            if (!m.h) continue;
            m.h.startupSounds = m.startup != null ? new List<AudioClip>(m.startup) : new List<AudioClip>();
            m.h.openMenuSounds = m.open != null ? new List<AudioClip>(m.open) : new List<AudioClip>();
            m.h.closeMenuSounds = m.close != null ? new List<AudioClip>(m.close) : new List<AudioClip>();
            m.h.buttonSounds = m.button != null ? new List<AudioClip>(m.button) : new List<AudioClip>();
            m.h.toggleSounds = m.toggle != null ? new List<AudioClip>(m.toggle) : new List<AudioClip>();
            m.h.sliderSounds = m.slider != null ? new List<AudioClip>(m.slider) : new List<AudioClip>();
            m.h.dropdownSounds = m.dropdown != null ? new List<AudioClip>(m.dropdown) : new List<AudioClip>();
        }
        _menuOriginals.Clear();

        _applied = false;
    }

    private IEnumerator WatchForNewHandlers()
    {
        var wait = new WaitForSeconds(watchInterval);
        while (enabled)
        {
#if UNITY_2023_1_OR_NEWER
            var drags = FindObjectsByType<AvatarDragSoundHandler>(FindObjectsSortMode.None);
            var pets = FindObjectsByType<PetVoiceReactionHandler>(FindObjectsSortMode.None);
            var bubbles = FindObjectsByType<AvatarBubbleHandler>(FindObjectsSortMode.None);
            var randMsgs = FindObjectsByType<AvatarRandomMessages>(FindObjectsSortMode.None);
            var bigs = FindObjectsByType<AvatarBigScreenTimer>(FindObjectsSortMode.None);
            var bots = FindObjectsByType<ChatBot>(FindObjectsSortMode.None);
            var menus = FindObjectsByType<MenuAudioHandler>(FindObjectsSortMode.None);
            var mcs = FindObjectsByType<AvatarMinecraftMessages>(FindObjectsSortMode.None);
#else
            var drags = FindObjectsOfType<AvatarDragSoundHandler>(true);
            var pets = FindObjectsOfType<PetVoiceReactionHandler>(true);
            var bubbles = FindObjectsOfType<AvatarBubbleHandler>(true);
            var randMsgs = FindObjectsOfType<AvatarRandomMessages>(true);
            var bigs = FindObjectsOfType<AvatarBigScreenTimer>(true);
            var bots = FindObjectsOfType<ChatBot>(true);
            var menus = FindObjectsOfType<MenuAudioHandler>(true);
            var mcs = FindObjectsOfType<AvatarMinecraftMessages>(true);
#endif
            foreach (var d in drags)
            {
                int id = d.GetInstanceID();
                if (_processedDrag.Contains(id)) continue;
                ApplyDragOverridesTo(new[] { d });
                _processedDrag.Add(id);
            }

            foreach (var p in pets)
            {
                int id = p.GetInstanceID();
                if (_processedPet.Contains(id)) continue;
                if (autoBindAnimator && p.avatarAnimator == null)
                {
                    var anim = p.GetComponentInParent<Animator>();
                    if (anim) p.SetAnimator(anim);
                }
                if (fixEmptyStateWhitelist) EnsureStateWhitelistNotEmpty(p);
                ApplyPetOverridesTo(new[] { p });
                _processedPet.Add(id);
            }

            foreach (var b in bubbles)
            {
                int id = b.GetInstanceID();
                if (_processedBubble.Contains(id)) continue;
                ApplyBubbleOverridesTo(new[] { b });
                _processedBubble.Add(id);
            }

            foreach (var r in randMsgs)
            {
                int id = r.GetInstanceID();
                if (_processedRandMsg.Contains(id)) continue;
                ApplyRandomMessagesOverridesTo(new[] { r });
                _processedRandMsg.Add(id);
            }

            foreach (var t in bigs)
            {
                int id = t.GetInstanceID();
                if (_processedBigTimer.Contains(id)) continue;
                ApplyBigScreenTimerOverridesTo(new[] { t });
                _processedBigTimer.Add(id);
            }

            foreach (var cb in bots)
            {
                int id = cb.GetInstanceID();
                if (_processedChatBot.Contains(id)) continue;
                ApplyChatBotOverridesTo(new[] { cb });
                _processedChatBot.Add(id);
            }

            foreach (var m in menus)
            {
                int id = m.GetInstanceID();
                if (_processedMenu.Contains(id)) continue;
                ApplyMenuAudioOverridesTo(new[] { m });
                _processedMenu.Add(id);
            }
            foreach (var mc in mcs)
            {
                int id = mc.GetInstanceID();
                if (_processedMinecraft.Contains(id)) continue;
                ApplyMinecraftOverridesTo(new[] { mc });
                _processedMinecraft.Add(id);
            }
            yield return wait;
        }
    }

    private void ApplyDragOverridesTo(AvatarDragSoundHandler[] handlers)
    {
        if (handlers == null || handlers.Length == 0) return;
        if (!_applied) _dragOriginals.Clear();
        foreach (var h in handlers)
        {
            if (dragStartClip.Count > 0 && h.dragStartSound)
            {
                if (!_applied) _dragOriginals.Add((h.dragStartSound, h.dragStartSound.clip));
                // Clears sounds list in case of existing objects 
                h.dragStartSound.clip = dragStartClip[Random.Range(0, dragStartClip.Count)];
                h.dragStartSound.playOnAwake = false;
                h.dragStartSoundList.Clear();
                h.dragStartSoundList = new List<AudioClip>(dragStartClip);
            }
            if (dragStopClip.Count > 0 && h.dragStopSound)
            {
                if (!_applied) _dragOriginals.Add((h.dragStopSound, h.dragStopSound.clip));
                h.dragStopSound.clip = dragStopClip[Random.Range(0, dragStopClip.Count)];
                h.dragStopSound.playOnAwake = false;
                h.dragStopSoundList.Clear();
                h.dragStopSoundList = new List<AudioClip>(dragStopClip);
            }
        }
    }
    private void ApplyPetOverridesTo(PetVoiceReactionHandler[] petHandlers)
    {
        if (petHandlers == null || petHandlers.Length == 0) return;
        if (!_applied) _petOriginals.Clear();

        foreach (var ph in petHandlers)
        {
            if (autoBindAnimator && ph.avatarAnimator == null)
            {
                var anim = ph.GetComponentInParent<Animator>();
                if (anim) ph.SetAnimator(anim);
            }

            var regions = ph.regions;
            if (regions == null || regions.Count == 0) continue;

            foreach (var ov in petRegionOverrides)
            {
                int idx = ResolveRegionIndex(regions, ov);
                if (idx < 0 || idx >= regions.Count) continue;
                var r = regions[idx];

                if (!_applied)
                {
                    _petOriginals.Add(new PetRegionSnapshot
                    {
                        handler = ph,
                        regionIndex = idx,
                        origVoice = r.voiceClips != null ? new List<AudioClip>(r.voiceClips) : new List<AudioClip>(),
                        origLayered = r.layeredVoiceClips != null ? new List<AudioClip>(r.layeredVoiceClips) : new List<AudioClip>()
                    });
                }

                if (ov.overrideVoiceClips && ov.voiceClips != null && ov.voiceClips.Count > 0)
                {
                    r.voiceClips = BuildMappedList(ov.voiceClips, r.voiceClips != null ? r.voiceClips.Count : ov.voiceClips.Count, ov.voiceMapping);
                }
                if (ov.overrideLayeredClips && ov.layeredClips != null && ov.layeredClips.Count > 0)
                {
                    r.layeredVoiceClips = BuildMappedList(ov.layeredClips, r.layeredVoiceClips != null ? r.layeredVoiceClips.Count : ov.layeredClips.Count, ov.layeredMapping);
                }
            }
        }
    }

    private void ApplyBubbleOverridesTo(AvatarBubbleHandler[] handlers)
    {
        if (handlers == null || handlers.Length == 0) return;
        if (!_applied) _bubbleOriginals.Clear();
        foreach (var h in handlers)
        {
            if (!_applied) _bubbleOriginals.Add((h, h.enableSound, h.disableSound));
            if (bubbleEnableClip) h.enableSound = bubbleEnableClip;
            if (bubbleDisableClip) h.disableSound = bubbleDisableClip;
        }
    }

    private void ApplyRandomMessagesOverridesTo(AvatarRandomMessages[] handlers)
    {
        if (handlers == null || handlers.Length == 0) return;
        foreach (var h in handlers)
        {
            if (!h.streamAudioSource) continue;
            if (randomStreamClip)
            {
                if (!_applied) _streamOriginals.Add((h.streamAudioSource, h.streamAudioSource.clip));
                h.streamAudioSource.clip = randomStreamClip;
                h.streamAudioSource.playOnAwake = false;
            }
        }
    }

    private void ApplyBigScreenTimerOverridesTo(AvatarBigScreenTimer[] timers)
    {
        if (timers == null || timers.Length == 0) return;
        foreach (var t in timers)
        {
            if (!_applied) _bigAlarmOriginals.Add((t, t.alarmClips != null ? new List<AudioClip>(t.alarmClips) : new List<AudioClip>()));
            if (bigScreenAlarmClips != null && bigScreenAlarmClips.Count > 0) t.alarmClips = new List<AudioClip>(bigScreenAlarmClips);
            if (t.streamAudioSource && bigScreenStreamClip)
            {
                if (!_applied) _streamOriginals.Add((t.streamAudioSource, t.streamAudioSource.clip));
                t.streamAudioSource.clip = bigScreenStreamClip;
                t.streamAudioSource.playOnAwake = false;
            }
        }
    }

    private void ApplyChatBotOverridesTo(ChatBot[] bots)
    {
        if (bots == null || bots.Length == 0) return;
        foreach (var b in bots)
        {
            if (!b) continue;
            var src = b.streamAudioSource;
            if (!src) continue;
            if (chatBotStreamClip)
            {
                if (!_applied) _streamOriginals.Add((src, src.clip));
                src.clip = chatBotStreamClip;
                src.playOnAwake = false;
            }
        }
    }
    private void ApplyMinecraftOverridesTo(AvatarMinecraftMessages[] handlers)
    {
        if (handlers == null || handlers.Length == 0) return;
        foreach (var h in handlers)
        {
            if (!h) continue;
            var src = h.streamAudioSource;
            if (!src) continue;
            if (minecraftStreamClip)
            {
                if (!_applied) _streamOriginals.Add((src, src.clip));
                src.clip = minecraftStreamClip;
                src.playOnAwake = false;
            }
        }
    }

    private void ApplyMenuAudioOverridesTo(MenuAudioHandler[] menus)
    {
        if (menus == null || menus.Length == 0) return;
        if (!_applied) _menuOriginals.Clear();
        foreach (var m in menus)
        {
            if (!m) continue;
            if (!_applied)
            {
                _menuOriginals.Add((m,
                    m.startupSounds != null ? new List<AudioClip>(m.startupSounds) : new List<AudioClip>(),
                    m.openMenuSounds != null ? new List<AudioClip>(m.openMenuSounds) : new List<AudioClip>(),
                    m.closeMenuSounds != null ? new List<AudioClip>(m.closeMenuSounds) : new List<AudioClip>(),
                    m.buttonSounds != null ? new List<AudioClip>(m.buttonSounds) : new List<AudioClip>(),
                    m.toggleSounds != null ? new List<AudioClip>(m.toggleSounds) : new List<AudioClip>(),
                    m.sliderSounds != null ? new List<AudioClip>(m.sliderSounds) : new List<AudioClip>(),
                    m.dropdownSounds != null ? new List<AudioClip>(m.dropdownSounds) : new List<AudioClip>()));
            }
            if (menuStartupClips != null && menuStartupClips.Count > 0) m.startupSounds = new List<AudioClip>(menuStartupClips);
            if (menuOpenClips != null && menuOpenClips.Count > 0) m.openMenuSounds = new List<AudioClip>(menuOpenClips);
            if (menuCloseClips != null && menuCloseClips.Count > 0) m.closeMenuSounds = new List<AudioClip>(menuCloseClips);
            if (menuButtonClips != null && menuButtonClips.Count > 0) m.buttonSounds = new List<AudioClip>(menuButtonClips);
            if (menuToggleClips != null && menuToggleClips.Count > 0) m.toggleSounds = new List<AudioClip>(menuToggleClips);
            if (menuSliderClips != null && menuSliderClips.Count > 0) m.sliderSounds = new List<AudioClip>(menuSliderClips);
            if (menuDropdownClips != null && menuDropdownClips.Count > 0) m.dropdownSounds = new List<AudioClip>(menuDropdownClips);
        }
    }

    private static int ResolveRegionIndex(List<PetVoiceReactionHandler.VoiceRegion> regions, PetRegionOverride ov)
    {
        if (!string.IsNullOrEmpty(ov.regionName))
        {
            for (int i = 0; i < regions.Count; i++)
                if (string.Equals(regions[i].name, ov.regionName, System.StringComparison.OrdinalIgnoreCase)) return i;
        }
        return ov.fallbackRegionIndex;
    }

    private static List<AudioClip> BuildMappedList(List<AudioClip> source, int existingCount, MappingMode mode)
    {
        if (mode == MappingMode.ReplaceAll) return new List<AudioClip>(source);
        int count = Mathf.Max(existingCount, source.Count);
        var outList = new List<AudioClip>(count);
        for (int i = 0; i < count; i++) outList.Add(source[i % source.Count]);
        return outList;
    }

    private void EnsureStateWhitelistNotEmpty(PetVoiceReactionHandler p)
    {
        if (p == null) return;
        var listField = p.stateWhitelist;
        if (listField != null && listField.Count > 0) return;
        var anim = p.avatarAnimator ? p.avatarAnimator : p.GetComponentInParent<Animator>();
        if (!anim) return;
        var names = new HashSet<string>();
        var infos = anim.GetCurrentAnimatorClipInfo(0);
        for (int i = 0; i < infos.Length; i++) if (infos[i].clip) names.Add(infos[i].clip.name);
        names.Add("Idle");
        names.Add("Base Layer.Idle");
        names.Add("Locomotion");
        names.Add("Base Layer.Locomotion");
        p.stateWhitelist = new List<string>(names);
    }
}

//OLD

/*
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using System.Collections;
using System.Collections.Generic;

public class MenuAudioHandler : MonoBehaviour
{
    [Header("Audio Source GameObject (drag here)")]
    public AudioSource audioSource;

    [Range(0f, 10f)]
    public float disableDelay = 1f;

    [Header("Startup Sounds (plays once on app start)")]
    public List<AudioClip> startupSounds = new List<AudioClip>();
    public float startupPitchMin = 1f;
    public float startupPitchMax = 1f;
    [Range(0f, 1f)] public float startupVolume = 1f;

    [Header("Open Menu Sounds")]
    public List<AudioClip> openMenuSounds = new List<AudioClip>();
    public float openMenuPitchMin = 1f;
    public float openMenuPitchMax = 1f;
    [Range(0f, 1f)] public float openMenuVolume = 1f;

    [Header("Close Menu Sounds")]
    public List<AudioClip> closeMenuSounds = new List<AudioClip>();
    public float closeMenuPitchMin = 1f;
    public float closeMenuPitchMax = 1f;
    [Range(0f, 1f)] public float closeMenuVolume = 1f;

    [Header("Button Sounds")]
    public List<AudioClip> buttonSounds = new List<AudioClip>();
    public float buttonPitchMin = 1f;
    public float buttonPitchMax = 1f;
    [Range(0f, 1f)] public float buttonVolume = 1f;

    [Header("Toggle Sounds")]
    public List<AudioClip> toggleSounds = new List<AudioClip>();
    public float togglePitchMin = 1f;
    public float togglePitchMax = 1f;
    [Range(0f, 1f)] public float toggleVolume = 1f;

    [Header("Slider Sounds")]
    public List<AudioClip> sliderSounds = new List<AudioClip>();
    public float sliderPitchMin = 1f;
    public float sliderPitchMax = 1f;
    [Range(0f, 1f)] public float sliderVolume = 1f;

    [Header("Dropdown Sounds")]
    public List<AudioClip> dropdownSounds = new List<AudioClip>();
    public float dropdownPitchMin = 1f;
    public float dropdownPitchMax = 1f;
    [Range(0f, 1f)] public float dropdownVolume = 1f;

    private HashSet<Slider> activeSliders = new HashSet<Slider>();
    private bool wasMenuOpenLastFrame = false;
    private float disableTimer = 0f;
    private bool hasPlayedStartupSound = false;

    private void OnEnable()
    {
        SetupUIListeners();
        StartCoroutine(MenuMonitor());
    }

    private IEnumerator MenuMonitor()
    {
        while (true)
        {
            bool isOpen = AvatarClothesHandler.IsMenuOpen || TutorialMenu.IsActive;


            if (!hasPlayedStartupSound && SaveLoadHandler.Instance?.data != null)
            {
                // Only play once menuVolume is actually set
                float menuVol = SaveLoadHandler.Instance.data.menuVolume;
                if (menuVol > 0f)
                {
                    PlaySound(startupSounds, startupPitchMin, startupPitchMax, startupVolume);
                    hasPlayedStartupSound = true;
                }
            }


            if (isOpen)
            {
                if (audioSource != null && !audioSource.gameObject.activeSelf)
                    audioSource.gameObject.SetActive(true);

                if (!wasMenuOpenLastFrame)
                    PlaySound(openMenuSounds, openMenuPitchMin, openMenuPitchMax, openMenuVolume);

                disableTimer = 0f;
            }
            else
            {
                if (wasMenuOpenLastFrame)
                {
                    disableTimer = Time.time + disableDelay;
                    PlaySound(closeMenuSounds, closeMenuPitchMin, closeMenuPitchMax, closeMenuVolume);
                }

                if (disableTimer != 0f && Time.time >= disableTimer && audioSource != null && audioSource.gameObject.activeSelf)
                {
                    audioSource.gameObject.SetActive(false);
                    disableTimer = 0f;
                }
            }

            wasMenuOpenLastFrame = isOpen;
            yield return null;
        }
    }

    private void SetupUIListeners()
    {
        if (audioSource == null)
        {
            Debug.LogWarning("MenuAudioHandler: AudioSource not assigned.");
            return;
        }

        foreach (var button in GetComponentsInChildren<Button>(true))
        {
            if (button.GetComponent<ButtonLinker>() == null)
                button.onClick.AddListener(() => PlaySound(buttonSounds, buttonPitchMin, buttonPitchMax, buttonVolume));
        }


        foreach (var toggle in GetComponentsInChildren<Toggle>(true))
            toggle.onValueChanged.AddListener((_) => PlaySound(toggleSounds, togglePitchMin, togglePitchMax, toggleVolume));

        foreach (var slider in GetComponentsInChildren<Slider>(true))
            AddSliderEvents(slider);

        foreach (var dropdown in GetComponentsInChildren<Dropdown>(true))
            dropdown.onValueChanged.AddListener((_) => PlaySound(dropdownSounds, dropdownPitchMin, dropdownPitchMax, dropdownVolume));
    }

    private void AddSliderEvents(Slider slider)
    {
        EventTrigger trigger = slider.gameObject.GetComponent<EventTrigger>() ?? slider.gameObject.AddComponent<EventTrigger>();

        var pointerDown = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
        pointerDown.callback.AddListener((_) =>
        {
            if (!activeSliders.Contains(slider))
            {
                activeSliders.Add(slider);
                PlaySound(sliderSounds, sliderPitchMin, sliderPitchMax, sliderVolume);
            }
        });
        trigger.triggers.Add(pointerDown);

        var pointerUp = new EventTrigger.Entry { eventID = EventTriggerType.PointerUp };
        pointerUp.callback.AddListener((_) => activeSliders.Remove(slider));
        trigger.triggers.Add(pointerUp);
    }

    private void PlaySound(List<AudioClip> clips, float pitchMin, float pitchMax, float volume)
    {
        if (clips == null || clips.Count == 0 || audioSource == null || !audioSource.gameObject.activeSelf)
            return;

        float menuVolumeMultiplier = 1f;
        if (SaveLoadHandler.Instance != null)
        {
            menuVolumeMultiplier = SaveLoadHandler.Instance.data.menuVolume;
        }

        float finalVolume = volume * menuVolumeMultiplier;
        if (finalVolume <= 0f) return;

        audioSource.pitch = Random.Range(pitchMin, pitchMax);
        audioSource.PlayOneShot(clips[Random.Range(0, clips.Count)], finalVolume);
    }

    public void PlayOpenSound()
    {
        if (audioSource != null && !audioSource.gameObject.activeSelf)
            audioSource.gameObject.SetActive(true);

        PlaySound(openMenuSounds, openMenuPitchMin, openMenuPitchMax, openMenuVolume);
    }

    public void PlayCloseSound()
    {
        if (audioSource != null && !audioSource.gameObject.activeSelf)
            audioSource.gameObject.SetActive(true);

        PlaySound(closeMenuSounds, closeMenuPitchMin, closeMenuPitchMax, closeMenuVolume);
    }

    public void PlayButtonSound()
    {
        if (audioSource != null && !audioSource.gameObject.activeSelf)
            audioSource.gameObject.SetActive(true);

        PlaySound(buttonSounds, buttonPitchMin, buttonPitchMax, buttonVolume);
    }


}
*/
