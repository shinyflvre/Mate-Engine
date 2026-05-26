using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Steamworks;
using Newtonsoft.Json;
using System.IO.Compression;

public class SteamWorkshopAutoLoader : MonoBehaviour
{
    private const string WorkshopFolderName = "Steam Workshop";
    private string workshopFolderPath => MateEnginePaths.WorkshopDir;
    private string modsFolderPath => MateEnginePaths.ModsDir;
    private readonly List<string> allowedExtensions = new List<string> { ".vrm", ".me", ".unity3d" };
    private AvatarLibraryMenu library;
    private Callback<DownloadItemResult_t> downloadCallback;
    private Callback<RemoteStoragePublishedFileSubscribed_t> subscribedCallback;
    private Callback<RemoteStoragePublishedFileUnsubscribed_t> unsubscribedCallback;
    private bool isRefreshing = false;
    private bool pendingRefresh = false;
    public bool hadChangesLastRun { get; private set; }
    private string modsMapPath => Path.Combine(MateEnginePaths.DataRoot, "mods_workshop_map.json");

    private void Awake()
    {
        if (SteamManager.Initialized)
        {
            downloadCallback = Callback<DownloadItemResult_t>.Create(OnWorkshopItemDownloaded);
            subscribedCallback = Callback<RemoteStoragePublishedFileSubscribed_t>.Create(OnWorkshopItemSubscribed);
            unsubscribedCallback = Callback<RemoteStoragePublishedFileUnsubscribed_t>.Create(OnWorkshopItemUnsubscribed);
        }
    }

    private void Start()
    {
        if (!SteamManager.Initialized) return;
        library = FindFirstObjectByType<AvatarLibraryMenu>();
        Directory.CreateDirectory(workshopFolderPath);
        Directory.CreateDirectory(modsFolderPath);
        RefreshWorkshopItems();
    }

    public void RefreshWorkshopAvatars()
    {
        RefreshWorkshopItems();
    }

    private void OnWorkshopItemSubscribed(RemoteStoragePublishedFileSubscribed_t data)
    {
        SteamUGC.DownloadItem(data.m_nPublishedFileId, true);
        RefreshWorkshopItems();
    }

    private void OnWorkshopItemUnsubscribed(RemoteStoragePublishedFileUnsubscribed_t data)
    {
        RefreshWorkshopItems();
    }

    private void OnWorkshopItemDownloaded(DownloadItemResult_t result)
    {
        if (result.m_eResult == EResult.k_EResultOK) RefreshWorkshopItems();
    }

    public void RefreshWorkshopItems()
    {
        if (!SteamManager.Initialized) return;
        if (isRefreshing)
        {
            pendingRefresh = true;
            return;
        }
        StartCoroutine(RefreshRoutine());
    }

    private IEnumerator RefreshRoutine()
    {
        isRefreshing = true;
        pendingRefresh = false;
        hadChangesLastRun = false;

        List<PublishedFileId_t> subscribed = new List<PublishedFileId_t>();
        uint count = SteamUGC.GetNumSubscribedItems();
        if (count > 0)
        {
            PublishedFileId_t[] tmp = new PublishedFileId_t[count];
            SteamUGC.GetSubscribedItems(tmp, count);
            subscribed.AddRange(tmp);
        }

        var snapshot = new List<(PublishedFileId_t id, string installPath, bool needsUpdate)>();
        for (int i = 0; i < subscribed.Count; i++)
        {
            var fileId = subscribed[i];
            uint state = SteamUGC.GetItemState(fileId);
            bool isInstalled = (state & (uint)EItemState.k_EItemStateInstalled) != 0;
            bool needsUpdate = (state & (uint)EItemState.k_EItemStateNeedsUpdate) != 0;
            if (!isInstalled || needsUpdate) SteamUGC.DownloadItem(fileId, true);

            bool installed = false;
            string installPath = null;
            float timeout = 10f;
            while (timeout > 0f)
            {
                installed = SteamUGC.GetItemInstallInfo(fileId, out ulong _, out installPath, 2048, out _);
                if (installed && !string.IsNullOrEmpty(installPath) && Directory.Exists(installPath)) break;
                yield return new WaitForSeconds(0.1f);
                timeout -= 0.1f;
            }
            if (installed && !string.IsNullOrEmpty(installPath) && Directory.Exists(installPath))
                snapshot.Add((fileId, installPath, needsUpdate));
            yield return null;
        }

        string persistentPath = MateEnginePaths.DataRoot;
        string tempCachePath = MateEnginePaths.CacheRoot;
        string workshopDir = workshopFolderPath;
        string modsDir = modsFolderPath;

        var task = Task.Run(() =>
        {
            var res = new WorkResult();
            var subscribedIds = new HashSet<ulong>(snapshot.Select(s => s.id.m_PublishedFileId));

            bool modsCleaned = CleanupUnsubscribedWorkshopMods(subscribedIds, modsDir, tempCachePath, persistentPath);
            bool avatarsCleaned = CleanupUnsubscribedWorkshopAvatars(subscribedIds, workshopDir, persistentPath);

            var avatarEntries = ReadAvatarEntries(persistentPath);
            bool avatarChanged = false;
            bool modsChanged = modsCleaned;

            foreach (var item in snapshot)
            {
                string[] topFiles = Array.Empty<string>();
                try { topFiles = Directory.GetFiles(item.installPath, "*", SearchOption.TopDirectoryOnly); } catch { }

                string file = topFiles.FirstOrDefault(f => allowedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
                if (string.IsNullOrEmpty(file)) continue;

                string ext = Path.GetExtension(file).ToLowerInvariant();

                if (ext == ".unity3d")
                {
                    bool changed = CopyToMods(file, item.needsUpdate, modsDir);
                    TryRecordModMapping(Path.Combine(modsDir, Path.GetFileName(file)), item.id.m_PublishedFileId, persistentPath);
                    if (changed) modsChanged = true;
                    continue;
                }

                if (ext == ".me")
                {
                    bool isDance = IsDanceME(file);
                    if (isDance)
                    {
                        bool changed = CopyToMods(file, item.needsUpdate, modsDir);
                        TryRecordModMapping(Path.Combine(modsDir, Path.GetFileName(file)), item.id.m_PublishedFileId, persistentPath);
                        TryCopyThumbFromME(file, persistentPath);
                        if (changed) modsChanged = true;
                        continue;
                    }
                    bool avChanged = HandleAvatarFile(item.id, file, item.needsUpdate, avatarEntries, workshopDir, persistentPath);
                    if (avChanged) avatarChanged = true;
                    continue;
                }

                if (ext == ".vrm")
                {
                    bool avChanged = HandleAvatarFile(item.id, file, item.needsUpdate, avatarEntries, workshopDir, persistentPath);
                    if (avChanged) avatarChanged = true;
                    continue;
                }
            }

            if (avatarChanged || avatarsCleaned) SaveAvatarEntries(avatarEntries, persistentPath);

            res.AvatarsChanged = avatarChanged || avatarsCleaned;
            res.ModsChanged = modsChanged;
            return res;
        });

        while (!task.IsCompleted) yield return null;

        if (!task.IsFaulted)
        {
            var res = task.Result;
            if (res.AvatarsChanged && library != null) library.ReloadAvatars();
            if (res.ModsChanged) NotifyMods();
            hadChangesLastRun = res.AvatarsChanged || res.ModsChanged;
        }

        isRefreshing = false;
        if (pendingRefresh) RefreshWorkshopItems();
    }

    private bool HandleAvatarFile(PublishedFileId_t fileId, string sourcePath, bool needsUpdate, List<AvatarLibraryMenu.AvatarEntry> avatarEntries, string workshopDir, string persistentPath)
    {
        string baseName = Path.GetFileName(sourcePath);
        string targetPath = Path.Combine(workshopDir, baseName);

        var existingSamePath = avatarEntries.FirstOrDefault(e => e.filePath == targetPath);
        if (existingSamePath != null && existingSamePath.steamFileId != fileId.m_PublishedFileId)
            targetPath = Path.Combine(workshopDir, $"{fileId.m_PublishedFileId}_{baseName}");

        bool copiedModel = CopyFileIfNeeded(sourcePath, targetPath, needsUpdate);

        bool alreadyRegistered = avatarEntries.Any(e => e.filePath == targetPath);

        string installDir = Path.GetDirectoryName(sourcePath) ?? "";
        string displayName = Path.GetFileNameWithoutExtension(sourcePath);
        string author = "Workshop";
        string version = "1.0";
        string format = sourcePath.EndsWith(".me", StringComparison.OrdinalIgnoreCase) ? ".ME" : "VRM";
        int polygonCount = 0;
        bool isNSFW = false;

        string metaPath = Path.Combine(installDir, "metadata.json");
        if (File.Exists(metaPath))
        {
            try
            {
                var meta = JsonConvert.DeserializeObject<Dictionary<string, object>>(File.ReadAllText(metaPath));
                if (meta != null)
                {
                    if (meta.TryGetValue("displayName", out var d)) displayName = d?.ToString() ?? displayName;
                    if (meta.TryGetValue("author", out var a)) author = a?.ToString() ?? author;
                    if (meta.TryGetValue("version", out var v)) version = v?.ToString() ?? version;
                    if (meta.TryGetValue("fileType", out var f)) format = f?.ToString() ?? format;
                    if (meta.TryGetValue("polygonCount", out var p)) polygonCount = Convert.ToInt32(p);
                    if (meta.TryGetValue("isNSFW", out var n)) isNSFW = Convert.ToBoolean(n);
                }
            }
            catch { }
        }

        string thumbnailsFolder = Path.Combine(persistentPath, "Thumbnails");
        try { Directory.CreateDirectory(thumbnailsFolder); } catch { }
        string thumbFileName = Path.GetFileNameWithoutExtension(targetPath) + "_thumb.png";
        string thumbSourceA = Path.Combine(installDir, Path.GetFileNameWithoutExtension(sourcePath) + "_thumb.png");
        string thumbSourceB = Path.Combine(Path.GetDirectoryName(targetPath) ?? "", Path.GetFileNameWithoutExtension(targetPath) + "_thumb.png");
        string thumbSource = File.Exists(thumbSourceA) ? thumbSourceA : thumbSourceB;

        string thumbnailPath = "";
        if (File.Exists(thumbSource))
        {
            try
            {
                string outPath = Path.Combine(thumbnailsFolder, Path.GetFileName(thumbFileName));
                CopyFileOverwrite(thumbSource, outPath);
                thumbnailPath = outPath;
            }
            catch { }
        }

        if (!alreadyRegistered)
        {
            var newEntry = new AvatarLibraryMenu.AvatarEntry
            {
                displayName = displayName,
                author = author,
                version = version,
                fileType = format,
                filePath = targetPath,
                thumbnailPath = thumbnailPath,
                polygonCount = polygonCount,
                isSteamWorkshop = true,
                steamFileId = fileId.m_PublishedFileId,
                isNSFW = isNSFW,
                isOwner = false
            };
            avatarEntries.Add(newEntry);
            return true;
        }
        else
        {
            var entry = avatarEntries.First(e => e.filePath == targetPath);
            bool metaChanged = false;

            if (entry.displayName != displayName) { entry.displayName = displayName; metaChanged = true; }
            if (entry.author != author) { entry.author = author; metaChanged = true; }
            if (entry.version != version) { entry.version = version; metaChanged = true; }
            if (entry.fileType != format) { entry.fileType = format; metaChanged = true; }
            if (entry.polygonCount != polygonCount) { entry.polygonCount = polygonCount; metaChanged = true; }
            if (entry.isNSFW != isNSFW) { entry.isNSFW = isNSFW; metaChanged = true; }
            if (!string.IsNullOrEmpty(thumbnailPath) && entry.thumbnailPath != thumbnailPath) { entry.thumbnailPath = thumbnailPath; metaChanged = true; }
            if (entry.steamFileId == 0) { entry.isSteamWorkshop = true; entry.steamFileId = fileId.m_PublishedFileId; metaChanged = true; }

            if (metaChanged || copiedModel) return true;
            return false;
        }
    }

    private bool IsDanceME(string mePath)
    {
        try
        {
            using (var fs = new FileStream(mePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                bool hasDanceMeta = zip.Entries.Any(e => string.Equals(e.FullName, "dance_meta.json", StringComparison.OrdinalIgnoreCase));
                bool hasBundle = zip.Entries.Any(e => e.FullName.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase));
                return hasDanceMeta || hasBundle;
            }
        }
        catch { return false; }
    }

    private bool CopyToMods(string sourcePath, bool needsUpdate, string modsDir)
    {
        try
        {
            Directory.CreateDirectory(modsDir);
            string target = Path.Combine(modsDir, Path.GetFileName(sourcePath));
            if (!File.Exists(target) || needsUpdate)
            {
                CopyFileOverwrite(sourcePath, target);
                return true;
            }
        }
        catch { }
        return false;
    }

    private bool CopyFileIfNeeded(string source, string target, bool needsUpdate)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? "");
            if (!File.Exists(target) || needsUpdate)
            {
                CopyFileOverwrite(source, target);
                return true;
            }
        }
        catch { }
        return false;
    }

    private void CopyFileOverwrite(string source, string target)
    {
        try
        {
            using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, true);
            using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, true);
            src.CopyTo(dst);
        }
        catch { }
    }

    private void TryRecordModMapping(string targetPath, ulong fileId, string persistentPath)
    {
        try
        {
            var map = LoadModWorkshopMap(persistentPath);
            string key = Path.GetFileName(targetPath);
            map[key] = fileId;
            SaveModWorkshopMap(map, persistentPath);
        }
        catch { }
    }

    private Dictionary<string, ulong> LoadModWorkshopMap(string persistentPath)
    {
        try
        {
            string p = Path.Combine(persistentPath, "mods_workshop_map.json");
            if (File.Exists(p))
                return JsonConvert.DeserializeObject<Dictionary<string, ulong>>(File.ReadAllText(p)) ?? new Dictionary<string, ulong>();
        }
        catch { }
        return new Dictionary<string, ulong>();
    }

    private void SaveModWorkshopMap(Dictionary<string, ulong> map, string persistentPath)
    {
        try
        {
            string p = Path.Combine(persistentPath, "mods_workshop_map.json");
            File.WriteAllText(p, JsonConvert.SerializeObject(map, Formatting.Indented));
        }
        catch { }
    }

    private bool CleanupUnsubscribedWorkshopMods(HashSet<ulong> subscribedIds, string modsDir, string tempCachePath, string persistentPath)
    {
        var map = LoadModWorkshopMap(persistentPath);
        bool changed = false;
        var toRemove = new List<string>();

        foreach (var kv in map)
        {
            if (!subscribedIds.Contains(kv.Value))
            {
                string filePath = Path.Combine(modsDir, kv.Key);
                try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }

                string name = Path.GetFileNameWithoutExtension(kv.Key);
                string cacheDir = Path.Combine(tempCachePath, "ME_Cache", name);
                try { if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true); } catch { }

                string thumb = Path.Combine(persistentPath, "Thumbnails", name + "_thumb.png");
                try { if (File.Exists(thumb)) File.Delete(thumb); } catch { }

                toRemove.Add(kv.Key);
                changed = true;
            }
        }

        for (int i = 0; i < toRemove.Count; i++) map.Remove(toRemove[i]);
        if (changed) SaveModWorkshopMap(map, persistentPath);
        return changed;
    }

    private bool CleanupUnsubscribedWorkshopAvatars(HashSet<ulong> subscribedIds, string workshopDir, string persistentPath)
    {
        var avatars = ReadAvatarEntries(persistentPath);
        bool changed = false;
        string workshopFull = Path.GetFullPath(workshopDir);
        string thumbsDir = Path.GetFullPath(Path.Combine(persistentPath, "Thumbnails"));

        for (int i = avatars.Count - 1; i >= 0; i--)
        {
            var a = avatars[i];
            if (!(a.isSteamWorkshop && a.steamFileId != 0 && !subscribedIds.Contains(a.steamFileId))) continue;

            string fileFull = string.IsNullOrEmpty(a.filePath) ? "" : Path.GetFullPath(a.filePath);
            string thumbFull = string.IsNullOrEmpty(a.thumbnailPath) ? "" : Path.GetFullPath(a.thumbnailPath);
            bool isInsideWorkshop = !string.IsNullOrEmpty(fileFull) && fileFull.StartsWith(workshopFull, StringComparison.OrdinalIgnoreCase);

            if (isInsideWorkshop)
            {
                try { if (File.Exists(fileFull)) File.Delete(fileFull); } catch { }
                try
                {
                    if (!string.IsNullOrEmpty(thumbFull) && thumbFull.StartsWith(thumbsDir, StringComparison.OrdinalIgnoreCase) && File.Exists(thumbFull))
                        File.Delete(thumbFull);
                }
                catch { }
                avatars.RemoveAt(i);
                changed = true;
            }
            else
            {
                a.isSteamWorkshop = false;
                changed = true;
            }
        }

        if (changed) SaveAvatarEntries(avatars, persistentPath);
        return changed;
    }

    private List<AvatarLibraryMenu.AvatarEntry> ReadAvatarEntries(string persistentPath)
    {
        string path = Path.Combine(persistentPath, "avatars.json");
        if (!File.Exists(path)) return new List<AvatarLibraryMenu.AvatarEntry>();
        try { return JsonConvert.DeserializeObject<List<AvatarLibraryMenu.AvatarEntry>>(File.ReadAllText(path)) ?? new List<AvatarLibraryMenu.AvatarEntry>(); }
        catch { return new List<AvatarLibraryMenu.AvatarEntry>(); }
    }

    private void SaveAvatarEntries(List<AvatarLibraryMenu.AvatarEntry> entries, string persistentPath)
    {
        string path = Path.Combine(persistentPath, "avatars.json");
        try { File.WriteAllText(path, JsonConvert.SerializeObject(entries, Formatting.Indented)); } catch { }
    }

    private void TryCopyThumbFromME(string mePath, string persistentPath)
    {
        try
        {
            using var fs = File.OpenRead(mePath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            var entry = zip.GetEntry("thumb.png");
            if (entry == null) return;

            string name = Path.GetFileNameWithoutExtension(mePath);
            string outDir = Path.Combine(persistentPath, "Thumbnails");
            Directory.CreateDirectory(outDir);
            string outPath = Path.Combine(outDir, name + "_thumb.png");

            using var zs = entry.Open();
            using var fo = File.Create(outPath);
            zs.CopyTo(fo);
        }
        catch { }
    }

    private void NotifyMods()
    {
        StartCoroutine(NotifyModsNextFrame());
    }

    private IEnumerator NotifyModsNextFrame()
    {
        yield return null;
        var modHandler = FindFirstObjectByType<MEModHandler>();
        if (modHandler != null)
        {
            var mi = typeof(MEModHandler).GetMethod("LoadAllModsInFolder", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (mi != null) mi.Invoke(modHandler, null);
        }
        var dance = FindFirstObjectByType<CustomDancePlayer.AvatarDanceHandler>();
        if (dance != null) dance.RescanMods();
    }

    private struct WorkResult
    {
        public bool AvatarsChanged;
        public bool ModsChanged;
    }
}
