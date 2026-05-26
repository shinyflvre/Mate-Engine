using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace CustomDancePlayer
{
    public class AvatarDancePlayerTools : MonoBehaviour
    {
        public AvatarDanceHandler handler;
        public Transform contentRoot;
        public InputField searchInput;
        public TMP_InputField tmpSearchInput;
        public Toggle showFavoriteToggle;
        public bool ignoreCase = true;
        public string favoritesFileName = "favorite_songs.json";
        public Toggle loopToggle;
        public Toggle shuffleToggle;

        readonly Dictionary<Transform, string> titleRaw = new();
        readonly Dictionary<Transform, string> titleNorm = new();
        readonly HashSet<Transform> wired = new();
        readonly HashSet<string> favorites = new(StringComparer.Ordinal);
        int lastChildCount = -1;
        string lastQuery = "";

        [Serializable]
        class FavData { public List<string> titles = new(); }

        void Awake()
        {
            if (!handler) handler = FindFirstObjectByType<AvatarDanceHandler>();
            if (!contentRoot && handler) contentRoot = handler.contentObject;
            LoadFavorites();
        }

        void OnEnable()
        {
            HookInputs(true);
            ReindexAndWire();
            ApplyFilter("");
        }

        void OnDisable()
        {
            HookInputs(false);
        }

        void Update()
        {
            if (!contentRoot && handler) contentRoot = handler.contentObject;
            if (!contentRoot) return;
            if (contentRoot.childCount != lastChildCount) { ReindexAndWire(); ApplyFilter(lastQuery); }
        }
        void HookInputs(bool on)
        {
            if (searchInput)
            {
                if (on) searchInput.onValueChanged.AddListener(OnSearchText);
                else searchInput.onValueChanged.RemoveListener(OnSearchText);
            }
            if (tmpSearchInput)
            {
                if (on) tmpSearchInput.onValueChanged.AddListener(OnSearchText);
                else tmpSearchInput.onValueChanged.RemoveListener(OnSearchText);
            }
            if (showFavoriteToggle)
            {
                if (on) showFavoriteToggle.onValueChanged.AddListener(OnShowFavChanged);
                else showFavoriteToggle.onValueChanged.RemoveListener(OnShowFavChanged);
            }
            if (loopToggle)
            {
                if (on) loopToggle.onValueChanged.AddListener(OnLoopChanged);
                else loopToggle.onValueChanged.RemoveListener(OnLoopChanged);
            }
            if (shuffleToggle)
            {
                if (on) shuffleToggle.onValueChanged.AddListener(OnShuffleChanged);
                else shuffleToggle.onValueChanged.RemoveListener(OnShuffleChanged);
            }
        }
        void OnLoopChanged(bool v)
        {
            if (!handler) handler = FindFirstObjectByType<AvatarDanceHandler>();
            if (handler) handler.loopOn = v;
        }

        void OnShuffleChanged(bool v)
        {
            if (!handler) handler = FindFirstObjectByType<AvatarDanceHandler>();
            if (handler) handler.shuffleOn = v;
        }


        void OnSearchText(string s)
        {
            ApplyFilter(s);
        }

        void OnShowFavChanged(bool _)
        {
            ApplyFilter(lastQuery);
        }

        void ReindexAndWire()
        {
            titleRaw.Clear();
            titleNorm.Clear();
            if (!contentRoot) return;
            lastChildCount = contentRoot.childCount;

            for (int i = 0; i < contentRoot.childCount; i++)
            {
                var t = contentRoot.GetChild(i);
                string raw = ExtractTitle(t);
                string norm = Normalize(raw);
                titleRaw[t] = raw;
                titleNorm[t] = norm;

                if (!wired.Contains(t))
                {
                    var fav = FindToggleByExactName(t, "Favorite");
                    if (fav)
                    {
                        bool isFav = favorites.Contains(raw);
                        if (fav.isOn != isFav) fav.isOn = isFav;
                        fav.onValueChanged.AddListener(v => OnItemFavoriteChanged(t, raw, v));
                    }
                    wired.Add(t);
                }
            }
        }

        void OnItemFavoriteChanged(Transform item, string rawTitle, bool isOn)
        {
            if (string.IsNullOrEmpty(rawTitle)) return;
            if (isOn) favorites.Add(rawTitle); else favorites.Remove(rawTitle);
            SaveFavorites();
            if (showFavoriteToggle && showFavoriteToggle.isOn) ApplyFilter(lastQuery);
        }

        void ApplyFilter(string query)
        {
            lastQuery = Normalize(query);
            if (!contentRoot) return;

            bool showFavOnly = showFavoriteToggle && showFavoriteToggle.isOn;
            bool showAll = string.IsNullOrEmpty(lastQuery);

            for (int i = 0; i < contentRoot.childCount; i++)
            {
                var t = contentRoot.GetChild(i);
                if (!titleRaw.TryGetValue(t, out var raw)) raw = ExtractTitle(t);
                if (!titleNorm.TryGetValue(t, out var norm)) norm = Normalize(raw);

                bool matchTitle = showAll || norm.Contains(lastQuery);
                bool matchFav = !showFavOnly || favorites.Contains(raw);
                t.gameObject.SetActive(matchTitle && matchFav);
            }

            if (!handler) return;

            if (showFavOnly)
            {
                var indices = new List<int>();
                for (int i = 0; i < contentRoot.childCount; i++)
                {
                    var t = contentRoot.GetChild(i);
                    if (!t.gameObject.activeSelf) continue;
                    if (!titleRaw.TryGetValue(t, out var raw)) raw = ExtractTitle(t);
                    int idx = handler.FindIndexByTitle(raw);
                    if (idx >= 0) indices.Add(idx);
                }
                handler.SetQueueByIndices(indices);
            }
            else
            {
                handler.SetQueueByIndices(null);
            }
        }


        string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Trim();
            s = s.Replace("\u200B", "").Replace("\u200C", "").Replace("\u200D", "").Replace("\uFEFF", "");
            return ignoreCase ? s.ToLowerInvariant() : s;
        }

        string ExtractTitle(Transform item)
        {
            var tf = FindByExactName<Text>(item, "TitleFallback");
            if (tf && !string.IsNullOrEmpty(tf.text)) return tf.text;
            var tt = FindByExactName<TMP_Text>(item, "Title");
            if (tt && !string.IsNullOrEmpty(tt.text)) return tt.text;
            var tf2 = FindByNameContains<Text>(item, "titlefallback");
            if (tf2 && !string.IsNullOrEmpty(tf2.text)) return tf2.text;
            var tt2 = FindByNameContains<TMP_Text>(item, "title");
            if (tt2 && !string.IsNullOrEmpty(tt2.text)) return tt2.text;
            var anyTmp = item.GetComponentInChildren<TMP_Text>(true);
            if (anyTmp && !string.IsNullOrEmpty(anyTmp.text)) return anyTmp.text;
            var anyText = item.GetComponentInChildren<Text>(true);
            if (anyText && !string.IsNullOrEmpty(anyText.text)) return anyText.text;
            return "";
        }

        Toggle FindToggleByExactName(Transform root, string name)
        {
            var arr = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < arr.Length; i++)
                if (arr[i].name == name) return arr[i].GetComponent<Toggle>();
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

        void SaveFavorites()
        {
            try
            {
                var data = new FavData { titles = new List<string>(favorites) };
                var json = JsonUtility.ToJson(data, true);
                var path = MateEnginePaths.DataFilePath(favoritesFileName);
                File.WriteAllText(path, json);
            }
            catch { }
        }

        void LoadFavorites()
        {
            try
            {
                var path = MateEnginePaths.DataFilePath(favoritesFileName);
                if (!File.Exists(path)) return;
                var json = File.ReadAllText(path);
                var data = JsonUtility.FromJson<FavData>(json);
                favorites.Clear();
                if (data != null && data.titles != null)
                    for (int i = 0; i < data.titles.Count; i++)
                        if (!string.IsNullOrEmpty(data.titles[i])) favorites.Add(data.titles[i]);
            }
            catch { }
        }
    }
}
