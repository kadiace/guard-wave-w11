using UnityEngine;

public class SoundManager
{
    private AudioClipCatalog _catalog;

    /// <summary>Loads the audio catalog from Resources for subsequent playback.</summary>
    public void Init()
    {
        _catalog = Managers.Resource.Load<AudioClipCatalog>("Datas/AudioClipCatalog");
    }

    /// <summary>Plays the clip identified by clipId on the supplied audio source.</summary>
    public void PlaySound(AudioSource audioSource, AudioClipId clipId)
    {
        AudioClip clip = _catalog.GetClip(clipId);
        if (clip == null)
        {
            Debug.LogError($"Fail to load audio clip: {clipId}");
            return;
        }

        audioSource.clip = clip;
        audioSource.Play();
    }

    /// <summary>Stops playback on the supplied audio source.</summary>
    public void StopSound(AudioSource audioSource)
    {
        audioSource.Stop();
    }
}
