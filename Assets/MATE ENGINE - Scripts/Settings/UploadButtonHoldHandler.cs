using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.EventSystems;
using System.Collections;
using System.IO;
using SFB;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.Localization;

public class UploadButtonHoldHandler : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
{
    [HideInInspector] public AvatarLibraryMenu.AvatarEntry entry;

    [Header("UI References")]
    public Slider progressSlider;
    public TMP_Text labelText;
    public TMP_Text errorText;

    [Header("Audio")]
    public AudioSource audioSource;
    public AudioClip tickSound;
    public AudioClip completeSound;

    [SerializeField] private string fallbackUpload = "Upload";
    [SerializeField] private string fallbackUpdate = "Update";
    [SerializeField] private string fallbackPngMissing = "PNG Missing";

    private Coroutine holdRoutine;
    private bool isHolding = false;

    private LocalizedString currentLocString;

    private void OnEnable()
    {
        CancelHoldIfRunning();
        SetInteractable(true);
        UpdateButtonLabel();
    }

    private void OnDisable()
    {
        CancelHoldIfRunning();
        SetInteractable(true);
        UpdateButtonLabel();
        if (currentLocString != null)
        {
            currentLocString.StringChanged -= OnLocalizedChanged;
            currentLocString = null;
        }
    }

    private void Start()
    {
        if (entry != null && !entry.isOwner)
        {
            gameObject.SetActive(false);
            return;
        }
        UpdateButtonLabel();
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        if (IsThumbnailMissing() || IsThumbnailTooBig())
        {
            string[] paths = StandaloneFileBrowser.OpenFilePanel("Select PNG Thumbnail (Max 700KB)", MateEnginePaths.DefaultImportDirectory, new[] {
                new ExtensionFilter("Image", "png")
            }, false);

            if (paths.Length == 0 || !File.Exists(paths[0])) return;

            FileInfo fi = new FileInfo(paths[0]);
            if (fi.Length > 700 * 1024)
            {
                if (errorText != null)
                {
                    SetErrorByKey("PNG_TOO_BIG", "PNG too big");
                }
                return;
            }

            string thumbnailsFolder = MateEnginePaths.ThumbnailsDir;
            if (!Directory.Exists(thumbnailsFolder))
                Directory.CreateDirectory(thumbnailsFolder);

            string safeName = Path.GetFileNameWithoutExtension(entry.filePath) + "_thumb.png";
            string destinationPath = Path.Combine(thumbnailsFolder, safeName);
            File.Copy(paths[0], destinationPath, true);
            entry.thumbnailPath = destinationPath;

            string avatarsJsonPath = MateEnginePaths.AvatarsJsonPath;
            if (File.Exists(avatarsJsonPath))
            {
                var json = File.ReadAllText(avatarsJsonPath);
                var list = JsonConvert.DeserializeObject<List<AvatarLibraryMenu.AvatarEntry>>(json);
                var match = list.FirstOrDefault(e => e.filePath == entry.filePath);
                if (match != null)
                {
                    match.thumbnailPath = destinationPath;
                    File.WriteAllText(avatarsJsonPath, JsonConvert.SerializeObject(list, Formatting.Indented));
                }
            }

            var menu = GameObject.FindFirstObjectByType<AvatarLibraryMenu>();
            if (menu != null) menu.ReloadAvatars();

            if (errorText != null) errorText.text = "";
            UpdateButtonLabel();
            return;
        }

        if (holdRoutine == null)
        {
            isHolding = true;
            holdRoutine = StartCoroutine(HoldToUpload());
        }
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        isHolding = false;
    }

    private bool IsThumbnailMissing()
    {
        return string.IsNullOrEmpty(entry.thumbnailPath) || !File.Exists(entry.thumbnailPath);
    }

    private bool IsThumbnailTooBig()
    {
        if (string.IsNullOrEmpty(entry.thumbnailPath) || !File.Exists(entry.thumbnailPath))
            return false;
        FileInfo fi = new FileInfo(entry.thumbnailPath);
        return fi.Length > 700 * 1024;
    }

    private void SetErrorByKey(string key, string fallback)
    {
        if (errorText == null) return;
        errorText.text = fallback;
        var ls = new LocalizedString("Languages (UI)", key);
        ls.StringChanged += (val) => { if (errorText != null) errorText.text = val; };
    }

    private void SetLabelImmediate(string text)
    {
        if (labelText != null) labelText.text = text;
    }

    private void OnLocalizedChanged(string val)
    {
        if (labelText != null) labelText.text = val;
    }

    private void SetLabelByKey(string key, string fallback)
    {
        if (labelText == null) return;
        if (currentLocString != null) currentLocString.StringChanged -= OnLocalizedChanged;

        labelText.text = fallback;
        currentLocString = new LocalizedString("Languages (UI)", key);
        currentLocString.StringChanged += OnLocalizedChanged;
    }

    private void UpdateButtonLabel()
    {
        if (labelText == null) return;
        if (IsThumbnailMissing() || IsThumbnailTooBig())
        {
            SetLabelByKey("PNG_MISSING", fallbackPngMissing);
            return;
        }

        if (entry != null && entry.steamFileId > 0)
            SetLabelByKey("UPDATE", fallbackUpdate);
        else
            SetLabelByKey("UPLOAD", fallbackUpload);
    }

    private IEnumerator HoldToUpload()
    {
        float duration = 5f;
        float timeHeld = 0f;
        int lastSecond = -1;
        float pitch = 1f;
        bool completed = false;
        SetLabelImmediate(Mathf.CeilToInt(duration).ToString());
        SetInteractable(false);

        while (isHolding && timeHeld < duration)
        {
            timeHeld += Time.deltaTime;
            int currentSecond = Mathf.CeilToInt(duration - timeHeld);

            if (currentSecond != lastSecond)
            {
                lastSecond = currentSecond;
                SetLabelImmediate(currentSecond.ToString());

                if (audioSource != null && tickSound != null)
                {
                    audioSource.pitch = pitch;
                    audioSource.PlayOneShot(tickSound);
                    pitch += 0.1f;
                }
            }
            yield return null;
        }

        if (timeHeld >= duration && isHolding)
        {
            completed = true;
            SetLabelImmediate("0");

            if (audioSource != null && completeSound != null)
            {
                audioSource.pitch = 1f;
                audioSource.PlayOneShot(completeSound);
            }

            yield return new WaitForSeconds(0.5f);

            if (SteamWorkshopHandler.Instance != null)
                SteamWorkshopHandler.Instance.UploadToWorkshop(entry, progressSlider);
            SetLabelImmediate("Uploaded");
            yield return StartCoroutine(WaitForSteamIdAndRelabel(entry.filePath, 20f));
        }

        if (!completed)
            UpdateButtonLabel(); 
        SetInteractable(true);
        holdRoutine = null;
    }

    private IEnumerator WaitForSteamIdAndRelabel(string filePath, float timeoutSeconds)
    {
        float t = 0f;
        string avatarsJsonPath = MateEnginePaths.AvatarsJsonPath;

        while (t < timeoutSeconds)
        {
            try
            {
                if (File.Exists(avatarsJsonPath))
                {
                    var json = File.ReadAllText(avatarsJsonPath);
                    var list = JsonConvert.DeserializeObject<List<AvatarLibraryMenu.AvatarEntry>>(json);
                    var match = list?.FirstOrDefault(e => e.filePath == filePath);

                    if (match != null && match.steamFileId != 0)
                    {
                        entry.steamFileId = match.steamFileId;
                        entry.isSteamWorkshop = match.isSteamWorkshop;
                        SetLabelByKey("UPDATE", fallbackUpdate);
                        yield break;
                    }
                }
            }
            catch { /* retry */ }

            yield return new WaitForSeconds(0.5f);
            t += 0.5f;
        }
        UpdateButtonLabel();
    }

    private void CancelHoldIfRunning()
    {
        isHolding = false;
        if (holdRoutine != null)
        {
            StopCoroutine(holdRoutine);
            holdRoutine = null;
        }
    }

    private void SetInteractable(bool value)
    {
        var btn = GetComponent<Button>();
        if (btn != null) btn.interactable = value;
    }
}
