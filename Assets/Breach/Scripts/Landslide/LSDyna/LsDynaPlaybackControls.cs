using System;
using UnityEngine;

/// <summary>Desktop inspection controls; the existing VR play/pause/stop UI uses the same manager.</summary>
public sealed class LsDynaPlaybackControls : MonoBehaviour
{
    private SimulationManager manager;
    private LsDynaPlayback standalone;
    public void Bind(SimulationManager value) { manager = value; }
    public void Bind(LsDynaPlayback value) { standalone = value; }

    private void Update()
    {
        if (manager == null && standalone != null)
        {
            try { standalone.Tick(Time.deltaTime); }
            catch (Exception error)
            {
                standalone.Clear();
                Debug.LogError("Data playback stopped: " + error.Message, this);
            }
        }
    }

    private void OnGUI()
    {
        var player = manager != null ? manager.DataPlayback : standalone;
        string status = manager != null ? manager.DataPlaybackStatus : player != null ? player.Status : "No dataset loaded";
        GUILayout.BeginArea(new Rect(16, 16, Mathf.Min(610, Screen.width - 32), 300), GUI.skin.box);
        bool ready = player != null && player.IsLoaded;
        bool analytic = ready && player.Manifest.provenanceKind == "analytic-verification";
        GUILayout.Label(analytic ? "ANALYTIC VERIFICATION / NOT LS-DYNA RESULTS" : "LS-DYNA / solver data playback");
        GUILayout.Label(status, GUI.skin.label);
        if (ready)
        {
            var site = player.Manifest.siteCaseReport;
            if (!analytic)
                GUILayout.Label(site == null ? "Site rainfall/stratigraphy evidence: not attached" :
                    $"Site {site.caseId}: preparation only; {site.evidenceMissingCount} evidence categories missing");
            GUILayout.Label($"Solver time: {player.SimulationTime:F4} s   |   Frame: {player.CurrentFrame + 1}/{player.FrameCount}");
            GUILayout.Label($"Max interpolated source speed: {player.MaxSpeed:F3} m/s");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(player.IsPlaying ? "Pause" : "Play"))
            {
                if (manager != null) manager.SetPlaying(!player.IsPlaying);
                else if (player.IsPlaying) player.Pause(); else player.Play();
            }
            if (GUILayout.Button("Reset")) { if (manager != null) manager.StopAndReset(); else player.Stop(); }
            GUILayout.EndHorizontal();
            double start = player.Manifest.timesSeconds[0];
            float normalized = player.Duration > 0 ? (float)((player.SimulationTime - start) / player.Duration) : 0f;
            float selected = GUILayout.HorizontalSlider(normalized, 0f, 1f);
            if (Math.Abs(selected - normalized) > 0.000001f)
                player.Seek(start + selected * player.Duration);
            GUILayout.Label("Positions follow exported samples. Playback speed does not change physical velocity.");
        }
        else
        {
            GUILayout.Label("No synthetic fallback. Configure a dataset in Landslide > LS-DYNA > Open setup.");
            if (manager != null && GUILayout.Button("Retry loading dataset")) manager.ReloadLsDyna();
        }
        GUILayout.EndArea();
    }
}
