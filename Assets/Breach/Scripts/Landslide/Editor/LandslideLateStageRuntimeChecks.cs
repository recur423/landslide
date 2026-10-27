using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Checks the integration in the actual desktop scene; no scene is saved.</summary>
[InitializeOnLoad]
public static class LandslideLateStageRuntimeChecks
{
    private static readonly List<string> report = new List<string>();
    private static double started, checkpoint;
    private static int step;
    private static float pausedTime;
    private static Terrain terrain;
    private static TerrainData data;
    private static float[,] heights;
    private static SimulationManager simulation;
    private static LandslideEvent evt;
    private static readonly FieldInfo End = typeof(SimulationManager).GetField("flowTimeEnd", BindingFlags.Instance | BindingFlags.NonPublic);
    private static string Output => Path.GetFullPath(Path.Combine(Application.dataPath, "../../Review/runtime-validation.txt"));
    static LandslideLateStageRuntimeChecks() { EditorApplication.playModeStateChanged += StateChanged; }
    public static void Run()
    {
        EditorSceneManager.OpenScene(LandslideLateStageTools.DesktopScene);
        SessionState.SetBool("ValidateLateStage", true);
        EditorApplication.isPlaying = true;
    }
    private static void StateChanged(PlayModeStateChange state)
    {
        if (!SessionState.GetBool("ValidateLateStage", false) || state != PlayModeStateChange.EnteredPlayMode) return;
        started = EditorApplication.timeSinceStartup;
        checkpoint = started;
        step = 0;
        report.Clear();
        EditorApplication.update -= Tick;
        EditorApplication.update += Tick;
    }
    private static void Require(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
        report.Add("PASS: " + message);
    }
    private static void Tick()
    {
        try
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - started > 100) throw new Exception("Runtime sequence timed out; phase " + (evt != null ? evt.Phase.ToString() : "missing"));
            if (step == 0 && now - started > 1.5)
            {
                simulation = UnityEngine.Object.FindObjectOfType<SimulationManager>();
                evt = UnityEngine.Object.FindObjectOfType<LandslideEvent>();
                terrain = UnityEngine.Object.FindObjectOfType<Terrain>();
                Require(evt != null && simulation != null && terrain != null, "Bootstrap attaches the extension to the original desktop scene.");
                Require(evt.Phase == LandslidePhase.Dormant && !evt.Visuals.IsBuilt, "Original early stage contains no new visible geometry or particles.");
                data = terrain.terrainData;
                heights = data.GetHeights(0, 0, data.heightmapResolution, data.heightmapResolution);
                simulation.SetPlaying(true);
                simulation.currentTimestep = (float)End.GetValue(simulation) + 0.1f;
                checkpoint = now; step = 1;
            }
            else if (step == 1 && now - checkpoint > 0.5)
            {
                Require(evt.Phase == LandslidePhase.Dormant, "Secondary failure waits through its delay after the original flow end.");
                simulation.currentTimestep = (float)End.GetValue(simulation) + evt.Settings.transitionDelay + 0.5f;
                checkpoint = now; step = 2;
            }
            else if (step == 2 && evt.Phase == LandslidePhase.Precursory)
            {
                Require(evt.Visuals.FragmentCount > 100, "A large terrain-aligned failure mass is created after the original sequence.");
                simulation.SetPlaying(false);
                pausedTime = evt.StageTime;
                checkpoint = now; step = 3;
            }
            else if (step == 3 && now - checkpoint > 0.5)
            {
                Require(Mathf.Abs(evt.StageTime - pausedTime) < 0.05f, "Original pause control also pauses the new late stage.");
                simulation.SetPlaying(true); Time.timeScale = 12f; step = 4;
            }
            else if (step == 4 && evt.Phase == LandslidePhase.Failure)
            {
                report.Add("PASS: Progressive slope-failure phase reached."); step = 5;
            }
            else if (step == 5 && evt.Phase == LandslidePhase.Runout)
            {
                report.Add("PASS: Large rock-avalanche runout phase reached."); step = 6;
            }
            else if (step == 6 && evt.StageTime >= evt.TotalDuration)
            {
                Require(evt.Phase == LandslidePhase.Deposited, "Stable deposition is retained after the complete sequence.");
                evt.Cancel();
                Require(!evt.Visuals.IsBuilt, "Reset removes all new geometry and particles.");
                Require(terrain.terrainData == data, "The original TerrainData asset is never replaced.");
                var after = data.GetHeights(0, 0, data.heightmapResolution, data.heightmapResolution);
                for (int z = 0; z < after.GetLength(0); z++) for (int x = 0; x < after.GetLength(1); x++)
                    if (after[z, x] != heights[z, x]) throw new Exception("Original heightmap changed.");
                report.Add("PASS: Every original terrain-height sample is unchanged after full playback and reset.");
                Finish(0);
            }
        }
        catch (Exception error) { report.Add("FAIL: " + error); Finish(1); }
    }
    private static void Finish(int code)
    {
        Time.timeScale = 1f;
        BinaryImporter.StopThread();
        Directory.CreateDirectory(Path.GetDirectoryName(Output));
        File.WriteAllLines(Output, report);
        SessionState.SetBool("ValidateLateStage", false);
        EditorApplication.update -= Tick;
        EditorApplication.Exit(code);
    }
}
