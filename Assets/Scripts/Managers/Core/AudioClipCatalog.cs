using System;
using System.Collections.Generic;

using UnityEngine;

[CreateAssetMenu(fileName = "AudioClipCatalog", menuName = "Audio/Audio Clip Catalog")]
public class AudioClipCatalog : ScriptableObject
{
    [Serializable]
    private struct Entry
    {
#pragma warning disable CS0649 // Unity가 AudioClipCatalog 에셋에서 직렬화된 값을 채운다.
        public AudioClipId Id;
        public AudioClip Clip;
#pragma warning restore CS0649
    }

    [SerializeField] private List<Entry> _clips = new();
    private Dictionary<AudioClipId, AudioClip> _clipById;

    /// <summary>Returns the audio clip registered for id, or null when no clip is registered.</summary>
    public AudioClip GetClip(AudioClipId id)
    {
        if (_clipById == null)
        {
            _clipById = new Dictionary<AudioClipId, AudioClip>();
            foreach (Entry entry in _clips)
                _clipById[entry.Id] = entry.Clip;
        }

        return _clipById.TryGetValue(id, out AudioClip clip) ? clip : null;
    }

    private void OnEnable()
    {
        _clipById = null;
    }

    private void OnValidate()
    {
        _clipById = null;
    }
}
