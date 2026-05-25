using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.EventSystems;
using System.Collections;
using System.IO;
using Steamworks;
using SFB;

public class ModUploadHoldHandler : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
{
    [Header("UI")]
    public Slider progressSlider;
    public TMP_Text labelText;
    public TMP_Text errorText;
    public RawImage previewImage;

    [Header("Audio")]
    public AudioSource audioSource;
    public AudioClip completeSound;
    public AudioClip tickSound;

    [Header("Labels")]
    public string fallbackUpload = "Upload";
    public string fallbackUpdate = "Update";

    [Header("Hold")]
    public float holdSeconds = 3f;

    Button btn;
    ModUploadButton modBtn;
    Coroutine holdRoutine;
    bool holding;

    const long MaxBytes = 2 * 1024 * 1024;

    void Awake()
    {
        btn = GetComponent<Button>();
        modBtn = GetComponent<ModUploadButton>();
        if (modBtn != null && modBtn.progressBar == null && progressSlider != null) modBtn.progressBar = progressSlider;
    }

    void OnEnable()
    {
        holding = false;
        CancelHold();
        SetInteractable(true);
        EnsureLocalThumbLink();
        UpdateLabel();
        ClearError();
        TryLoadPreviewFromPath(modBtn != null ? modBtn.thumbnailPath : null);
    }

    void OnDisable()
    {
        holding = false;
        CancelHold();
        SetInteractable(true);
        UpdateLabel();
    }

    public void OnPointerDown(PointerEventData e)
    {
        if (!CanUpload())
        {
            SetError("Not ready");
            return;
        }

        if (IsThumbnailMissingOrTooBig())
        {
            if (PickThumbnail(out string savedPath))
            {
                modBtn.thumbnailPath = savedPath;
                TryLoadPreviewFromPath(savedPath);
                ClearError();
                UpdateLabel();
            }
            return;
        }

        if (holdRoutine == null)
        {
            holding = true;
            holdRoutine = StartCoroutine(HoldAndUpload());
        }
    }

    public void OnPointerUp(PointerEventData e)
    {
        holding = false;
    }

    IEnumerator HoldAndUpload()
    {
        float t = 0f;
        int lastShown = -1;
        SetInteractable(false);

        while (holding && t < holdSeconds)
        {
            t += Time.deltaTime;
            int left = Mathf.CeilToInt(holdSeconds - t);
            if (left != lastShown)
            {
                lastShown = left;
                SetLabel(left > 0 ? left.ToString() : "0");
                if (audioSource != null && tickSound != null && left > 0) audioSource.PlayOneShot(tickSound);
            }
            yield return null;
        }

        if (holding)
        {
            if (audioSource != null && completeSound != null) audioSource.PlayOneShot(completeSound);
            yield return null;
            StartUpload();
        }

        UpdateLabel();
        SetInteractable(true);
        holdRoutine = null;
    }

    void StartUpload()
    {
        ClearError();
        if (modBtn == null || string.IsNullOrEmpty(modBtn.filePath)) { SetError("Missing file"); return; }
        if (IsThumbnailMissingOrTooBig()) { SetError("Thumbnail required (≤2MB)"); return; }

        if (modBtn.progressBar == null && progressSlider != null) modBtn.progressBar = progressSlider;

        modBtn.UploadNow();   
        SetLabel("Uploading");
    }


    void UpdateLabel()
    {
        if (labelText == null) return;
        bool isUpdate = ResolveWorkshopIdForPath(modBtn != null ? modBtn.filePath : null) != 0UL;
        labelText.text = isUpdate ? fallbackUpdate : fallbackUpload;
    }

    void SetLabel(string s)
    {
        if (labelText != null) labelText.text = s;
    }

    void SetError(string s)
    {
        if (errorText != null) errorText.text = s;
    }

    void ClearError()
    {
        if (errorText != null) errorText.text = "";
    }

    bool CanUpload()
    {
        if (btn == null || modBtn == null) return false;
        if (string.IsNullOrEmpty(modBtn.filePath)) return false;
        if (!File.Exists(modBtn.filePath)) return false;
        return true;
    }

    bool IsThumbnailMissingOrTooBig()
    {
        string p = modBtn != null ? modBtn.thumbnailPath : null;
        if (string.IsNullOrEmpty(p) || !File.Exists(p)) return true;
        var fi = new FileInfo(p);
        if (fi.Length > MaxBytes) return true;
        string ext = Path.GetExtension(p).ToLowerInvariant();
        return !(ext == ".png" || ext == ".jpg" || ext == ".jpeg");
    }

    bool PickThumbnail(out string savedPath)
    {
        savedPath = null;

        var paths = StandaloneFileBrowser.OpenFilePanel(
            "Select Thumbnail (PNG/JPG, Max 2MB)",
            MateEnginePaths.DefaultImportDirectory,
            new[] { new ExtensionFilter("Image", "png", "jpg", "jpeg") },
            false
        );
        if (paths == null || paths.Length == 0) return false;

        string src = paths[0];
        if (!File.Exists(src)) return false;

        var fi = new FileInfo(src);
        if (fi.Length > MaxBytes) { SetError("Image too big (>2MB)"); return false; }

        string ext = Path.GetExtension(src).ToLowerInvariant();
        if (ext != ".png" && ext != ".jpg" && ext != ".jpeg") { SetError("Unsupported format"); return false; }

        string thumbs = MateEnginePaths.ThumbnailsDir;
        Directory.CreateDirectory(thumbs);

        string baseName = Path.GetFileNameWithoutExtension(modBtn.filePath);
        string dest = Path.Combine(thumbs, baseName + "_thumb.png");
        try
        {
            File.Copy(src, dest, true);
        }
        catch
        {
            SetError("Copy failed");
            return false;
        }

        savedPath = dest;
        return true;
    }

    void TryLoadPreviewFromPath(string path)
    {
        if (previewImage == null) return;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.LoadImage(bytes);
            previewImage.texture = tex;
        }
        catch { }
    }

    void EnsureLocalThumbLink()
    {
        if (modBtn == null || string.IsNullOrEmpty(modBtn.filePath)) return;
        if (!string.IsNullOrEmpty(modBtn.thumbnailPath) && File.Exists(modBtn.thumbnailPath)) return;
        string def = GetDefaultThumbPath();
        if (File.Exists(def)) modBtn.thumbnailPath = def;
    }

    string GetDefaultThumbPath()
    {
        string name = string.IsNullOrEmpty(modBtn?.filePath) ? "" : Path.GetFileNameWithoutExtension(modBtn.filePath);
        return Path.Combine(MateEnginePaths.ThumbnailsDir, name + "_thumb.png");
    }

    void SetInteractable(bool v)
    {
        if (btn != null) btn.interactable = v;
    }

    void CancelHold()
    {
        if (holdRoutine != null)
        {
            StopCoroutine(holdRoutine);
            holdRoutine = null;
        }
    }

    ulong ResolveWorkshopIdForPath(string localPath)
    {
        try
        {
            if (!SteamManager.Initialized) return 0UL;
            if (string.IsNullOrEmpty(localPath)) return 0UL;
            uint count = SteamUGC.GetNumSubscribedItems();
            if (count == 0) return 0UL;

            var ids = new PublishedFileId_t[count];
            SteamUGC.GetSubscribedItems(ids, count);

            string targetName = Path.GetFileName(localPath);
            for (int i = 0; i < ids.Length; i++)
            {
                if (!SteamUGC.GetItemInstallInfo(ids[i], out ulong _, out string installPath, 1024, out uint _)) continue;
                if (string.IsNullOrEmpty(installPath) || !Directory.Exists(installPath)) continue;
                var top = Directory.GetFiles(installPath, "*", SearchOption.TopDirectoryOnly);
                for (int f = 0; f < top.Length; f++)
                    if (string.Equals(Path.GetFileName(top[f]), targetName, System.StringComparison.OrdinalIgnoreCase))
                        return ids[i].m_PublishedFileId;
            }
        }
        catch { }
        return 0UL;
    }
}
