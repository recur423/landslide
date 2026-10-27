using System;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Project-level selection. Missing/invalid scientific data never enables legacy motion.</summary>
[Serializable]
public sealed class LsDynaProjectSettings
{
    public string mode = "LsDyna";
    public string datasetDirectory = "LSDyna/Hunnedalen";
    public bool allowAnalyticVerification = false;
    public bool autoPlay = true;
    public float playbackSpeed = 1f;
    public bool showControls = true;
    [NonSerialized] public string error;

    public bool UsesLsDyna => mode != "RammsLegacy";

    public static bool IsTargetScene(string name)
        => name == "Hunnedalen_DSK" || name == "Hunnedalen_VR";

    public static LsDynaProjectSettings Load()
    {
        var settings = new LsDynaProjectSettings();
        try
        {
            var asset = Resources.Load<TextAsset>("LSDynaPlaybackSettings");
            if (asset != null) settings = JsonUtility.FromJson<LsDynaProjectSettings>(asset.text);
            if (settings == null) throw new InvalidDataException("Empty playback settings.");
            if (settings.mode != "LsDyna" && settings.mode != "RammsLegacy")
                throw new InvalidDataException("mode must be LsDyna or RammsLegacy.");
            if (float.IsNaN(settings.playbackSpeed) || float.IsInfinity(settings.playbackSpeed) || settings.playbackSpeed <= 0f)
                throw new InvalidDataException("playbackSpeed must be finite and positive.");
            settings.ResolveDirectory();
            return settings;
        }
        catch (Exception exception)
        {
            return new LsDynaProjectSettings { error = exception.Message };
        }
    }

    public string ResolveDirectory()
    {
        if (string.IsNullOrWhiteSpace(datasetDirectory) || Path.IsPathRooted(datasetDirectory) || datasetDirectory.Contains(":"))
            throw new InvalidDataException("datasetDirectory must be relative to StreamingAssets.");
        var root = Path.GetFullPath(Application.streamingAssetsPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(root, datasetDirectory));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Dataset must stay inside StreamingAssets.");
        return path;
    }

    public static bool ScientificModeFor(Scene scene)
        => IsTargetScene(scene.name) && Load().UsesLsDyna;

    public static void SuppressLegacyMotion(Scene scene)
    {
        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour is LandslideEvent landslide) { landslide.Cancel(); landslide.enabled = false; }
                else if (behaviour is EarlyWarning warning)
                {
                    warning.enabled = false;
                    if (warning.UiObject != null) warning.UiObject.SetActive(false);
                    if (warning.ScreenInfo != null)
                        foreach (var screen in warning.ScreenInfo) if (screen != null) screen.SetActive(false);
                }
                else if (behaviour is PlayAudio audio)
                {
                    audio.enabled = false;
                    var source = audio.GetComponent<AudioSource>();
                    if (source != null) source.Stop();
                }
                else if (behaviour is LandslideSlopeMass || behaviour is LandslideTerrainScar ||
                         behaviour is erosionScript || behaviour is TerrainEdit || behaviour is RiverSurfaceRaise ||
                         behaviour is DestructibleHouse || behaviour is Flickering)
                    behaviour.enabled = false;
            }
        }
    }
}
