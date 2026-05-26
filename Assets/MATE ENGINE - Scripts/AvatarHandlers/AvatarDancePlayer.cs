using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CustomDancePlayer
{
    public class AvatarDanceHandler : MonoBehaviour
    {
        [Header("UI")]
        public Button playButton;
        public Button stopButton;
        public Button prevButton;
        public Button nextButton;
        public Slider progressSlider;
        public TMP_Text playingNowText;
        public TMP_Text playTimeText;
        public TMP_Text maxPlayTimeText;
        public TMP_Text authorText;

        [Header("UI Fallback")]
        public bool useFallbackFont = false;
        public Text playingNowFallbackText;
        public Text authorFallbackText;

        [Header("Sound")]
        public AudioSource audioSource;
        public Slider volumeSlider;

        [Header("Animator")]
        public string danceLayerName = "Dance Layer";
        public string danceStateName = "Custom Dance";
        public string placeholderClipName = "CUSTOM_DANCE";
        public string customDancingParam = "isCustomDancing";
        public string waitingParam = "isWaitingForDancing";

        [Header("List UI")]
        public Transform contentObject;
        public GameObject prefab;
        public Button songPlayButton;

        [Header("Sources")]
        public string streamingSubfolder = "CustomDances";
        public string modsFolderName = "Mods";

        [Header("Sync")]
        public bool enableSync = true;
        public string syncFileName = "avatar_dance_play_bus.json";
        public float pollInterval = 0.05f;
        public double leadSeconds = 1.5;

        class BusCmd
        {
            public int v;
            public string cmd;
            public string sid;
            public string title;
            public int index;
            public double atUtc;
            public double writeUtc;
        }

        AnimationClip placeholderClipCached;
        Coroutine playRoutine;
        bool autoNextScheduled;

        Animator animator;
        Animator lastAnimator;
        RuntimeAnimatorController defaultController;
        AnimatorOverrideController overrideController;
        int layerIndex = -1;
        int stateHash = 0;
        int currentIndex = -1;

        string defaultPlayingNowText = "";
        string defaultPlayTimeText = "";
        string defaultMaxPlayTimeText = "";
        string defaultAuthorText = "";
        const string unknownAuthorLabel = "Author: Unknown";

        float currentTotalSeconds = 0f;
        float playStartTime = 0f;
        bool isPlaying = false;
        List<int> filteredQueue = null;
        bool holdDuringTransition;
        bool pendingStop;


        readonly HashSet<string> mmdBlendShapeNames = new HashSet<string>(new[]{
            "まばたき","ウィンク","ウィンク２","ウィンク右","笑い","なごみ","びっくり","ジト目","瞳小","キリッ","星目","はぁと","はちゅ目","はっ","ハイライト消し","怒るいい子！",
            "あ","い","う","え","お","えーん","ん","▲","口","ω口","はんっ！","にっこり","にやり","にやり２","べろっ","てへぺろ","口角上げ","口角下げ","口横広げ","真面目","上下","困る","怒り","照れ","涙","すぼめ"
        }, StringComparer.Ordinal);

        class DanceEntry
        {
            public string id;
            public string path;
            public string bundlePath;
            public AnimationClip clip;
            public AudioClip audio;
            public AssetBundle bundle;
            public bool fromME;
            public string extractedDir;
            public string author;
            public string stableId;
        }

        [Serializable]
        class DanceMeta
        {
            public string songName;
            public string songAuthor;
            public string mmdAuthor;
            public float songLength;
            public string placeholderClipName;
        }

        readonly List<DanceEntry> entries = new();
        readonly Dictionary<string, DanceEntry> byId = new(StringComparer.OrdinalIgnoreCase);
        DanceEntry loadedEntry = null;

        class PooledItem
        {
            public GameObject go;
            public TMP_Text titleTMP;
            public TMP_Text authorTMP;
            public Text titleFB;
            public Text authorFB;
            public Button button;
        }

        readonly List<PooledItem> uiPool = new();

        string busPath;
        int lastSeenV = -1;
        Mutex leaderMutex;
        bool isLeader;
        Coroutine scheduledCo;
        bool guardActive;
        double guardUntilUtc;
        bool animatorFrozen;
        float animatorPrevSpeed = 1f;
        readonly List<Button> tempDisabled = new();
        float storedSliderValue = -1f;
        float storedAudioVolume = -1f;
        bool followerMuted;

        void Awake()
        {
            if (!useFallbackFont)
            {
                if (playingNowText != null) defaultPlayingNowText = playingNowText.text;
                if (authorText != null) defaultAuthorText = string.IsNullOrWhiteSpace(authorText.text) ? unknownAuthorLabel : authorText.text;
            }
            else
            {
                if (playingNowFallbackText != null) defaultPlayingNowText = playingNowFallbackText.text;
                if (authorFallbackText != null) defaultAuthorText = string.IsNullOrWhiteSpace(authorFallbackText.text) ? unknownAuthorLabel : authorText.text;
            }
            if (playTimeText != null) defaultPlayTimeText = playTimeText.text;
            if (maxPlayTimeText != null) defaultMaxPlayTimeText = maxPlayTimeText.text;
            BindUI();

            busPath = Path.Combine(MateEnginePaths.SyncDir, syncFileName);
            TryAcquireLeader();
        }

        IEnumerator Start()
        {
            if (audioSource == null) EnsureAudioSource();
            yield return null;
            FindAvatarSmart();
            LoadAllSources();
            BuildListUI();
            if (entries.Count > 0 && currentIndex < 0) currentIndex = 0;
            UpdatePlayingNowLabel(null);
            UpdateAuthorLabel(null);
            UpdateTimeLabels(0f, 0f);
        }

        void OnEnable()
        {
            if (enableSync)
            {
                StartCoroutine(Poll());
                StartCoroutine(LeaderAutoNextWatcher());
            }
        }

        bool IsOnDanceState()
        {
            if (animator == null) return false;
            if (layerIndex < 0) layerIndex = animator.GetLayerIndex(danceLayerName);
            var st = animator.GetCurrentAnimatorStateInfo(layerIndex);
            return st.shortNameHash == stateHash;
        }

        bool IsFullyInWaiting()
        {
            if (animator == null) return false;
            if (layerIndex < 0) layerIndex = animator.GetLayerIndex(danceLayerName);
            return !IsOnDanceState() && !animator.IsInTransition(layerIndex) &&
                   HasBool(waitingParam) && animator.GetBool(waitingParam);
        }

        void PauseAudio() { if (audioSource != null) try { audioSource.Pause(); } catch { } }
        void ResumeAudio() { if (audioSource != null && audioSource.clip != null) try { audioSource.Play(); } catch { } }

        void OnDisable()
        {
            if (scheduledCo != null) { StopCoroutine(scheduledCo); scheduledCo = null; }
            StopAllCoroutines();
            ReleaseLeader();
            UnfreezeAnimator();
            guardActive = false;
            ReenableAll();
        }

        void Update()
        {
            RefreshAnimatorIfChanged();

            bool dancingOn = animator != null && HasBool(customDancingParam) && animator.GetBool(customDancingParam);
            if (isPlaying && !dancingOn && !holdDuringTransition) StopAndUnload();


            float total = currentTotalSeconds;
            float elapsed = 0f;

            if (isPlaying)
            {
                if (audioSource != null && audioSource.clip != null)
                {
                    total = audioSource.clip.length;
                    elapsed = audioSource.time;
                    currentTotalSeconds = total;
                }
                else elapsed = Mathf.Clamp(Time.time - playStartTime, 0f, total);
            }

            if (progressSlider != null && total > 0f) progressSlider.value = Mathf.Clamp01(elapsed / total);
            else if (progressSlider != null) progressSlider.value = 0f;

            UpdateTimeLabels(elapsed, total);

            if (!(enableSync && isLeader))
            {
                if (isPlaying && total > 0f)
                {
                    bool audioEnded = audioSource != null && audioSource.clip != null && !audioSource.loop && audioSource.time >= audioSource.clip.length - 0.05f;
                    bool timeReached = elapsed >= total - 0.05f;
                    if (audioEnded || timeReached) TryAutoNext();
                }
            }

            if (guardActive)
            {
                if (UtcNow() < guardUntilUtc) EnforceHold();
                else guardActive = false;
            }
        }

        void BindUI()
        {
            if (playButton != null) playButton.onClick.AddListener(OnPlayClicked);
            if (stopButton != null) stopButton.onClick.AddListener(OnStopClicked);
            if (prevButton != null) prevButton.onClick.AddListener(OnPrevClicked);
            if (nextButton != null) nextButton.onClick.AddListener(OnNextClicked);
            if (songPlayButton != null) songPlayButton.onClick.AddListener(OnPlayClicked);
        }

        void OnPlayClicked()
        {
            SetWaiting(true);
            SetDancing(false);
            if (enableSync && isLeader)
            {
                int idx = ResolvePlayableIndex();
                var e = idx >= 0 && idx < entries.Count ? entries[idx] : null;
                double at = UtcNow() + leadSeconds;
                guardActive = true;
                guardUntilUtc = at;
                EnforceHold();
                ScheduleLocal(() => TryPlayByStableIdOrFallback(e != null ? e.stableId : null, idx, e != null ? e.id : null), at);
                Broadcast("PlayByStableId", e != null ? e.stableId : null, idx, e != null ? e.id : null, at);
                return;
            }
            TryPlayCurrentOrFirst();
        }

        void OnListItemClicked(int idx)
        {
            SetWaiting(true);
            SetDancing(false);
            if (enableSync && isLeader)
            {
                var e = idx >= 0 && idx < entries.Count ? entries[idx] : null;
                double at = UtcNow() + leadSeconds;
                guardActive = true;
                guardUntilUtc = at;
                EnforceHold();
                ScheduleLocal(() => TryPlayByStableIdOrFallback(e != null ? e.stableId : null, idx, e != null ? e.id : null), at);
                Broadcast("PlayByStableId", e != null ? e.stableId : null, idx, e != null ? e.id : null, at);
                return;
            }
            currentIndex = idx;
            PlayIndex(idx);
        }

        void OnNextClicked()
        {
            SetWaiting(true);
            SetDancing(false);
            int target = NextIndexForManual(true);
            if (enableSync && isLeader)
            {
                var e = target >= 0 && target < entries.Count ? entries[target] : null;
                double at = UtcNow() + leadSeconds;
                guardActive = true;
                guardUntilUtc = at;
                EnforceHold();
                ScheduleLocal(() => TryPlayByStableIdOrFallback(e != null ? e.stableId : null, target, e != null ? e.id : null), at);
                Broadcast("PlayByStableId", e != null ? e.stableId : null, target, e != null ? e.id : null, at);
                return;
            }
            if (target >= 0) PlayIndex(target);
        }
        void OnPrevClicked()
        {
            SetWaiting(true);
            SetDancing(false);
            int target = NextIndexForManual(false);
            if (enableSync && isLeader)
            {
                var e = target >= 0 && target < entries.Count ? entries[target] : null;
                double at = UtcNow() + leadSeconds;
                guardActive = true;
                guardUntilUtc = at;
                EnforceHold();
                ScheduleLocal(() => TryPlayByStableIdOrFallback(e != null ? e.stableId : null, target, e != null ? e.id : null), at);
                Broadcast("PlayByStableId", e != null ? e.stableId : null, target, e != null ? e.id : null, at);
                return;
            }
            if (target >= 0) PlayIndex(target);
        }

        void OnStopClicked()
        {
            SetDancing(false);
            SetWaiting(false);
            if (enableSync && isLeader)
            {
                double at = UtcNow();
                ScheduleLocal(() => { TryStopPlay(); }, at);
                Broadcast("StopPlay", null, -1, null, at);
                return;
            }
            StopPlay();
        }

        int ResolvePlayableIndex()
        {
            if (filteredQueue != null && filteredQueue.Count > 0)
            {
                if (currentIndex >= 0 && filteredQueue.Contains(currentIndex)) return currentIndex;
                return filteredQueue[0];
            }
            return currentIndex < 0 ? 0 : currentIndex;
        }

        void EnsureAudioSource()
        {
            var soundFX = GameObject.Find("SoundFX");
            if (soundFX == null) soundFX = new GameObject("SoundFX");
            var t = soundFX.transform.Find("CustomDanceAudio");
            GameObject go = t ? t.gameObject : new GameObject("CustomDanceAudio");
            if (!t) go.transform.SetParent(soundFX.transform, false);
            audioSource = go.GetComponent<AudioSource>();
            if (audioSource == null) audioSource = go.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.loop = false;
            audioSource.spatialBlend = 0f;
            audioSource.volume = 0.25f;
        }

        void FindAvatarSmart()
        {
            Animator found = null;
            var loader = FindFirstObjectByType<VRMLoader>();
            if (loader != null)
            {
                var current = loader.GetCurrentModel();
                if (current != null) found = current.GetComponentsInChildren<Animator>(true).FirstOrDefault(a => a && a.gameObject.activeInHierarchy);
            }
            if (found == null)
            {
                var modelParent = GameObject.Find("Model");
                if (modelParent != null) found = modelParent.GetComponentsInChildren<Animator>(true).FirstOrDefault(a => a && a.gameObject.activeInHierarchy);
            }
            if (found == null)
            {
                var all = GameObject.FindObjectsByType<Animator>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                found = all.FirstOrDefault(a => a && a.isActiveAndEnabled);
            }
            if (found != animator)
            {
                animator = found;
                lastAnimator = animator;
                defaultController = animator != null ? animator.runtimeAnimatorController : null;
                layerIndex = animator != null ? animator.GetLayerIndex(danceLayerName) : -1;
                stateHash = Animator.StringToHash(danceStateName);
                overrideController = null;
            }
        }

        void RefreshAnimatorIfChanged()
        {
            if (animator == null || lastAnimator == null || animator != lastAnimator || animator.runtimeAnimatorController != defaultController)
            {
                FindAvatarSmart();
            }
        }

        void LoadAllSources()
        {
            UnloadEntry(loadedEntry);
            loadedEntry = null;

            foreach (var e in entries) { try { e.bundle?.Unload(true); } catch { } e.bundle = null; e.clip = null; e.audio = null; }
            entries.Clear();
            byId.Clear();

            var files = new List<string>();

            string streamDir = Path.Combine(Application.streamingAssetsPath, streamingSubfolder);
            if (Directory.Exists(streamDir)) files.AddRange(Directory.GetFiles(streamDir, "*", SearchOption.AllDirectories));

            string modsDir = string.Equals(modsFolderName, "Mods", StringComparison.OrdinalIgnoreCase)
                ? MateEnginePaths.ModsDir
                : MateEnginePaths.SafePersistentSubdirectory(modsFolderName);
            files.AddRange(Directory.GetFiles(modsDir, "*", SearchOption.AllDirectories));

            for (int i = 0; i < files.Count; i++)
            {
                string f = files[i];
                string ext = Path.GetExtension(f);
                if (string.IsNullOrEmpty(ext)) continue;

                if (ext.Equals(".unity3d", StringComparison.OrdinalIgnoreCase))
                    TryAddUnity3D(f);
                else if (ext.Equals(".me", StringComparison.OrdinalIgnoreCase))
                    TryAddME(f);
            }

            entries.Sort((a, b) => string.Compare(a.id, b.id, StringComparison.OrdinalIgnoreCase));
        }

        void TryAddUnity3D(string path)
        {
            string id = Path.GetFileNameWithoutExtension(path);
            if (!IsModEnabled(id)) return;
            if (byId.ContainsKey(id)) return;

            var e = new DanceEntry
            {
                id = id,
                path = path,
                bundlePath = path,
                clip = null,
                audio = null,
                bundle = null,
                fromME = false,
                extractedDir = null,
                author = unknownAuthorLabel
            };
            entries.Add(e);
            byId[id] = e;
        }

        void TryAddME(string mePath)
        {
            string id = Path.GetFileNameWithoutExtension(mePath);
            if (!IsModEnabled(id)) return;
            if (byId.ContainsKey(id)) return;

            string cacheRoot = MateEnginePaths.ModCacheDir;
            string dst = Path.Combine(cacheRoot, id);

            bool needExtract = true;
            try
            {
                if (Directory.Exists(dst))
                {
                    DateTime srcTime = File.GetLastWriteTimeUtc(mePath);
                    DateTime dstTime = Directory.GetLastWriteTimeUtc(dst);
                    if (dstTime >= srcTime) needExtract = false;
                    else Directory.Delete(dst, true);
                }
                if (needExtract)
                {
                    ZipFile.ExtractToDirectory(mePath, dst);
                    Directory.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(mePath));
                }
            }
            catch { return; }

            string metaA = Path.Combine(dst, "dance_meta.json");
            string metaB = Path.Combine(dst, "dance.json");
            bool isDance = File.Exists(metaA) || File.Exists(metaB);
            if (!isDance) return;

            string bundlePath = Directory.GetFiles(dst, "*.bundle", SearchOption.AllDirectories).FirstOrDefault();
            if (string.IsNullOrEmpty(bundlePath) || !File.Exists(bundlePath)) return;

            string author = unknownAuthorLabel;
            string metaPath = File.Exists(metaA) ? metaA : metaB;
            if (File.Exists(metaPath))
            {
                try
                {
                    var json = File.ReadAllText(metaPath);
                    var meta = JsonUtility.FromJson<DanceMeta>(json);
                    string cand = null;
                    if (!string.IsNullOrWhiteSpace(meta.songAuthor)) cand = meta.songAuthor;
                    else if (!string.IsNullOrWhiteSpace(meta.mmdAuthor)) cand = meta.mmdAuthor;
                    if (!string.IsNullOrWhiteSpace(cand)) author = "Author: " + cand;
                }
                catch { }
            }

            var e = new DanceEntry
            {
                id = id,
                path = mePath,
                bundlePath = bundlePath,
                clip = null,
                audio = null,
                bundle = null,
                fromME = true,
                extractedDir = dst,
                author = author,
                stableId = "sha1:" + ComputeFileSha1(bundlePath)
            };
            entries.Add(e);
            byId[id] = e;
        }

        string ComputeFileSha1(string path)
        {
            try
            {
                using (var s = File.OpenRead(path))
                using (var sha1 = SHA1.Create())
                {
                    var h = sha1.ComputeHash(s);
                    return BitConverter.ToString(h).Replace("-", "").ToLowerInvariant();
                }
            }
            catch { return null; }
        }

        public string GetCurrentStableId() => loadedEntry != null ? loadedEntry.stableId : null;

        public bool PlayByStableId(string stableId)
        {
            if (string.IsNullOrEmpty(stableId)) return false;
            int idx = entries.FindIndex(e => string.Equals(e.stableId, stableId, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return false;
            return PlayIndex(idx);
        }

        void BuildListUI()
        {
            if (contentObject == null || prefab == null) return;

            if (uiPool.Count == 0)
            {
                for (int i = contentObject.childCount - 1; i >= 0; i--)
                    Destroy(contentObject.GetChild(i).gameObject);
            }

            while (uiPool.Count < entries.Count)
            {
                var go = Instantiate(prefab, contentObject);
                var item = new PooledItem
                {
                    go = go,
                    titleTMP = FindChildByName<TMP_Text>(go.transform, "Title"),
                    authorTMP = FindChildByName<TMP_Text>(go.transform, "Author"),
                    titleFB = FindChildByName<Text>(go.transform, "TitleFallback"),
                    authorFB = FindChildByName<Text>(go.transform, "AuthorFallback"),
                    button = FindChildByName<Button>(go.transform, "Button")
                };
                if (item.button == null)
                {
                    var allButtons = go.GetComponentsInChildren<Button>(true);
                    if (allButtons != null && allButtons.Length > 0) item.button = allButtons[0];
                }
                go.SetActive(false);
                uiPool.Add(item);
            }

            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var item = uiPool[i];

                if (useFallbackFont && (item.titleFB != null || item.authorFB != null))
                {
                    if (item.titleFB != null) { item.titleFB.text = e.id; item.titleFB.gameObject.SetActive(true); }
                    if (item.authorFB != null) { item.authorFB.text = string.IsNullOrWhiteSpace(e.author) ? unknownAuthorLabel : e.author; item.authorFB.gameObject.SetActive(true); }
                    if (item.titleTMP != null) item.titleTMP.gameObject.SetActive(false);
                    if (item.authorTMP != null) item.authorTMP.gameObject.SetActive(false);
                }
                else
                {
                    if (item.titleTMP != null) { item.titleTMP.text = e.id; item.titleTMP.gameObject.SetActive(true); }
                    if (item.authorTMP != null) { item.authorTMP.text = string.IsNullOrWhiteSpace(e.author) ? unknownAuthorLabel : e.author; item.authorTMP.gameObject.SetActive(true); }
                    if (item.titleFB != null) item.titleFB.gameObject.SetActive(false);
                    if (item.authorFB != null) item.authorFB.gameObject.SetActive(false);
                }

                if (item.button != null)
                {
                    item.button.onClick.RemoveAllListeners();
                    int idx = i;
                    item.button.onClick.AddListener(() => OnListItemClicked(idx));
                }

                item.go.SetActive(true);
            }

            for (int i = entries.Count; i < uiPool.Count; i++)
                uiPool[i].go.SetActive(false);
        }

        T FindChildByName<T>(Transform root, string name) where T : Component
        {
            if (root == null || string.IsNullOrEmpty(name)) return null;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t.GetComponent<T>();
            return null;
        }

        void PlayPrev()
        {
            if (entries.Count == 0) return;
            if (filteredQueue == null || filteredQueue.Count == 0)
            {
                currentIndex = currentIndex <= 0 ? entries.Count - 1 : currentIndex - 1;
            }
            else
            {
                int pos = filteredQueue.IndexOf(currentIndex);
                currentIndex = pos < 0 ? filteredQueue[filteredQueue.Count - 1]
                                       : (pos == 0 ? filteredQueue[filteredQueue.Count - 1] : filteredQueue[pos - 1]);
            }
            PlayIndex(currentIndex);
        }

        void PlayNext()
        {
            if (entries.Count == 0) return;
            if (filteredQueue == null || filteredQueue.Count == 0)
            {
                currentIndex = (currentIndex + 1) % entries.Count;
            }
            else
            {
                int pos = filteredQueue.IndexOf(currentIndex);
                currentIndex = pos < 0 ? filteredQueue[0] : filteredQueue[(pos + 1) % filteredQueue.Count];
            }
            PlayIndex(currentIndex);
        }

        bool EnsureAnimatorReady()
        {
            RefreshAnimatorIfChanged();
            if (animator == null) return false;

            if (defaultController == null) defaultController = animator.runtimeAnimatorController;
            if (layerIndex < 0) layerIndex = animator.GetLayerIndex(danceLayerName);
            if (stateHash == 0) stateHash = Animator.StringToHash(danceStateName);

            if (overrideController == null && defaultController != null)
            {
                overrideController = new AnimatorOverrideController(defaultController);
                animator.runtimeAnimatorController = overrideController;
            }

            if (placeholderClipCached == null && defaultController != null)
            {
                placeholderClipCached = FindPlaceholderClip(defaultController, placeholderClipName);
                if (placeholderClipCached != null && overrideController != null)
                    overrideController[placeholderClipName] = placeholderClipCached;
            }

            return true;
        }

        AnimationClip FindPlaceholderClip(RuntimeAnimatorController ctrl, string name)
        {
            if (ctrl == null) return null;
            var aocProbe = new AnimatorOverrideController(ctrl);
            var clips = aocProbe.animationClips;
            return clips.FirstOrDefault(c => c != null && c.name == name);
        }

        public bool PlayIndex(int index)
        {
            if (entries.Count == 0 || index < 0 || index >= entries.Count) return false;
            if (!EnsureAnimatorReady()) return false;
            if (playRoutine != null) StopCoroutine(playRoutine);
            pendingStop = false;
            playRoutine = StartCoroutine(SmoothPlayFlow(index));
            return true;
        }

        IEnumerator SmoothPlayFlow(int index)
        {
            holdDuringTransition = true;
            FreezeAnimator();
            PauseAudio();
            SetDancing(false);
            SetWaiting(true);

            float timeout = 2f;
            float t0 = Time.unscaledTime;
            while (!IsFullyInWaiting() && Time.unscaledTime - t0 < timeout)
                yield return null;

            var prev = loadedEntry;

            var e = entries[index];
            if (e.bundle == null)
            {
                string bp = string.IsNullOrEmpty(e.bundlePath) ? e.path : e.bundlePath;
                e.bundle = AssetBundle.LoadFromFile(bp);
                if (e.bundle == null) { UnfreezeAnimator(); holdDuringTransition = false; yield break; }
            }
            if (e.clip == null) e.clip = e.bundle.LoadAllAssets<AnimationClip>().FirstOrDefault();
            if (e.audio == null) e.audio = e.bundle.LoadAllAssets<AudioClip>().FirstOrDefault();

            if (!EnsureAnimatorReady()) { UnfreezeAnimator(); holdDuringTransition = false; yield break; }

            if (placeholderClipCached == null) placeholderClipCached = FindPlaceholderClip(defaultController, placeholderClipName);
            if (overrideController == null || placeholderClipCached == null) { UnfreezeAnimator(); holdDuringTransition = false; yield break; }

            overrideController[placeholderClipName] = e.clip != null ? e.clip : placeholderClipCached;

            if (prev != null && prev != e)
            {
                UnloadEntry(prev);
                StartCoroutine(UnloadUnusedAssetsRoutine());
            }

            if (audioSource == null) EnsureAudioSource();
            if (audioSource != null)
            {
                audioSource.Stop();
                audioSource.clip = e.audio;
                audioSource.time = 0f;
                audioSource.loop = false;
            }

            currentTotalSeconds = e.audio != null ? e.audio.length : (e.clip != null ? e.clip.length : 0f);
            playStartTime = Time.time;
            isPlaying = true;

            currentIndex = index;
            loadedEntry = e;
            UpdatePlayingNowLabel(e.id);
            UpdateAuthorLabel(e.author);
            UpdateTimeLabels(0f, currentTotalSeconds);

            SetWaiting(false);
            SetDancing(true);
            UnfreezeAnimator();
            ResumeAudio();

            holdDuringTransition = false;
            playRoutine = null;
        }



        void StopAndUnload()
        {
            if (audioSource != null)
            {
                try { audioSource.Stop(); } catch { }
                try { if (audioSource.clip != null) audioSource.clip.UnloadAudioData(); } catch { }
                audioSource.clip = null;
            }

            if (animator != null)
            {
                if (overrideController != null && placeholderClipCached != null)
                    overrideController[placeholderClipName] = placeholderClipCached;

                SetDancing(false);
                SetWaiting(false);
            }

            isPlaying = false;
            UpdatePlayingNowLabel(null);
            UpdateAuthorLabel(null);
            UpdateTimeLabels(0f, 0f);
            StartCoroutine(UnloadUnusedAssetsRoutine());
        }

        public void StopPlay()
        {
            if (!EnsureAnimatorReady())
            {
                isPlaying = false;
                UpdatePlayingNowLabel(null);
                UpdateAuthorLabel(null);
                UpdateTimeLabels(0f, 0f);
                return;
            }
            if (playRoutine != null) StopCoroutine(playRoutine);
            pendingStop = true;
            playRoutine = StartCoroutine(SmoothStopFlow());
        }
        IEnumerator SmoothStopFlow()
        {
            holdDuringTransition = true;
            FreezeAnimator();
            PauseAudio();

            SetDancing(false);

            float timeout = 2f;
            float t0 = Time.unscaledTime;
            while (IsOnDanceState() && Time.unscaledTime - t0 < timeout)
                yield return null;

            if (overrideController != null && placeholderClipCached != null)
                overrideController[placeholderClipName] = placeholderClipCached;

            if (audioSource != null)
            {
                try { audioSource.Stop(); } catch { }
                try { if (audioSource.clip != null) audioSource.clip.UnloadAudioData(); } catch { }
                audioSource.clip = null;
            }
            var prev = loadedEntry;
            loadedEntry = null;
            if (prev != null)
            {
                UnloadEntry(prev);
                StartCoroutine(UnloadUnusedAssetsRoutine());
            }

            isPlaying = false;
            UpdatePlayingNowLabel(null);
            UpdateAuthorLabel(null);
            UpdateTimeLabels(0f, 0f);

            UnfreezeAnimator();
            holdDuringTransition = false;
            playRoutine = null;
        }

        void UnloadEntry(DanceEntry e)
        {
            if (e == null) return;
            try { if (e.bundle != null) e.bundle.Unload(true); } catch { }
            e.bundle = null;
            e.clip = null;
            e.audio = null;
        }

        void UpdatePlayingNowLabel(string nameOrNull)
        {
            if (useFallbackFont && playingNowFallbackText != null)
            {
                playingNowFallbackText.text = string.IsNullOrEmpty(nameOrNull) ? defaultPlayingNowText : nameOrNull;
                if (playingNowText != null) playingNowText.text = "";
                return;
            }
            if (playingNowText != null) playingNowText.text = string.IsNullOrEmpty(nameOrNull) ? defaultPlayingNowText : nameOrNull;
        }

        void UpdateAuthorLabel(string authorOrNull)
        {
            if (useFallbackFont && authorFallbackText != null)
            {
                if (string.IsNullOrWhiteSpace(authorOrNull))
                {
                    authorFallbackText.text = string.IsNullOrWhiteSpace(defaultAuthorText) ? unknownAuthorLabel : defaultAuthorText;
                }
                else authorFallbackText.text = authorOrNull;
                if (authorText != null) authorText.text = "";
                return;
            }

            if (authorText == null) return;
            if (string.IsNullOrWhiteSpace(authorOrNull))
            {
                authorText.text = string.IsNullOrWhiteSpace(defaultAuthorText) ? unknownAuthorLabel : defaultAuthorText;
            }
            else authorText.text = authorOrNull;
        }

        void UpdateTimeLabels(float elapsed, float total)
        {
            if (playTimeText != null) playTimeText.text = total <= 0f ? defaultPlayTimeText : FormatTime(elapsed);
            if (maxPlayTimeText != null) maxPlayTimeText.text = total <= 0f ? defaultMaxPlayTimeText : FormatTime(total);
        }

        string FormatTime(float seconds)
        {
            int s = Mathf.FloorToInt(seconds + 0.0001f);
            int m = s / 60;
            int r = s % 60;
            return m.ToString("00") + ":" + r.ToString("00");
        }

        void OnDestroy()
        {
            UnloadEntry(loadedEntry);
            foreach (var e in entries) { try { e.bundle?.Unload(true); } catch { } }
            entries.Clear();
            byId.Clear();
        }

        IEnumerator UnloadUnusedAssetsRoutine()
        {
            yield return Resources.UnloadUnusedAssets();
        }
        void TryAutoNext()
        {
            if (autoNextScheduled) return;
            autoNextScheduled = true;
            StartCoroutine(AutoNextCo());
        }

        IEnumerator AutoNextCo()
        {
            yield return null;
            int target = NextIndexForAuto();
            if (target >= 0) PlayIndex(target);
            autoNextScheduled = false;
        }
        public bool IsPlaying => isPlaying;
        public AnimationClip GetCurrentClip() => loadedEntry != null ? loadedEntry.clip : null;
        public float GetPlaybackTime()
        {
            if (audioSource != null && audioSource.clip != null) return audioSource.time;
            if (isPlaying) return Mathf.Clamp(Time.time - playStartTime, 0f, currentTotalSeconds);
            return 0f;
        }
        public float GetPlaybackLength() => currentTotalSeconds;

        public void SetQueueByIndices(List<int> indices)
        {
            if (indices == null || indices.Count == 0) { filteredQueue = null; return; }
            var seen = new HashSet<int>();
            var list = new List<int>(indices.Count);
            for (int i = 0; i < indices.Count; i++)
            {
                int idx = indices[i];
                if (idx < 0 || idx >= entries.Count) continue;
                if (seen.Add(idx)) list.Add(idx);
            }
            filteredQueue = list.Count > 0 ? list : null;
        }

        public int FindIndexByTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return -1;
            for (int i = 0; i < entries.Count; i++)
                if (string.Equals(entries[i].id, title, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        bool IsModEnabled(string id)
        {
            if (SaveLoadHandler.Instance == null || SaveLoadHandler.Instance.data == null) return true;
            if (SaveLoadHandler.Instance.data.modStates.TryGetValue(id, out var on)) return on;
            return true;
        }

        IEnumerator Poll()
        {
            var wait = new WaitForSecondsRealtime(pollInterval);
            while (true)
            {
                if (!isLeader && enableSync)
                {
                    var d = Read();
                    if (d != null && d.v > lastSeenV)
                    {
                        lastSeenV = d.v;
                        if (scheduledCo != null) { StopCoroutine(scheduledCo); scheduledCo = null; }

                        if (d.cmd == "PlayCurrentOrFirst")
                        {
                            guardActive = true;
                            guardUntilUtc = d.atUtc;
                            EnforceHold();
                            MuteFollower();
                            ScheduleRemote(() => TryPlayCurrentOrFirst(), d.atUtc);
                        }
                        else if (d.cmd == "PlayByStableId")
                        {
                            guardActive = true;
                            guardUntilUtc = d.atUtc;
                            EnforceHold();
                            MuteFollower();
                            ScheduleRemote(() => TryPlayByStableIdOrFallback(d.sid, d.index, d.title), d.atUtc);
                        }
                        else if (d.cmd == "PlayNext")
                        {
                            guardActive = true;
                            guardUntilUtc = d.atUtc;
                            EnforceHold();
                            MuteFollower();
                            ScheduleRemote(() => PlayNext(), d.atUtc);
                        }
                        else if (d.cmd == "PlayPrev")
                        {
                            guardActive = true;
                            guardUntilUtc = d.atUtc;
                            EnforceHold();
                            MuteFollower();
                            ScheduleRemote(() => PlayPrev(), d.atUtc);
                        }
                        else if (d.cmd == "StopPlay")
                        {
                            ScheduleRemote(() => { TryStopPlay(); UnmuteFollower(); }, d.atUtc);
                        }
                    }

                }
                yield return wait;
            }
        }
        IEnumerator LeaderAutoNextWatcher()
        {
            var wait = new WaitForSecondsRealtime(0.05f);
            while (true)
            {
                if (enableSync && isLeader)
                {
                    if (audioSource != null && audioSource.clip != null && audioSource.time > 0f)
                    {
                        float remain = audioSource.clip.length - audioSource.time;
                        if (remain <= 0.18f && !autoNextScheduled)
                        {
                            autoNextScheduled = true;
                            double at = UtcNow() + leadSeconds;
                            guardActive = true;
                            guardUntilUtc = at;
                            EnforceHold();
                            int target = NextIndexForAuto();
                            var e = target >= 0 && target < entries.Count ? entries[target] : null;
                            ScheduleLocal(() => TryPlayByStableIdOrFallback(e != null ? e.stableId : null, target, e != null ? e.id : null), at);
                            Broadcast("PlayByStableId", e != null ? e.stableId : null, target, e != null ? e.id : null, at);
                        }
                        else if (remain > 0.5f)
                        {
                            autoNextScheduled = false;
                        }
                    }
                    else
                    {
                        autoNextScheduled = false;
                    }
                }
                yield return wait;
            }
        }

        int NextFromFiltered(bool forward)
        {
            int cur = currentIndex;
            if (entries.Count == 0) return -1;

            if (filteredQueue == null || filteredQueue.Count == 0)
            {
                if (cur < 0) return 0;
                if (forward) return (cur + 1) % entries.Count;
                return cur <= 0 ? entries.Count - 1 : cur - 1;
            }
            int pos = filteredQueue.IndexOf(cur);
            if (pos < 0) return filteredQueue[0];
            if (forward) return filteredQueue[(pos + 1) % filteredQueue.Count];
            return pos == 0 ? filteredQueue[filteredQueue.Count - 1] : filteredQueue[pos - 1];
        }

        void ScheduleLocal(Action act, double atUtc)
        {
            double wait = Math.Max(0.0, atUtc - UtcNow());
            if (scheduledCo != null) StopCoroutine(scheduledCo);
            scheduledCo = StartCoroutine(Co(wait, act));
        }

        void ScheduleRemote(Action act, double atUtc)
        {
            double wait = Math.Max(0.0, atUtc - UtcNow());
            if (scheduledCo != null) StopCoroutine(scheduledCo);
            scheduledCo = StartCoroutine(Co(wait, act));
        }

        IEnumerator Co(double wait, Action act)
        {
            if (wait > 0) yield return new WaitForSecondsRealtime((float)wait);
            act();
            UnfreezeAnimator();
            guardActive = false;
            ReenableAll();
            scheduledCo = null;
        }

        void TryPlayCurrentOrFirst()
        {
            if (filteredQueue != null && filteredQueue.Count > 0)
            {
                int idx = (currentIndex >= 0 && filteredQueue.Contains(currentIndex)) ? currentIndex : filteredQueue[0];
                PlayIndex(idx);
                return;
            }
            int fallback = currentIndex < 0 ? 0 : currentIndex;
            PlayIndex(fallback);
        }

        void TryStopPlay()
        {
            StopPlay();
        }

        void TryPlayByStableIdOrFallback(string sid, int idx, string title)
        {
            if (!string.IsNullOrEmpty(sid))
            {
                if (PlayByStableId(sid)) return;
            }
            if (idx >= 0)
            {
                PlayIndex(idx);
                return;
            }
            if (!string.IsNullOrEmpty(title))
            {
                int k = FindIndexByTitle(title);
                if (k >= 0) { PlayIndex(k); return; }
            }
            TryPlayCurrentOrFirst();
        }

        void Broadcast(string cmd, string sid, int index, string title, double atUtc)
        {
            var d0 = Read();
            int v = d0 != null ? d0.v + 1 : 0;
            var d = new BusCmd { v = v, cmd = cmd, sid = sid, index = index, title = title, atUtc = atUtc, writeUtc = UtcNow() };
            SafeWrite(d);
        }

        BusCmd Read()
        {
            try
            {
                if (!File.Exists(busPath)) return null;
                var s = File.ReadAllText(busPath);
                if (string.IsNullOrWhiteSpace(s)) return null;
                return JsonUtility.FromJson<BusCmd>(s);
            }
            catch { return null; }
        }

        void SafeWrite(BusCmd d)
        {
            try
            {
                string tmp = busPath + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(d));
                if (File.Exists(busPath)) File.Delete(busPath);
                File.Move(tmp, busPath);
            }
            catch { }
        }

        void TryAcquireLeader()
        {
            ReleaseLeader();
            try
            {
                bool createdNew;
                leaderMutex = new Mutex(false, "MateEngine.AvatarDanceSync.Leader", out createdNew);
                isLeader = leaderMutex.WaitOne(0);
            }
            catch { isLeader = GetInstanceIndex() == 0; }
        }

        int GetInstanceIndex()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], "--instance", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out int v))
                    return Math.Max(0, v);
            return 0;
        }

        void ReleaseLeader()
        {
            if (leaderMutex != null)
            {
                try { if (isLeader) leaderMutex.ReleaseMutex(); } catch { }
                leaderMutex.Dispose();
                leaderMutex = null;
            }
            isLeader = false;
        }

        static double UtcNow()
        {
            var e = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (DateTime.UtcNow - e).TotalSeconds;
        }

        void EnforceHold()
        {
            holdDuringTransition = true;
            FreezeAnimator();
            PauseAudio();
            SetDancing(false);
            SetWaiting(true);
        }

        void FreezeAnimator()
        {
            if (animator == null) return;
            if (animatorFrozen) return;
            animatorPrevSpeed = animator.speed > 0f ? animator.speed : 1f;
            animator.speed = 0f;
            animatorFrozen = true;
        }

        void UnfreezeAnimator()
        {
            if (animator == null) return;
            animator.speed = animatorPrevSpeed > 0f ? animatorPrevSpeed : 1f;
            animatorFrozen = false;
        }

        void ReenableAll()
        {
            for (int i = 0; i < tempDisabled.Count; i++)
                if (tempDisabled[i] != null) tempDisabled[i].interactable = true;
            tempDisabled.Clear();
        }

        void MuteFollower()
        {
            if (isLeader) return;
            if (!followerMuted)
            {
                if (volumeSlider != null)
                {
                    storedSliderValue = volumeSlider.value;
                    volumeSlider.value = 0f;
                }
                if (audioSource != null)
                {
                    storedAudioVolume = audioSource.volume;
                    audioSource.volume = 0f;
                }
                followerMuted = true;
            }
        }

        public void RescanMods()
        {
            bool wasPlaying = isPlaying;
            string sid = GetCurrentStableId();
            float t = GetPlaybackTime();

            LoadAllSources();
            BuildListUI();

            if (wasPlaying && !string.IsNullOrEmpty(sid))
            {
                int i = entries.FindIndex(e => string.Equals(e.stableId, sid, StringComparison.OrdinalIgnoreCase));
                if (i >= 0)
                {
                    PlayIndex(i);
                    if (audioSource != null && audioSource.clip != null)
                        audioSource.time = Mathf.Clamp(t, 0f, audioSource.clip.length);
                }
            }
        }

        bool HasBool(string param)
        {
            if (animator == null) return false;
            var ps = animator.parameters;
            for (int i = 0; i < ps.Length; i++)
                if (ps[i].type == AnimatorControllerParameterType.Bool && ps[i].name == param)
                    return true;
            return false;
        }

        void SetWaiting(bool v)
        {
            if (HasBool(waitingParam)) animator.SetBool(waitingParam, v);
        }

        void SetDancing(bool v)
        {
            if (HasBool(customDancingParam)) animator.SetBool(customDancingParam, v);
        }

        void ResetMMDBlendShapes()
        {
            if (animator == null) return;

            var smrs = animator.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int s = 0; s < smrs.Length; s++)
            {
                var smr = smrs[s];
                var mesh = smr != null ? smr.sharedMesh : null;
                if (mesh == null) continue;

                int count = mesh.blendShapeCount;
                for (int i = 0; i < count; i++)
                {
                    string n = mesh.GetBlendShapeName(i);
                    if (string.IsNullOrEmpty(n)) continue;
                    if (mmdBlendShapeNames.Contains(n)) smr.SetBlendShapeWeight(i, 0f);
                }
            }
        }

        // RNG, Shuffle
        public bool loopOn = false;
        public bool shuffleOn = false;

        int PickRandomFromFilteredExcludingCurrent()
        {
            if (entries.Count == 0) return -1;
            List<int> pool = (filteredQueue != null && filteredQueue.Count > 0) ? filteredQueue : Enumerable.Range(0, entries.Count).ToList();
            if (pool.Count == 0) return -1;
            if (pool.Count == 1) return pool[0];
            int pick;
            do { pick = pool[UnityEngine.Random.Range(0, pool.Count)]; } while (pick == currentIndex);
            return pick;
        }

        int NextIndexSequential(bool forward)
        {
            if (entries.Count == 0) return -1;
            if (filteredQueue == null || filteredQueue.Count == 0)
            {
                if (currentIndex < 0) return 0;
                return forward ? (currentIndex + 1) % entries.Count : (currentIndex <= 0 ? entries.Count - 1 : currentIndex - 1);
            }
            int pos = filteredQueue.IndexOf(currentIndex);
            if (pos < 0) return filteredQueue[0];
            if (forward) return filteredQueue[(pos + 1) % filteredQueue.Count];
            return pos == 0 ? filteredQueue[filteredQueue.Count - 1] : filteredQueue[pos - 1];
        }

        int NextIndexForManual(bool forward)
        {
            if (shuffleOn) return PickRandomFromFilteredExcludingCurrent();
            return NextIndexSequential(forward);
        }

        int NextIndexForAuto()
        {
            if (loopOn && currentIndex >= 0) return currentIndex;
            if (shuffleOn) return PickRandomFromFilteredExcludingCurrent();
            return NextIndexSequential(true);
        }


        void UnmuteFollower()
        {
            if (isLeader) return;
            if (followerMuted)
            {
                if (volumeSlider != null && storedSliderValue >= 0f) volumeSlider.value = storedSliderValue;
                if (audioSource != null && storedAudioVolume >= 0f) audioSource.volume = storedAudioVolume;
                followerMuted = false;
            }
        }
    }
}
