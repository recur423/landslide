using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public sealed class LsDynaSetupWindow : EditorWindow
{
    private const string SettingsPath = "Assets/Resources/LSDynaPlaybackSettings.json";
    public const string VerificationScene = "Assets/Breach/Scenes/LSDynaVerification.unity";
    private LsDynaProjectSettings settings;
    private string status;
    private Vector2 scroll;

    [MenuItem("Landslide/LS-DYNA/Open setup")]
    public static void Open() => GetWindow<LsDynaSetupWindow>("LS-DYNA data setup");

    private void OnEnable() { settings = LsDynaProjectSettings.Load(); }

    private void OnGUI()
    {
        if (settings == null) settings = LsDynaProjectSettings.Load();
        scroll = EditorGUILayout.BeginScrollView(scroll);
        EditorGUILayout.LabelField("LS-DYNA / Hunnedalen", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("目前没有现场降雨、地层、孔压或真实 LS-DYNA 滑坡结果。可先用场地准备工具核查地形、分层和降雨；其报告不代表已完成渗流或失稳求解。", MessageType.Info);
        settings.mode = EditorGUILayout.Popup("播放模式", settings.UsesLsDyna ? 0 : 1,
            new[] { "LS-DYNA 数据（严格）", "原 RAMMS + 程序后阶段（非 LS-DYNA）" }) == 0 ? "LsDyna" : "RammsLegacy";
        settings.datasetDirectory = EditorGUILayout.TextField("StreamingAssets 内目录", settings.datasetDirectory);
        settings.autoPlay = EditorGUILayout.Toggle("自动播放", settings.autoPlay);
        settings.playbackSpeed = EditorGUILayout.FloatField("播放倍速", settings.playbackSpeed);
        settings.showControls = EditorGUILayout.Toggle("桌面时间轴与状态", settings.showControls);
        settings.allowAnalyticVerification = EditorGUILayout.Toggle("允许解析测试数据", settings.allowAnalyticVerification);
        if (settings.allowAnalyticVerification)
            EditorGUILayout.HelpBox("解析数据不代表 LS-DYNA 求解，也不代表 Hunnedalen 实际滑坡。正式结果应关闭此选项。", MessageType.Warning);
        EditorGUILayout.HelpBox("坐标变换、单位及地形配准在转换时显式指定；回放不会贴地或补加重力。固定拓扑数据不包含侵蚀、删单元或新裂面的重建。", MessageType.None);
        if (GUILayout.Button("保存配置（重新进入 Play 后生效）")) Run(Save);
        if (GUILayout.Button("检查当前数据")) Run(() =>
        {
            var data = LsDynaDataset.Load(settings.ResolveDirectory(), settings.allowAnalyticVerification);
            var site = data.Manifest.siteCaseReport;
            string siteStatus = site == null ? "未附场地准备报告。" :
                $"场地案例 {site.caseId}：已准备但未求解，缺少 {site.evidenceMissingCount} 类证据。";
            status = $"格式有效：{data.NodeCount} 节点，{data.FrameCount} 帧；{data.StartTime:G8}–{data.EndTime:G8} s。\n" +
                     data.Manifest.sourceDescription + "\n" + siteStatus + " 格式通过不等于模型物理验证通过。";
        });
        if (GUILayout.Button("导入已转换的数据目录…")) Run(Import);
        EditorGUILayout.Space();
        if (GUILayout.Button("打开独立解析验证场景"))
        {
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) Run(() =>
            {
                if (!File.Exists(VerificationScene)) CreateVerificationScene();
                EditorSceneManager.OpenScene(VerificationScene);
                status = "按 Play 查看解析滑块。蓝到红表示导出速度；到末帧停止。";
            });
        }
        if (GUILayout.Button("查看中文接入说明")) Application.OpenURL(new Uri(Path.GetFullPath("docs/LSDyna.md")).AbsoluteUri);
        if (GUILayout.Button("查看降雨场地建模流程")) Application.OpenURL(new Uri(Path.GetFullPath("docs/RainfallCasePreparation.md")).AbsoluteUri);
        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.Info);
        EditorGUILayout.EndScrollView();
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception error) { status = error.Message; Debug.LogWarning("LS-DYNA setup: " + error.Message); }
    }

    private void Save()
    {
        if (settings.playbackSpeed <= 0 || float.IsNaN(settings.playbackSpeed) || float.IsInfinity(settings.playbackSpeed))
            throw new InvalidDataException("播放倍速必须是正有限数。");
        settings.ResolveDirectory();
        File.WriteAllText(SettingsPath, JsonUtility.ToJson(settings, true));
        AssetDatabase.ImportAsset(SettingsPath);
        status = "配置已保存。";
    }

    private void Import()
    {
        string source = EditorUtility.OpenFolderPanel("选择含 dataset.json 的转换输出目录", "", "");
        if (string.IsNullOrEmpty(source)) return;
        var data = LsDynaDataset.Load(source, settings.allowAnalyticVerification);
        string relative = "LSDyna/Imported-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
        string destination = Path.Combine(Application.streamingAssetsPath, relative);
        Directory.CreateDirectory(destination);
        foreach (string file in data.Manifest.frameFiles)
        {
            string target = Path.Combine(destination, file);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(Path.Combine(source, file), target);
        }
        File.Copy(Path.Combine(source, "dataset.json"), Path.Combine(destination, "dataset.json"));
        if (File.Exists(Path.Combine(source, "validation.json")))
            File.Copy(Path.Combine(source, "validation.json"), Path.Combine(destination, "validation.json"));
        if (data.Manifest.siteCaseReport != null)
            File.Copy(Path.Combine(source, data.Manifest.siteCaseReport.file),
                      Path.Combine(destination, data.Manifest.siteCaseReport.file));
        settings.datasetDirectory = relative;
        settings.mode = "LsDyna";
        Save();
        AssetDatabase.Refresh();
        status = "已导入并选中：" + relative + "。请核对 validation.json 及模型物理验证后播放。";
    }

    // Batch-safe creation of a separate test scene. Does not modify the original mountain scenes.
    public static void CreateVerificationScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var camera = new GameObject("Verification camera").AddComponent<Camera>();
        camera.tag = "MainCamera";
        camera.transform.position = new Vector3(17f, 15f, -32f);
        camera.transform.LookAt(new Vector3(13f, 3f, 0f));
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.08f, 0.11f, 0.16f);
        camera.fieldOfView = 48f;
        camera.farClipPlane = 200f;
        camera.gameObject.AddComponent<AudioListener>();
        var light = new GameObject("Verification sun").AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.2f;
        light.transform.rotation = Quaternion.Euler(40f, -30f, 0f);
        RenderSettings.ambientLight = new Color(0.45f, 0.45f, 0.45f);
        RenderSettings.fog = false;
        var slope = GameObject.CreatePrimitive(PrimitiveType.Cube);
        slope.name = "Analytical 30 degree plane (visual reference only)";
        slope.transform.rotation = Quaternion.Euler(0f, 0f, -30f);
        slope.transform.position = new Vector3(14f, 10f - 14f * Mathf.Tan(Mathf.PI / 6f), 0f) - slope.transform.up * 0.1f;
        slope.transform.localScale = new Vector3(40f, 0.2f, 7f);
        DestroyImmediate(slope.GetComponent<Collider>());
        slope.GetComponent<Renderer>().sharedMaterial = Resources.Load<Material>("LandslideLateStage/Earth");
        new GameObject("ANALYTIC ONLY - gravity and Coulomb friction benchmark").AddComponent<LsDynaVerificationDemo>();
        EditorSceneManager.SaveScene(scene, VerificationScene);
        AssetDatabase.Refresh();
    }
}
