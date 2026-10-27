using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Secondary mountain failure after the original RAMMS sequence.</summary>
[DisallowMultipleComponent]
public class LandslideEvent : MonoBehaviour
{
    [SerializeField] private LandslideSettings settings = LandslideSettings.Default();
    [SerializeField] private LandslideReleaseZone zone;
    private SimulationManager simulation;
    private LandslideLateStageVisuals visuals;
    private Terrain terrain;
    private Coroutine runner;
    private bool preview, paused, sawPlaying, waitForReplay;
    private int lastFrame;
    private AudioSource rumble;
    private AudioClip rumbleClip;
    private static readonly FieldInfo FlowEnd = typeof(SimulationManager).GetField("flowTimeEnd", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo Playing = typeof(SimulationManager).GetField("playing", BindingFlags.Instance | BindingFlags.NonPublic);

    public static LandslideEvent Active { get; private set; }
    public static event EventHandler<LandslidePhase> OnPhaseChanged;
    public static event EventHandler<LandslideEvent> OnLandslideTriggered;
    public static event EventHandler<LandslideEvent> OnLandslideFinished;
    public LandslidePhase Phase { get; private set; } = LandslidePhase.Dormant;
    public float ElapsedInPhase { get; private set; }
    public float StageTime { get; private set; }
    public LandslideSettings Settings => settings;
    public LandslideSettings MutableSettings => settings;
    public LandslideReleaseZone Zone => zone;
    public LandslideLateStageVisuals Visuals => visuals;
    public float VolumeEstimate => settings.releaseZoneSize.x * settings.releaseZoneSize.y * settings.blockDepth * 0.65f;
    public float TotalDuration => settings.precursoryDuration + settings.failureDuration + settings.runoutDuration + settings.settlingDuration;
    public bool IsRunning => runner != null;

    private void Awake()
    {
        settings.Validate();
        simulation = FindObjectOfType<SimulationManager>();
        foreach (var t in Terrain.activeTerrains)
            if (terrain == null || t.terrainData.size.x * t.terrainData.size.z > terrain.terrainData.size.x * terrain.terrainData.size.z) terrain = t;
        zone = zone != null ? zone : gameObject.AddComponent<LandslideReleaseZone>();
        visuals = GetComponent<LandslideLateStageVisuals>() ?? gameObject.AddComponent<LandslideLateStageVisuals>();
        Active = this;
    }
    private void Start() { if (!preview) Begin(); }
    public void Begin()
    {
        if (Application.isPlaying && LsDynaProjectSettings.ScientificModeFor(gameObject.scene))
        {
            Cancel();
            return;
        }
        if (runner != null) StopCoroutine(runner);
        StageTime = 0f;
        runner = StartCoroutine(RunSequence());
    }
    private IEnumerator RunSequence()
    {
        SetPhase(LandslidePhase.Dormant);
        while (!preview && simulation != null)
        {
            // Read the existing flow-end clock; never change the original simulation.
            float end = FlowEnd != null ? (float)FlowEnd.GetValue(simulation) : 238f;
            if (end > 0f && simulation.currentTimestep >= end + settings.transitionDelay && !waitForReplay) break;
            if (waitForReplay && simulation.AnimationFrame >= Mathf.Max(2, BinaryImporter.GetNumFrames() - 4)) waitForReplay = false;
            yield return null;
        }
        if (!preview && simulation == null) { runner = null; yield break; }
        if (terrain == null) { Debug.LogError("[Late landslide] Hunnedalen Terrain is required."); runner = null; yield break; }
        LandslideSourceEstimate estimate = null;
        yield return LandslideSourceEstimator.Estimate(settings, terrain, e => estimate = e);
        if (estimate == null || !estimate.success) { runner = null; yield break; }
        // The secondary failure is deliberately larger than the original fluid footprint.
        estimate.size = settings.releaseZoneSize;
        estimate.runoutDistance = settings.runoutDistance;
        estimate.ApplyTo(zone);
        visuals.Build(terrain, zone, settings);
        BuildRumble();
        Debug.Log($"[Late landslide] Original flow complete. Secondary release at {zone.Center}, area {zone.size}, visual volume ~{VolumeEstimate:N0} m3.");
        EvaluateAt(0f);
        while (StageTime < TotalDuration)
        {
            float next = StageTime + (IsPaused() ? 0f : Time.deltaTime);
            EvaluateAt(next);
            yield return null;
        }
        EvaluateAt(TotalDuration);
        runner = null;
    }
    private bool IsPaused()
    {
        if (paused) return true;
        if (preview || simulation == null || Playing == null) return false;
        bool playing = (bool)Playing.GetValue(simulation);
        sawPlaying |= playing;
        return sawPlaying && !playing;
    }
    private void Update()
    {
        if (simulation == null || preview) return;
        if (Playing != null) sawPlaying |= (bool)Playing.GetValue(simulation);
        int frame = simulation.AnimationFrame;
        if (frame < lastFrame - 2 && Phase != LandslidePhase.Dormant)
        {
            waitForReplay = true;
            ResetEvent();
        }
        lastFrame = frame;
    }
    public void EvaluateAt(float seconds)
    {
        if (visuals == null || !visuals.IsBuilt) return;
        StageTime = Mathf.Clamp(seconds, 0f, TotalDuration);
        float failure = StageTime - settings.precursoryDuration;
        float runout = failure - settings.failureDuration;
        float settle = runout - settings.runoutDuration;
        LandslidePhase next = failure < 0f ? LandslidePhase.Precursory : runout < 0f ? LandslidePhase.Failure : settle < 0f ? LandslidePhase.Runout : LandslidePhase.Deposited;
        ElapsedInPhase = next == LandslidePhase.Precursory ? StageTime : next == LandslidePhase.Failure ? failure : next == LandslidePhase.Runout ? runout : settle;
        SetPhase(next);
        visuals.Evaluate(StageTime);
        visuals.SetPaused(IsPaused());
        if (rumble != null)
        {
            float volume = failure < 0f ? 0.15f * Mathf.Clamp01(StageTime / Mathf.Max(1f, settings.precursoryDuration)) : settle < 0f ? 0.85f : 0.85f * (1f - Mathf.Clamp01(settle / Mathf.Max(1f, settings.settlingDuration)));
            rumble.volume = IsPaused() ? 0f : volume * settings.rumbleVolume;
            rumble.transform.position = visuals.FrontPosition;
        }
    }
    private void SetPhase(LandslidePhase value)
    {
        if (Phase == value) return;
        Phase = value;
        OnPhaseChanged?.Invoke(this, value);
        if (value == LandslidePhase.Failure) OnLandslideTriggered?.Invoke(this, this);
        if (value == LandslidePhase.Deposited) OnLandslideFinished?.Invoke(this, this);
    }
    public void SetPaused(bool value) => paused = value;
    public void TriggerNow() { preview = true; Begin(); }
    public void PreparePreview(Terrain target, LandslideReleaseZone release)
    {
        preview = true;
        if (runner != null) { StopCoroutine(runner); runner = null; }
        terrain = target;
        zone = release;
        if (visuals == null) visuals = GetComponent<LandslideLateStageVisuals>() ?? gameObject.AddComponent<LandslideLateStageVisuals>();
        visuals.Build(terrain, zone, settings);
        EvaluateAt(0f);
    }
    public void Cancel()
    {
        if (runner != null) StopCoroutine(runner);
        runner = null;
        StageTime = 0f;
        if (visuals != null) visuals.Clear();
        if (rumble != null) rumble.Stop();
        SetPhase(LandslidePhase.Disabled);
    }
    public void ResetEvent() { Cancel(); preview = false; Begin(); }
    private void BuildRumble()
    {
        if (!Application.isPlaying) return;
        if (rumble != null) { rumble.volume = 0f; rumble.Play(); return; }
        var audio = new GameObject("Secondary landslide rumble");
        audio.transform.SetParent(transform);
        rumble = audio.AddComponent<AudioSource>();
        const int rate = 22050;
        float[] samples = new float[rate * 4];
        var rng = new System.Random(7031);
        float low = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            low = low * 0.97f + (float)(rng.NextDouble() * 2 - 1) * 0.03f;
            float t = i / (float)rate;
            float envelope = Mathf.Sin(Mathf.PI * i / (samples.Length - 1));
            samples[i] = (low * 0.6f + 0.13f * Mathf.Sin(t * 2f * Mathf.PI * 38f) + 0.06f * Mathf.Sin(t * 2f * Mathf.PI * 61f)) * envelope;
        }
        rumbleClip = AudioClip.Create("Secondary slope rumble", samples.Length, 1, rate, false);
        rumbleClip.SetData(samples, 0);
        rumble.clip = rumbleClip;
        rumble.loop = true;
        rumble.spatialBlend = 1f;
        rumble.minDistance = 100f;
        rumble.maxDistance = 2000f;
        rumble.volume = 0f;
        rumble.Play();
    }
    private void OnDestroy()
    {
        if (Active == this) Active = null;
        if (rumbleClip != null) Destroy(rumbleClip);
    }
}

public static class LandslideLateStageBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Register()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }
    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != "Hunnedalen_DSK" && scene.name != "Hunnedalen_VR") return;
        if (LsDynaProjectSettings.ScientificModeFor(scene))
        {
            LsDynaProjectSettings.SuppressLegacyMotion(scene);
            return;
        }
        foreach (var root in scene.GetRootGameObjects())
            if (root.GetComponentInChildren<LandslideEvent>(true) != null) return;
        var go = new GameObject("Late-stage mountain failure");
        SceneManager.MoveGameObjectToScene(go, scene);
        go.AddComponent<LandslideEvent>();
    }
}
