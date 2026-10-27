using System;
using System.IO;
using UnityEngine;

/// <summary>Explicit analytical test scene, never a Hunnedalen or LS-DYNA result.</summary>
public sealed class LsDynaVerificationDemo : MonoBehaviour
{
    private void Start()
    {
        try
        {
            var player = gameObject.AddComponent<LsDynaPlayback>();
            player.LoadDataset(Path.Combine(Application.streamingAssetsPath, "LSDyna/Verification"), true);
            gameObject.AddComponent<LsDynaPlaybackControls>().Bind(player);
            player.Play();
        }
        catch (Exception error) { Debug.LogError("Analytical verification unavailable: " + error.Message, this); }
    }
}
