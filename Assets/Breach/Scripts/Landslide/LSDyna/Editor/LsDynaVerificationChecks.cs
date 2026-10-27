using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Executable integration checks against the analytical fixture, not solver validation.</summary>
[InitializeOnLoad]
public static class LsDynaVerificationChecks
{
    private static readonly List<string> report = new List<string>();
    private static string Output => Path.GetFullPath("Validation/LSDyna");
    private static string Fixture => Path.Combine(Application.streamingAssetsPath, "LSDyna/Verification");
    private static double enteredPlay;
    private static readonly List<string> runtimeExceptions = new List<string>();
    static LsDynaVerificationChecks() { EditorApplication.playModeStateChanged += OnPlayState; }

    public static void RunBatch()
    {
        Directory.CreateDirectory(Output);
        report.Clear();
        try
        {
            RunDataChecks();
            LsDynaSetupWindow.CreateVerificationScene();
            RenderVerification();
            File.WriteAllLines(Path.Combine(Output, "unity-checks.txt"), report);
            if (!LsDynaProjectSettings.Load().UsesLsDyna)
                throw new InvalidOperationException("Integration test requires the default LsDyna project mode.");
            EditorSceneManager.OpenScene("Assets/Breach/Scenes/DynamicScenes/Hunnedalen_DSK.unity");
            SessionState.SetBool("LSDynaIntegrationCheck", true);
            EditorApplication.isPlaying = true;
        }
        catch (Exception error) { Finish(error); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        report.Add("PASS: " + message);
    }

    private static void Reject(Action action, string message)
    {
        bool rejected = false;
        try { action(); }
        catch (InvalidDataException) { rejected = true; }
        catch (FileNotFoundException) { rejected = true; }
        catch (DirectoryNotFoundException) { rejected = true; }
        Check(rejected, message);
    }

    private static void RunDataChecks()
    {
        Reject(() => LsDynaDataset.Load(Fixture, false), "Scientific mode rejects the analytic fixture.");
        var data = LsDynaDataset.Load(Fixture, true);
        Check(data.NodeCount == 8 && data.FrameCount == 51, "Analytic fixture contains eight nodes and 51 physical time samples.");
        Check(data.Manifest.solver == "ANALYTIC", "Fixture provenance explicitly identifies an analytical solution.");
        var first = data.ReadFrame(0);
        var last = data.ReadFrame(data.FrameCount - 1);
        double acceleration = 9.81 * (0.5 - 0.3 * Math.Cos(Math.PI / 6));
        double time = data.Duration;
        Vector3 travel = new Vector3((float)Math.Cos(Math.PI / 6), -0.5f, 0f);
        Check(Vector3.Distance(last.Positions[0] - first.Positions[0], travel * (float)(0.5 * acceleration * time * time)) < 0.0001f,
            "Final displacement matches gravity minus Coulomb friction on a 30 degree incline.");
        Check(Vector3.Distance(last.Velocities[0], travel * (float)(acceleration * time)) < 0.0001f,
            "Final exported velocity matches the analytical acceleration.");

        var go = new GameObject("Data playback test");
        try
        {
            var player = go.AddComponent<LsDynaPlayback>();
            player.LoadDataset(Fixture, true);
            Check(!player.IsPlaying && player.SimulationTime == data.StartTime, "Load displays the first state and pauses.");
            double middle = (data.Manifest.timesSeconds[1] + data.Manifest.timesSeconds[2]) / 2;
            player.Seek(middle);
            var expected = Vector3.Lerp(data.ReadFrame(1).Positions[0], data.ReadFrame(2).Positions[0], 0.5f);
            Check(Vector3.Distance(player.GetNodePosition(0), expected) < 0.000001f, "Intermediate position interpolates adjacent exported states.");
            player.Play(); player.Tick(0.02f); player.Pause();
            double paused = player.SimulationTime;
            var pausedPosition = player.GetNodePosition(0);
            player.Tick(1f);
            Check(player.SimulationTime == paused && player.GetNodePosition(0) == pausedPosition, "Pause freezes both data clock and geometry.");
            player.Stop();
            Check(player.SimulationTime == data.StartTime && !player.IsPlaying, "Stop restores the first state and stays paused.");
            player.Play(); player.Tick(100f);
            Check(player.SimulationTime == data.EndTime && !player.IsPlaying && player.CurrentFrame == data.FrameCount - 1,
                "A large tick reaches the last state once, without extrapolation or looping.");
            player.Tick(100f);
            Check(player.GetNodePosition(0) == last.Positions[0], "The final position remains the exact exported sample.");
            player.SeekFrame(10);
            float speed = player.GetNodeVelocity(0).magnitude;
            player.Play(); player.Tick(0f);
            Check(Mathf.Abs(player.MaxSpeed - speed) < 0.00001f, "Velocity display uses solver seconds, independent of render clock.");
            Check(go.GetComponentsInChildren<Rigidbody>().Length == 0 && go.GetComponentsInChildren<Collider>().Length == 0,
                "Playback creates no second physics system or collision corrections.");
            Reject(() => player.LoadDataset(Path.Combine(Output, "missing-dataset"), false), "Missing data is rejected.");
            Check(!player.IsLoaded, "A failed reload removes the old result rather than displaying stale geometry.");
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }

        string scratch = Path.Combine(Output, "invalid-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            foreach (string file in Directory.GetFiles(Fixture)) File.Copy(file, Path.Combine(scratch, Path.GetFileName(file)));
            string manifestPath = Path.Combine(scratch, "dataset.json");
            string original = File.ReadAllText(manifestPath);
            var manifest = JsonUtility.FromJson<LsDynaManifest>(original);
            manifest.nodeIds[1] = manifest.nodeIds[0];
            File.WriteAllText(manifestPath, JsonUtility.ToJson(manifest));
            Reject(() => LsDynaDataset.Load(scratch, true), "Duplicate node IDs are rejected.");
            manifest = JsonUtility.FromJson<LsDynaManifest>(original);
            manifest.timesSeconds[1] = manifest.timesSeconds[0];
            File.WriteAllText(manifestPath, JsonUtility.ToJson(manifest));
            Reject(() => LsDynaDataset.Load(scratch, true), "Non-increasing solver timestamps are rejected.");
            manifest = JsonUtility.FromJson<LsDynaManifest>(original);
            manifest.frameFiles[0] = "../outside.bytes";
            File.WriteAllText(manifestPath, JsonUtility.ToJson(manifest));
            Reject(() => LsDynaDataset.Load(scratch, true), "Frame paths cannot escape the dataset directory.");
            manifest = JsonUtility.FromJson<LsDynaManifest>(original);
            // Irregular intervals and a large initial time must not be reduced to a fixed FPS or float timestamp.
            for (int i = 0; i < manifest.timesSeconds.Length; i++) manifest.timesSeconds[i] = 100000000d + i * i * 0.001d;
            File.WriteAllText(manifestPath, JsonUtility.ToJson(manifest));
            var irregular = LsDynaDataset.Load(scratch, true);
            Check(irregular.FindLowerFrame((manifest.timesSeconds[2] + manifest.timesSeconds[3]) / 2) == 2,
                "Nonuniform timestamps retain double precision and select the correct interval.");
            File.WriteAllText(manifestPath, original);
            string frame = Path.Combine(scratch, data.Manifest.frameFiles[0]);
            byte[] bytes = File.ReadAllBytes(frame);
            byte[] corrupt = (byte[])bytes.Clone();
            Array.Copy(BitConverter.GetBytes(123.456f), 0, corrupt, 12, 4);
            File.WriteAllBytes(frame, corrupt);
            Reject(() => LsDynaDataset.Load(scratch, true), "Finite but altered frame values fail the recorded integrity checksum.");
            Array.Copy(BitConverter.GetBytes(float.NaN), 0, corrupt, 12, 4);
            File.WriteAllBytes(frame, corrupt);
            using (var sha = SHA256.Create()) manifest.frameSha256[0] = BitConverter.ToString(sha.ComputeHash(corrupt)).Replace("-", "");
            File.WriteAllText(manifestPath, JsonUtility.ToJson(manifest));
            Reject(() => LsDynaDataset.Load(scratch, true), "Nonfinite binary positions are rejected before rendering.");
            File.WriteAllBytes(frame, new byte[13]);
            Reject(() => LsDynaDataset.Load(scratch, true), "Truncated binary frames are rejected.");
        }
        finally { Directory.Delete(scratch, true); }
    }

    private static void RenderVerification()
    {
        var demo = UnityEngine.Object.FindObjectOfType<LsDynaVerificationDemo>();
        var player = demo.gameObject.AddComponent<LsDynaPlayback>();
        player.LoadDataset(Fixture, true);
        var camera = Camera.main;
        var target = new RenderTexture(1280, 720, 24);
        var image = new Texture2D(1280, 720, TextureFormat.RGB24, false);
        var previous = RenderTexture.active;
        try
        {
            camera.targetTexture = target;
            foreach (int second in new[] { 0, 2, 5 })
            {
                player.Seek(second);
                camera.Render();
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
                image.Apply();
                File.WriteAllBytes(Path.Combine(Output, "analytic-t" + second + ".png"), image.EncodeToPNG());
            }
            Check(new FileInfo(Path.Combine(Output, "analytic-t0.png")).Length > 1000 &&
                  new FileInfo(Path.Combine(Output, "analytic-t2.png")).Length > 1000 &&
                  new FileInfo(Path.Combine(Output, "analytic-t5.png")).Length > 1000,
                  "Verification scene exported three review images (separate visual review required).");
        }
        finally
        {
            camera.targetTexture = null;
            RenderTexture.active = previous;
            UnityEngine.Object.DestroyImmediate(image);
            UnityEngine.Object.DestroyImmediate(target);
            UnityEngine.Object.DestroyImmediate(player);
        }
    }

    private static void OnPlayState(PlayModeStateChange state)
    {
        if (!SessionState.GetBool("LSDynaIntegrationCheck", false) || state != PlayModeStateChange.EnteredPlayMode) return;
        report.Clear();
        report.AddRange(File.ReadAllLines(Path.Combine(Output, "unity-checks.txt")));
        enteredPlay = EditorApplication.timeSinceStartup;
        runtimeExceptions.Clear();
        Application.logMessageReceived -= RecordRuntimeException;
        Application.logMessageReceived += RecordRuntimeException;
        EditorApplication.update -= CheckRuntime;
        EditorApplication.update += CheckRuntime;
    }

    private static void CheckRuntime()
    {
        if (EditorApplication.timeSinceStartup - enteredPlay < 3d) return;
        try
        {
            var manager = UnityEngine.Object.FindObjectOfType<SimulationManager>();
            Check(manager != null && manager.UsesLsDyna, "Original Hunnedalen desktop scene selects the strict data mode.");
            Check(UnityEngine.Object.FindObjectOfType<LandslideEvent>() == null, "Original scene creates no procedural late-stage landslide in strict mode.");
            Check(!manager.CurrentFrameValid && manager.DataPlaybackStatus.Contains("blocked"), "Missing genuine results show a blocked status and no fake result.");
            Check(manager.currentTimestep == 0f, "Missing data does not advance the scientific clock.");
            foreach (var warning in UnityEngine.Object.FindObjectsOfType<Flickering>(true))
                Check(!warning.enabled, "Legacy road-warning timing is disabled in scientific playback.");
            Check(runtimeExceptions.Count == 0, "Original scene produces no managed exceptions during strict-mode startup.");
            manager.SetPlaying(true); manager.SetPlaying(false); manager.StopAndReset();
            Check(manager.GetWaterHeightAt(Vector3.zero) == null && manager.GetWaterVelocity(Vector3.zero) == null,
                "Rock data is not exposed as a water depth or velocity field.");
            Finish(null);
        }
        catch (Exception error) { Finish(error); }
    }

    private static void Finish(Exception error)
    {
        Application.logMessageReceived -= RecordRuntimeException;
        if (error != null) report.Add("FAIL: " + error);
        report.Add("These checks validate data transport, playback and the analytical fixture; they do not validate a real LS-DYNA slope model.");
        Directory.CreateDirectory(Output);
        File.WriteAllLines(Path.Combine(Output, "unity-checks.txt"), report);
        SessionState.SetBool("LSDynaIntegrationCheck", false);
        EditorApplication.update -= CheckRuntime;
        EditorApplication.Exit(error == null ? 0 : 1);
    }

    private static void RecordRuntimeException(string message, string stack, LogType type)
    {
        if (type == LogType.Exception) runtimeExceptions.Add(message + "\n" + stack);
    }
}
