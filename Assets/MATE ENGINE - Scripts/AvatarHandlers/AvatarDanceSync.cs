/*
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;

namespace CustomDancePlayer
{
    public class AvatarDanceSync : MonoBehaviour
    {
        [Serializable]
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

        class PreArmHook : MonoBehaviour, IPointerDownHandler
        {
            public AvatarDanceSync owner;
            public string intent;
            public string sid;
            public int index;
            public string title;
            public void OnPointerDown(PointerEventData e)
            {
                var btn = GetComponent<Button>();
                owner?.BeginLeaderAction(intent, sid, index, title, btn);
            }
        }

        public AvatarDanceHandler handler;
        public string fileName = "avatar_dance_play_bus.json";
        public float pollInterval = 0.05f;
        public double leadSeconds = 1.5;

        string path;
        int lastSeenV = -1;
        Coroutine scheduledCo;
        Mutex leaderMutex;
        bool isLeader;

        Button mainPlayBtn;
        Button stopBtn;
        Button nextBtn;
        Button prevBtn;
        Transform contentRoot;
        IList entriesList;
        FieldInfo entriesFi;
        FieldInfo entryStableIdFi;

        private bool animatorFrozen = false;

        readonly HashSet<Button> wiredButtons = new HashSet<Button>();
        readonly List<Button> tempDisabled = new List<Button>();

        bool guardActive;
        double guardUntilUtc;
        AudioSource audioSource;
        Animator animator;
        float animatorPrevSpeed = 1f;

        AvatarDancePlayerUtils utils;
        Slider volumeSlider;
        float storedSliderValue = -1f;
        float storedAudioVolume = -1f;
        bool followerMuted;

        FieldInfo currentIndexFi;
        FieldInfo entryIdFi;
        MethodInfo findIndexByTitleMi;
        bool autoNextArmed;

        void Awake()
        {
            if (handler == null) handler = GetComponent<AvatarDanceHandler>();
            path = Path.Combine(MateEnginePaths.SyncDir, fileName);
            TryAcquireLeader();
            ResolveRefs();
        }

        void OnEnable()
        {
            StartCoroutine(LeaderAutoNextWatcher());
            StartCoroutine(Poll());
            StartCoroutine(WireLoop());
        }

        IEnumerator LeaderAutoNextWatcher()
        {
            var wait = new WaitForSecondsRealtime(0.05f);
            while (true)
            {
                if (isLeader)
                {
                    ResolveRefs();
                    if (audioSource != null && audioSource.clip != null && audioSource.time > 0f)
                    {
                        float remain = audioSource.clip.length - audioSource.time;
                        if (remain <= 0.18f && !autoNextArmed)
                        {
                            autoNextArmed = true;
                            double at = UtcNow() + leadSeconds;
                            guardActive = true;
                            guardUntilUtc = at;
                            EnforceHold();
                            int target = NextFromFiltered(true);
                            GetEntryData(target, out var nsid, out var ntit);
                            ScheduleLocal(() => TryPlayByStableIdOrFallback(nsid, target, ntit), at);
                            Broadcast("PlayByStableId", nsid, target, ntit, at);
                        }
                        else if (remain > 0.5f)
                        {
                            autoNextArmed = false;
                        }
                    }
                    else
                    {
                        autoNextArmed = false;
                    }
                }
                yield return wait;
            }
        }


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
            if (!guardActive) return;
            if (UtcNow() < guardUntilUtc) EnforceHold();
            else guardActive = false;
        }

        IEnumerator Poll()
        {
            var wait = new WaitForSecondsRealtime(pollInterval);
            while (true)
            {
                if (!isLeader)
                {
                    var d = Read();
                    if (d != null && d.v > lastSeenV)
                    {
                        lastSeenV = d.v;
                        if (scheduledCo != null) { StopCoroutine(scheduledCo); scheduledCo = null; }
                        if (d.cmd == "PlayCurrentOrFirst") { MuteFollower(); ScheduleRemote(() => TryPlayCurrentOrFirst(), d.atUtc); }
                        else if (d.cmd == "PlayByStableId") { MuteFollower(); ScheduleRemote(() => TryPlayByStableIdOrFallback(d.sid, d.index, d.title), d.atUtc); }
                        else if (d.cmd == "StopPlay") { ScheduleRemote(() => { TryStopPlay(); UnmuteFollower(); }, d.atUtc); }
                        else if (d.cmd == "PlayNext") { MuteFollower(); ScheduleRemote(() => TryPlayNext(), d.atUtc); }
                        else if (d.cmd == "PlayPrev") { MuteFollower(); ScheduleRemote(() => TryPlayPrev(), d.atUtc); }
                    }
                }
                yield return wait;
            }
        }

        IEnumerator WireLoop()
        {
            var wait = new WaitForSecondsRealtime(0.35f);
            while (true)
            {
                ResolveRefs();
                WireMainControls();
                WireListButtons();
                yield return wait;
            }
        }

        void ResolveRefs()
        {
            if (handler == null) return;

            if (currentIndexFi == null)
                currentIndexFi = handler.GetType().GetField("currentIndex", BindingFlags.Instance | BindingFlags.NonPublic);

            if (findIndexByTitleMi == null)
                findIndexByTitleMi = handler.GetType().GetMethod("FindIndexByTitle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);

            if (entryIdFi == null && entriesList != null && entriesList.Count > 0)
                entryIdFi = entriesList[0].GetType().GetField("id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);


            if (mainPlayBtn == null)
            {
                var fi = handler.GetType().GetField("playButton", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                mainPlayBtn = fi != null ? fi.GetValue(handler) as Button : null;
            }
            if (stopBtn == null)
            {
                var fi = handler.GetType().GetField("stopButton", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                stopBtn = fi != null ? fi.GetValue(handler) as Button : null;
            }
            if (nextBtn == null)
            {
                var fi = handler.GetType().GetField("nextButton", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                nextBtn = fi != null ? fi.GetValue(handler) as Button : null;
            }
            if (prevBtn == null)
            {
                var fi = handler.GetType().GetField("prevButton", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                prevBtn = fi != null ? fi.GetValue(handler) as Button : null;
            }
            if (contentRoot == null)
            {
                var fi = handler.GetType().GetField("contentObject", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                contentRoot = fi != null ? fi.GetValue(handler) as Transform : null;
            }
            if (entriesFi == null)
                entriesFi = handler.GetType().GetField("entries", BindingFlags.Instance | BindingFlags.NonPublic);
            entriesList = entriesFi != null ? entriesFi.GetValue(handler) as IList : null;

            if (audioSource == null) audioSource = handler.GetComponentInChildren<AudioSource>(true);
            if (animator == null)
            {
                var fiA = handler.GetType().GetField("animator", BindingFlags.NonPublic | BindingFlags.Instance);
                animator = fiA != null ? fiA.GetValue(handler) as Animator : null;
                if (animator == null) animator = FindFirstObjectByType<Animator>();
            }

            if (utils == null) utils = handler.GetComponentInChildren<AvatarDancePlayerUtils>(true);
            if (utils != null)
            {
                if (utils.danceAudioSource != null) audioSource = utils.danceAudioSource;
                if (volumeSlider == null) volumeSlider = utils.volumeSlider;
            }
            if (volumeSlider == null) volumeSlider = handler.GetComponentInChildren<Slider>(true);
        }

        void WireMainControls()
        {
            if (mainPlayBtn != null && !wiredButtons.Contains(mainPlayBtn))
            {
                AddPreArm(mainPlayBtn, "play", null, -1, null);
                wiredButtons.Add(mainPlayBtn);
            }
            if (stopBtn != null && !wiredButtons.Contains(stopBtn))
            {
                AddPreArm(stopBtn, "stop", null, -1, null);
                wiredButtons.Add(stopBtn);
            }
            if (nextBtn != null && !wiredButtons.Contains(nextBtn))
            {
                AddPreArm(nextBtn, "next", null, -1, null);
                wiredButtons.Add(nextBtn);
            }
            if (prevBtn != null && !wiredButtons.Contains(prevBtn))
            {
                AddPreArm(prevBtn, "prev", null, -1, null);
                wiredButtons.Add(prevBtn);
            }
        }

        void WireListButtons()
        {
            if (contentRoot == null || entriesList == null) return;

            int n = Mathf.Min(contentRoot.childCount, entriesList.Count);
            for (int i = 0; i < n; i++)
            {
                var tr = contentRoot.GetChild(i);
                var btn = tr.GetComponentInChildren<Button>(true);
                if (btn == null) continue;
                if (wiredButtons.Contains(btn)) continue;

                if (entryStableIdFi == null)
                    entryStableIdFi = entriesList[i].GetType().GetField("stableId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                string sid = entryStableIdFi != null ? (entryStableIdFi.GetValue(entriesList[i]) as string) : null;
                string title = ExtractTitle(tr);

                AddPreArm(btn, "playByItem", sid, i, title);
                wiredButtons.Add(btn);
            }
        }

        void AddPreArm(Button b, string intent, string sid, int index, string title)
        {
            var hook = b.gameObject.GetComponent<PreArmHook>();
            if (hook == null) hook = b.gameObject.AddComponent<PreArmHook>();
            hook.owner = this;
            hook.intent = intent;
            hook.sid = sid;
            hook.index = index;
            hook.title = title;
        }

        public void BeginLeaderAction(string intent, string sid, int idx, string title, Button srcBtn)
        {
            if (!isLeader) return;

            double at = intent == "stop" ? UtcNow() : UtcNow() + leadSeconds;

            if (intent != "stop")
            {
                guardActive = true;
                guardUntilUtc = at;
                EnforceHold();
            }

            if (srcBtn != null)
            {
                srcBtn.interactable = false;
                tempDisabled.Add(srcBtn);
            }

            if (intent == "play")
            {
                ResolveRefs();
                int cur = GetCurrentIndex();
                var visible = VisibleFilteredIndices();
                int target = visible.Count > 0
                    ? (visible.Contains(cur) ? cur : visible[0])
                    : (cur < 0 ? 0 : cur);

                GetEntryData(target, out var nsid, out var ntit);
                ScheduleLocal(() => TryPlayByStableIdOrFallback(nsid, target, ntit), at);
                Broadcast("PlayByStableId", nsid, target, ntit, at);
            }
            else if (intent == "playByItem")
            {
                ScheduleLocal(() => TryPlayByStableIdOrFallback(sid, idx, title), at);
                Broadcast("PlayByStableId", sid, idx, title, at);
            }
            else if (intent == "next")
            {
                ResolveRefs();
                int target = NextFromFiltered(true);
                GetEntryData(target, out var nsid, out var ntit);
                ScheduleLocal(() => TryPlayByStableIdOrFallback(nsid, target, ntit), at);
                Broadcast("PlayByStableId", nsid, target, ntit, at);
            }
            else if (intent == "prev")
            {
                ResolveRefs();
                int target = NextFromFiltered(false);
                GetEntryData(target, out var nsid, out var ntit);
                ScheduleLocal(() => TryPlayByStableIdOrFallback(nsid, target, ntit), at);
                Broadcast("PlayByStableId", nsid, target, ntit, at);
            }
            else if (intent == "stop")
            {
                ScheduleLocal(() => { TryStopPlay(); }, at);
                Broadcast("StopPlay", null, -1, null, at);
            }
        }

        int GetCurrentIndex()
        {
            if (handler == null || currentIndexFi == null) return -1;
            try { return (int)currentIndexFi.GetValue(handler); } catch { return -1; }
        }

        List<int> VisibleFilteredIndices()
        {
            var list = new List<int>();
            if (contentRoot == null) return list;
            if (findIndexByTitleMi == null) return list;
            for (int i = 0; i < contentRoot.childCount; i++)
            {
                var tr = contentRoot.GetChild(i);
                if (!tr.gameObject.activeSelf) continue;
                var t = ExtractTitle(tr);
                if (string.IsNullOrEmpty(t)) continue;
                int idx = (int)findIndexByTitleMi.Invoke(handler, new object[] { t });
                if (idx >= 0 && !list.Contains(idx)) list.Add(idx);
            }
            return list;
        }

        void GetEntryData(int idx, out string sid, out string title)
        {
            sid = null;
            title = null;
            if (entriesList == null || idx < 0 || idx >= entriesList.Count) return;
            var e = entriesList[idx];
            if (entryStableIdFi == null)
                entryStableIdFi = e.GetType().GetField("stableId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (entryIdFi == null)
                entryIdFi = e.GetType().GetField("id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (entryStableIdFi != null) sid = entryStableIdFi.GetValue(e) as string;
            if (entryIdFi != null) title = entryIdFi.GetValue(e) as string;
        }

        int NextFromFiltered(bool forward)
        {
            int cur = GetCurrentIndex();
            if (entriesList == null || entriesList.Count == 0) return -1;

            var vis = VisibleFilteredIndices();
            if (vis.Count == 0)
            {
                if (cur < 0) return 0;
                if (forward) return (cur + 1) % entriesList.Count;
                return cur <= 0 ? entriesList.Count - 1 : cur - 1;
            }
            int pos = vis.IndexOf(cur);
            if (pos < 0) return vis[0];
            if (forward) return vis[(pos + 1) % vis.Count];
            return pos == 0 ? vis[vis.Count - 1] : vis[pos - 1];
        }



        void EnforceHold()
        {
            if (handler != null)
            {
                var stopMi = handler.GetType().GetMethod("StopPlay", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (stopMi != null) stopMi.Invoke(handler, null);
            }
            if (audioSource != null)
            {
                try { audioSource.Stop(); } catch { }
                audioSource.time = 0f;
            }
            FreezeAnimator();
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
            ResolveRefs();
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

        void UnmuteFollower()
        {
            if (isLeader) return;
            ResolveRefs();
            if (followerMuted)
            {
                if (volumeSlider != null && storedSliderValue >= 0f) volumeSlider.value = storedSliderValue;
                if (audioSource != null && storedAudioVolume >= 0f) audioSource.volume = storedAudioVolume;
                followerMuted = false;
            }
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
            if (handler == null) return;
            var mi = handler.GetType().GetMethod("TryPlayCurrentOrFirst", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (mi != null) mi.Invoke(handler, null);
        }

        void TryStopPlay()
        {
            if (handler == null) return;
            var mi = handler.GetType().GetMethod("StopPlay", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (mi != null) mi.Invoke(handler, null);
        }

        void TryPlayNext()
        {
            if (handler == null) return;
            var mi = handler.GetType().GetMethod("PlayNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (mi != null) mi.Invoke(handler, null);
        }

        void TryPlayPrev()
        {
            if (handler == null) return;
            var mi = handler.GetType().GetMethod("PlayPrev", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (mi != null) mi.Invoke(handler, null);
        }

        void TryPlayByStableIdOrFallback(string sid, int idx, string title)
        {
            if (handler == null)
            {
                TryPlayCurrentOrFirst();
                return;
            }

            if (!string.IsNullOrEmpty(sid))
            {
                var mSid = handler.GetType().GetMethod("PlayByStableId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
                if (mSid != null)
                {
                    var ok = mSid.Invoke(handler, new object[] { sid });
                    if (ok is bool b && b) return;
                }
            }

            if (idx >= 0)
            {
                var mIdx = handler.GetType().GetMethod("PlayIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int) }, null);
                if (mIdx != null) { mIdx.Invoke(handler, new object[] { idx }); return; }
            }

            if (!string.IsNullOrEmpty(title))
            {
                var mTitle = FindByTitleMethod(handler);
                if (mTitle != null) { mTitle.Invoke(handler, new object[] { title }); return; }
            }

            TryPlayCurrentOrFirst();
        }

        MethodInfo FindByTitleMethod(object target)
        {
            var t = target.GetType();
            var cands = new[] { "PlayByTitle", "PlaySongByTitle", "SelectAndPlayByTitle", "PlayByName" };
            for (int i = 0; i < cands.Length; i++)
            {
                var mi = t.GetMethod(cands[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
                if (mi != null) return mi;
            }
            return null;
        }

        string ExtractTitle(Transform item)
        {
            var tf = FindByExactName<Text>(item, "TitleFallback");
            if (tf && !string.IsNullOrEmpty(tf.text)) return tf.text.Trim();
            var tt = FindByExactName<TMP_Text>(item, "Title");
            if (tt && !string.IsNullOrEmpty(tt.text)) return tt.text.Trim();
            var tf2 = FindByNameContains<Text>(item, "titlefallback");
            if (tf2 && !string.IsNullOrEmpty(tf2.text)) return tf2.text.Trim();
            var tt2 = FindByNameContains<TMP_Text>(item, "title");
            if (tt2 && !string.IsNullOrEmpty(tt2.text)) return tt2.text.Trim();
            return null;
        }

        T FindByExactName<T>(Transform root, string name) where T : Component
        {
            var arr = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < arr.Length; i++)
                if (arr[i].name == name) return arr[i].GetComponent<T>();
            return null;
        }

        T FindByNameContains<T>(Transform root, string partLower) where T : Component
        {
            var arr = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < arr.Length; i++)
                if (arr[i].name.ToLowerInvariant().Contains(partLower)) return arr[i].GetComponent<T>();
            return null;
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
                if (!File.Exists(path)) return null;
                var s = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(s)) return null;
                return JsonUtility.FromJson<BusCmd>(s);
            }
            catch { return null; }
        }

        void SafeWrite(BusCmd d)
        {
            try
            {
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(d));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
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
    }
}
*/
