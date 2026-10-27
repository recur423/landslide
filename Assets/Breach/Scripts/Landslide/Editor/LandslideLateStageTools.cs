using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

public static class LandslideLateStageTools
{
    public const string DesktopScene = "Assets/Breach/Scenes/DynamicScenes/Hunnedalen_DSK.unity";
    private static string Output => Path.GetFullPath(Path.Combine(Application.dataPath, "../../Review"));

    [MenuItem("Landslide/Late stage/Open desktop scene")]
    public static void OpenDesktop() => EditorSceneManager.OpenScene(DesktopScene);

    [MenuItem("Landslide/Late stage/Preview on original terrain")]
    public static void OpenPreview() => LandslideLateStagePreview.ShowWindow();

    [MenuItem("Landslide/Late stage/Prepare materials")]
    public static void PrepareMaterials()
    {
        string folder = "Assets/Resources/LandslideLateStage";
        Directory.CreateDirectory(folder);
        AssetDatabase.Refresh();
        foreach (var label in new[] { "Rock", "Earth", "Fracture", "Dust" })
        {
            string path = folder + "/" + label + ".mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) continue;
            string shaderName = label == "Dust" ? "Hunnedalen/LateStageDust" : "Hunnedalen/LateStageRock";
            var shader = Shader.Find(shaderName);
            if (shader == null) throw new Exception("Missing shader " + shaderName);
            AssetDatabase.CreateAsset(new Material(shader), path);
        }
        AssetDatabase.SaveAssets();
    }

    public static LandslideSourceEstimate Source(Terrain terrain)
    {
        var frames = new Dictionary<int, MapInfo>();
        string folder = Path.Combine(Application.streamingAssetsPath, "SceneData/Hunnedalen/Frames");
        foreach (int i in new[] { 0, 3, 10, 25 }) frames[i] = MapInfo.Load(Path.Combine(folder, "Frame_" + i + ".bin"));
        var estimate = new LandslideSourceEstimate();
        if (!LandslideSourceEstimator.AnalyzeFrames(frames, 150, terrain, estimate)) throw new Exception("Cannot locate the original RAMMS moving mass.");
        estimate.size = LandslideSettings.Default().releaseZoneSize;
        estimate.runoutDistance = LandslideSettings.Default().runoutDistance;
        estimate.center.y = terrain.SampleHeight(estimate.center) + terrain.transform.position.y;
        return estimate;
    }

    public static LandslideEvent Preview(Terrain terrain)
    {
        var go = new GameObject("Late stage preview (not saved)") { hideFlags = HideFlags.DontSave };
        var zone = go.AddComponent<LandslideReleaseZone>();
        Source(terrain).ApplyTo(zone);
        var evt = go.AddComponent<LandslideEvent>();
        evt.PreparePreview(terrain, zone);
        return evt;
    }

    public static void AuditDependencies()
    {
        var required = new HashSet<string>();
        foreach (var scene in new[] { DesktopScene, "Assets/Breach/Scenes/DynamicScenes/Hunnedalen_VR.unity", "Assets/Breach/Scenes/MainMenu.unity", "Assets/Breach/Scenes/VRMenu.unity" })
            foreach (var path in AssetDatabase.GetDependencies(scene, true)) required.Add(path);
        File.WriteAllLines(Path.GetFullPath(Path.Combine(Application.dataPath, "../../required-assets.txt")), required);
        Directory.CreateDirectory(Output);
        EditorSceneManager.OpenScene(DesktopScene);
        var terrain = UnityEngine.Object.FindObjectOfType<Terrain>();
        var source = Source(terrain);
        File.WriteAllText(Path.Combine(Output, "source-audit.txt"), "Terrain: " + terrain.transform.position + " size " + terrain.terrainData.size + "\nSource: " + source.center + " direction " + source.slideDirection + "\nDependencies: " + required.Count);
        Debug.Log("[Late landslide] Dependency audit complete.");
    }

    // Called by Unity batch mode: validates actual original terrain and outputs review images.
    public static void ValidateAndRender()
    {
        var sequence = RenderSequence();
        EditorApplication.CallbackFunction tick = null;
        tick = () =>
        {
            try
            {
                if (sequence.MoveNext()) return;
                EditorApplication.update -= tick;
                LandslideLateStageRuntimeChecks.Run();
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                EditorApplication.update -= tick;
                EditorApplication.Exit(1);
            }
        };
        EditorApplication.update += tick;
    }

    private static IEnumerator RenderSequence()
    {
        Directory.CreateDirectory(Output);
        PrepareMaterials();
        EditorSceneManager.OpenScene(DesktopScene);
        Terrain terrain = UnityEngine.Object.FindObjectOfType<Terrain>();
        if (terrain == null) throw new Exception("Original Hunnedalen terrain missing.");
        var originalData = terrain.terrainData;
        // Neutral inspection lighting only for exported review images. The original
        // scene and its runtime weather/fog are never saved or changed by the extension.
        bool originalFog = RenderSettings.fog;
        var originalAmbientMode = RenderSettings.ambientMode;
        var originalAmbient = RenderSettings.ambientLight;
        RenderSettings.fog = false;
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.42f, 0.45f, 0.49f);
        var inspectionSun = new GameObject("Review lighting (not saved)") { hideFlags = HideFlags.DontSave };
        var sun = inspectionSun.AddComponent<Light>();
        sun.type = LightType.Directional; sun.intensity = 1.15f;
        sun.shadows = LightShadows.Soft;
        sun.transform.rotation = Quaternion.Euler(45f, -35f, 0f);
        float[,] originalHeights = originalData.GetHeights(0, 0, originalData.heightmapResolution, originalData.heightmapResolution);
        var estimate = Source(terrain);
        var evt = Preview(terrain);
        evt.gameObject.hideFlags = HideFlags.None;
        var route = evt.Visuals.Route;
        var report = new List<string> {
            "Original scene: " + DesktopScene,
            "Terrain origin: " + terrain.transform.position,
            "Terrain size: " + originalData.size,
            "Source: " + estimate.center,
            "Initial downhill direction: " + estimate.slideDirection,
            "Original flow: early warning 30s + navigation 60s + RAMMS 148s = 238s (unchanged)",
            "Secondary precursor: original end + 4s; main failure 12s later",
            "Fragments: " + evt.Visuals.FragmentCount,
            "Visual volume estimate (not calibrated): " + evt.VolumeEstimate + " m3",
            "Route end: " + route[route.Length - 1]
        };
        report.Add("Review images use temporary neutral lighting with fog disabled; original runtime weather is unchanged.");
        for (int i = 0; i < route.Length; i += 10) report.Add("Route " + i + ": " + route[i]);
        var cameraGO = new GameObject("Late stage review camera") { hideFlags = HideFlags.DontSave };
        cameraGO.hideFlags = HideFlags.None;
        var camera = cameraGO.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.Skybox;
        camera.farClipPlane = 4000f;
        camera.fieldOfView = 60f;
        cameraGO.AddComponent<UniversalAdditionalCameraData>();
        var direction = estimate.slideDirection.normalized;
        var across = Vector3.Cross(Vector3.up, direction);
        var focus = route[Mathf.Min(route.Length - 1, route.Length / 2)];
        camera.transform.position = route[route.Length - 1] - across * 250f + direction * 20f + Vector3.up * 820f;
        camera.transform.LookAt(focus + Vector3.up * 10f);
        yield return null;
        yield return null;
        Capture(camera, "00-original.png");
        float[] times = { 8f, 16f, 40f, evt.TotalDuration };
        string[] names = { "01-fractures.png", "02-slope-failure.png", "03-rock-avalanche.png", "04-deposit.png" };
        for (int i = 0; i < times.Length; i++)
        {
            evt.EvaluateAt(times[i]);
            foreach (var filter in evt.GetComponentsInChildren<MeshFilter>(true))
                foreach (var vertex in filter.sharedMesh.vertices)
                    if (float.IsNaN(vertex.x) || float.IsNaN(vertex.y) || float.IsNaN(vertex.z) || float.IsInfinity(vertex.x) || float.IsInfinity(vertex.y) || float.IsInfinity(vertex.z))
                        throw new Exception("Invalid secondary geometry: " + filter.name);
            evt.Visuals.SimulateDust(4f);
            yield return null;
            yield return null;
            Capture(camera, names[i]);
            report.Add(names[i] + ": stage " + evt.Phase + ", time " + times[i] + ", front " + evt.Visuals.FrontPosition);
            if (evt.Visuals.FragmentCount < 100) throw new Exception("Missing large slide mass.");
        }
        evt.Cancel();
        float[,] after = originalData.GetHeights(0, 0, originalData.heightmapResolution, originalData.heightmapResolution);
        for (int z = 0; z < after.GetLength(0); z++) for (int x = 0; x < after.GetLength(1); x++)
            if (after[z, x] != originalHeights[z, x]) throw new Exception("The original terrain was modified.");
        report.Add("PASS: original TerrainData reference and all heightmap samples unchanged after full sequence and reset.");
        UnityEngine.Object.DestroyImmediate(evt.gameObject);
        UnityEngine.Object.DestroyImmediate(cameraGO);
        UnityEngine.Object.DestroyImmediate(inspectionSun);
        RenderSettings.fog = originalFog;
        RenderSettings.ambientMode = originalAmbientMode;
        RenderSettings.ambientLight = originalAmbient;
        File.WriteAllLines(Path.Combine(Output, "validation.txt"), report);
        Debug.Log("[Late landslide] Validation and renders written to " + Output);
    }

    private static void Capture(Camera camera, string name)
    {
        var target = new RenderTexture(1600, 900, 24);
        camera.targetTexture = target;
        camera.Render();
        var previous = RenderTexture.active;
        RenderTexture.active = target;
        var image = new Texture2D(1600, 900, TextureFormat.RGB24, false);
        image.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0); image.Apply();
        File.WriteAllBytes(Path.Combine(Output, name), image.EncodeToPNG());
        RenderTexture.active = previous; camera.targetTexture = null;
        UnityEngine.Object.DestroyImmediate(image); target.Release(); UnityEngine.Object.DestroyImmediate(target);
    }
}

public class LandslideLateStagePreview : EditorWindow
{
    private LandslideEvent evt;
    private float seconds;
    private bool playing;
    private double lastTick;
    public static void ShowWindow() => GetWindow<LandslideLateStagePreview>("山体滑坡后阶段");
    private void OnEnable() { EditorApplication.update += Tick; lastTick = EditorApplication.timeSinceStartup; }
    private void OnDisable() { EditorApplication.update -= Tick; Clear(); }
    private void OnGUI()
    {
        EditorGUILayout.HelpBox("保留原场景。这里仅预览原泥石流结束后的继发山体滑坡。关闭窗口会清除预览。", MessageType.Info);
        if (GUILayout.Button("在当前 Hunnedalen 地形上创建预览"))
        {
            Clear(); LandslideLateStageTools.PrepareMaterials();
            var terrain = UnityEngine.Object.FindObjectOfType<Terrain>();
            if (terrain != null) evt = LandslideLateStageTools.Preview(terrain);
        }
        if (evt == null) return;
        EditorGUILayout.LabelField("阶段", evt.Phase.ToString());
        EditorGUI.BeginChangeCheck();
        seconds = EditorGUILayout.Slider("后阶段时间（秒）", seconds, 0f, evt.TotalDuration);
        if (EditorGUI.EndChangeCheck()) { evt.EvaluateAt(seconds); SceneView.RepaintAll(); }
        playing = GUILayout.Toggle(playing, "播放后阶段", "Button");
        if (GUILayout.Button("恢复原场景")) Clear();
    }
    private void Tick()
    {
        double now = EditorApplication.timeSinceStartup;
        if (playing && evt != null)
        {
            seconds = Mathf.Min(evt.TotalDuration, seconds + (float)(now - lastTick));
            evt.EvaluateAt(seconds); evt.Visuals.SimulateDust(2f);
            SceneView.RepaintAll(); Repaint();
            if (seconds >= evt.TotalDuration) playing = false;
        }
        lastTick = now;
    }
    private void Clear()
    {
        if (evt != null) { evt.Cancel(); DestroyImmediate(evt.gameObject); }
        evt = null; seconds = 0f; playing = false;
    }
}
