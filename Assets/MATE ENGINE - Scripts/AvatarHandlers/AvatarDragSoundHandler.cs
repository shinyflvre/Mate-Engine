using System.Collections.Generic;
using UnityEngine;

public class AvatarDragSoundHandler : MonoBehaviour
{
    [Header("Sound Settings")]
    public AudioSource dragStartSound, dragStopSound;
    public List<AudioClip> dragStartSoundList, dragStopSoundList;
    [Range(0, 100)] public float maxHighPitchPercent = 10f, maxLowPitchPercent = 10f;

    private bool wasDragging;
    private AvatarAnimatorController avatarController;

    void Start()
    {
        avatarController = GetComponent<AvatarAnimatorController>();
        if (!avatarController) Debug.LogError("AvatarAnimatorController script not found on this GameObject.");
    }

    void Update()
    {
        if (!avatarController) return;
        bool dragging = avatarController.isDragging;
        if (dragging != wasDragging)
        {
            if (dragging) PlaySound(dragStartSoundList, dragStartSound);
            else PlaySound(dragStopSoundList, dragStopSound);
            wasDragging = dragging;
        }
    }

    void PlaySound(List<AudioClip> audioClips, AudioSource audio)
    {
        if (audioClips.Count <= 0) return;
        float low = 1f - maxLowPitchPercent / 100f, high = 1f + maxHighPitchPercent / 100f;
        audio.clip = audioClips[Random.Range(0, audioClips.Count)];
        audio.pitch = Random.Range(low, high);
        audio.Play();
    }
}
