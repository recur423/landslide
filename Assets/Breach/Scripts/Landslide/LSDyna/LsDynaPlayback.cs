using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// A data renderer, not a dynamics solver. Positions and velocities are interpolated only
/// between adjacent exported states. No gravity, rigid bodies, terrain clamping or extrapolation.
/// Tick is explicitly driven by the host's playback clock; there is no automatic Update.
/// </summary>
[DisallowMultipleComponent]
public sealed class LsDynaPlayback : MonoBehaviour
{
    [SerializeField] private Material materialTemplate;
    [SerializeField] private bool colorBySpeed = true;
    [SerializeField] private Color solidColor = new Color(0.48f, 0.43f, 0.34f, 1f);
    [SerializeField, Min(0.001f)] private float nodeMarkerRadius = 0.35f;
    [SerializeField, Min(0f)] private float speedColorMaximum;
    private const int NodesPerMarkerMesh = 10000;
    private static readonly Vector3[] MarkerOffsets = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
    private static readonly int[] MarkerTriangles = { 2, 4, 0, 2, 1, 4, 2, 5, 1, 2, 0, 5, 3, 0, 4, 3, 4, 1, 3, 1, 5, 3, 5, 0 };

    private sealed class RenderChunk
    {
        internal Mesh mesh;
        internal int firstNode, nodeCount;
        internal Vector3[] vertices;
        internal Color[] colors;
    }
    private sealed class RenderState
    {
        internal LsDynaDataset dataset;
        internal GameObject root;
        internal Material material;
        internal readonly List<RenderChunk> chunks = new List<RenderChunk>();
        internal bool markers;
    }
    private RenderState state;
    public bool IsLoaded => state != null;
    public bool IsPlaying { get; private set; }
    public string Status { get; private set; } = "No LS-DYNA dataset loaded.";
    public double SimulationTime { get; private set; }
    public double Duration => IsLoaded ? state.dataset.Duration : 0d;
    public int FrameCount => IsLoaded ? state.dataset.FrameCount : 0;
    public int CurrentFrame { get; private set; }
    /// <summary>Maximum interpolated exported velocity magnitude at the displayed time (m/s).</summary>
    public float MaxSpeed { get; private set; }
    public LsDynaManifest Manifest => IsLoaded ? state.dataset.Manifest : null;
    public bool UsesNodeMarkers => IsLoaded && state.markers;
    public bool ColorBySpeed { get => colorBySpeed; set { colorBySpeed = value; Refresh(); } }
    public float SpeedColorMaximum { get => speedColorMaximum; set { if (!LsDynaDataset.Finite(value) || value < 0f) throw new ArgumentOutOfRangeException(nameof(value)); speedColorMaximum = value; Refresh(); } }

    public void LoadDataset(string directory, bool allowAnalytic = false)
    {
        // A failed source switch must never leave an older result looking current.
        Clear();
        Status = "Loading and validating LS-DYNA dataset.";
        RenderState candidate = null;
        try
        {
            ValidateTransform();
            if (!LsDynaDataset.Finite(nodeMarkerRadius) || nodeMarkerRadius <= 0f || !LsDynaDataset.Finite(speedColorMaximum) || speedColorMaximum < 0f)
                throw new InvalidOperationException("Marker radius must be finite and positive; speed color maximum must be finite and nonnegative.");
            candidate = new RenderState { dataset = LsDynaDataset.Load(directory, allowAnalytic) };
            BuildGeometry(candidate);
            float maximum = RenderAt(candidate, candidate.dataset.StartTime);
            state = candidate;
            SimulationTime = candidate.dataset.StartTime;
            CurrentFrame = 0;
            MaxSpeed = maximum;
            IsPlaying = false;
            candidate.root.SetActive(true);
            UpdateStatus("Paused");
        }
        catch (Exception error)
        {
            Release(candidate);
            state = null;
            Status = "LS-DYNA data rejected: " + error.Message;
            throw;
        }
    }

    private void BuildGeometry(RenderState target)
    {
        target.markers = target.dataset.Manifest.triangles.Length == 0;
        target.root = new GameObject(target.markers ? "LS-DYNA nodes (markers, not solid volumes)" : "LS-DYNA exported surface");
        target.root.SetActive(false);
        target.root.transform.SetParent(transform, false);
        var template = materialTemplate != null ? materialTemplate : Resources.Load<Material>("LandslideLateStage/Rock");
        if (template != null) target.material = new Material(template);
        else
        {
            var shader = Shader.Find("Hunnedalen/LateStageRock");
            if (shader == null) throw new InvalidOperationException("Missing vertex-color rock material/shader. Assign a material supporting mesh vertex colors.");
            target.material = new Material(shader);
        }
        target.material.name = "LS-DYNA playback material";
        if (target.material.HasProperty("_BaseColor")) target.material.SetColor("_BaseColor", Color.white);
        if (target.material.HasProperty("_Color")) target.material.SetColor("_Color", Color.white);
        for (int first = 0; first < target.dataset.NodeCount;)
        {
            int count = target.markers ? Math.Min(NodesPerMarkerMesh, target.dataset.NodeCount - first) : target.dataset.NodeCount;
            var chunk = new RenderChunk { firstNode = first, nodeCount = count };
            target.chunks.Add(chunk);
            int vertexCount = target.markers ? count * 6 : count;
            chunk.vertices = new Vector3[vertexCount];
            chunk.colors = new Color[vertexCount];
            chunk.mesh = new Mesh { name = target.markers ? "Exported node glyphs" : "Exported topology", indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            chunk.mesh.MarkDynamic();
            chunk.mesh.vertices = chunk.vertices;
            if (target.markers)
            {
                var indices = new int[count * MarkerTriangles.Length];
                var normals = new Vector3[vertexCount];
                for (int i = 0; i < count; i++)
                {
                    for (int j = 0; j < 6; j++) normals[i * 6 + j] = MarkerOffsets[j];
                    for (int j = 0; j < MarkerTriangles.Length; j++) indices[i * MarkerTriangles.Length + j] = i * 6 + MarkerTriangles[j];
                }
                chunk.mesh.triangles = indices;
                chunk.mesh.normals = normals;
            }
            else chunk.mesh.triangles = target.dataset.Manifest.triangles;
            var go = new GameObject(target.markers ? "Node markers " + first : "Surface mesh");
            go.transform.SetParent(target.root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = chunk.mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = target.material;
            renderer.shadowCastingMode = target.markers ? ShadowCastingMode.Off : ShadowCastingMode.On;
            renderer.receiveShadows = !target.markers;
            first += count;
        }
    }

    public void Play()
    {
        RequireLoaded();
        ValidateTransform();
        IsPlaying = SimulationTime < state.dataset.EndTime;
        UpdateStatus(IsPlaying ? "Playing" : "At final exported state");
    }
    public void Pause() { IsPlaying = false; if (IsLoaded) UpdateStatus("Paused"); }
    public void Stop()
    {
        if (!IsLoaded) return;
        IsPlaying = false;
        Seek(state.dataset.StartTime);
        UpdateStatus("Stopped at first exported state");
    }
    public void SeekFrame(int index)
    {
        RequireLoaded();
        if (index < 0 || index >= FrameCount) throw new ArgumentOutOfRangeException(nameof(index));
        Seek(Manifest.timesSeconds[index]);
    }
    public void Seek(double absoluteSimulationSeconds)
    {
        RequireLoaded();
        ValidateTransform();
        if (!LsDynaDataset.Finite(absoluteSimulationSeconds)) throw new ArgumentOutOfRangeException(nameof(absoluteSimulationSeconds));
        double time = Math.Max(state.dataset.StartTime, Math.Min(state.dataset.EndTime, absoluteSimulationSeconds));
        // Read and validate both states before publishing a new playback time.
        float maximum = RenderAt(state, time);
        SimulationTime = time;
        CurrentFrame = state.dataset.FindLowerFrame(time);
        MaxSpeed = maximum;
        if (time >= state.dataset.EndTime) IsPlaying = false;
        UpdateStatus(IsPlaying ? "Playing" : time >= state.dataset.EndTime ? "At final exported state" : "Paused");
    }
    public void Tick(float deltaTime)
    {
        if (!LsDynaDataset.Finite(deltaTime) || deltaTime < 0f) throw new ArgumentOutOfRangeException(nameof(deltaTime));
        if (!IsPlaying || !IsLoaded) return;
        Seek(Math.Min(state.dataset.EndTime, SimulationTime + deltaTime));
    }
    /// <summary>Interpolated exported position in Unity-local metres, before registration pose.</summary>
    public Vector3 GetNodePosition(int nodeIndex) => GetNodeValue(nodeIndex, false);
    /// <summary>Interpolated exported velocity in Unity-local metres per solver second.</summary>
    public Vector3 GetNodeVelocity(int nodeIndex) => GetNodeValue(nodeIndex, true);
    private Vector3 GetNodeValue(int nodeIndex, bool velocity)
    {
        RequireLoaded();
        if (nodeIndex < 0 || nodeIndex >= state.dataset.NodeCount) throw new ArgumentOutOfRangeException(nameof(nodeIndex));
        int lower = state.dataset.FindLowerFrame(SimulationTime);
        int upper = Math.Min(lower + 1, state.dataset.FrameCount - 1);
        var a = state.dataset.ReadFrame(lower);
        var b = state.dataset.ReadFrame(upper);
        double t = upper == lower ? 0d : (SimulationTime - Manifest.timesSeconds[lower]) / (Manifest.timesSeconds[upper] - Manifest.timesSeconds[lower]);
        return velocity ? Interpolate(a.Velocities[nodeIndex], b.Velocities[nodeIndex], t)
                        : Interpolate(a.Positions[nodeIndex], b.Positions[nodeIndex], t);
    }
    private void Refresh() { if (IsLoaded) Seek(SimulationTime); }

    private float RenderAt(RenderState target, double time)
    {
        int lower = target.dataset.FindLowerFrame(time);
        int upper = Math.Min(lower + 1, target.dataset.FrameCount - 1);
        LsDynaFrame a = target.dataset.ReadFrame(lower), b = target.dataset.ReadFrame(upper);
        double t = upper == lower ? 0d : (time - target.dataset.Manifest.timesSeconds[lower]) / (target.dataset.Manifest.timesSeconds[upper] - target.dataset.Manifest.timesSeconds[lower]);
        float maximum = 0f;
        float colorMaximum = speedColorMaximum > 0f ? speedColorMaximum : Math.Max(0.0001f, target.dataset.MaximumSourceSpeed);
        foreach (var chunk in target.chunks)
        {
            for (int i = 0; i < chunk.nodeCount; i++)
            {
                int node = chunk.firstNode + i;
                // Evaluate in double to avoid float subtraction overflow before interpolation.
                Vector3 position = Interpolate(a.Positions[node], b.Positions[node], t);
                Vector3 velocity = Interpolate(a.Velocities[node], b.Velocities[node], t);
                float speed = (float)Math.Sqrt((double)velocity.x * velocity.x + (double)velocity.y * velocity.y + (double)velocity.z * velocity.z);
                maximum = Math.Max(maximum, speed);
                Color color = colorBySpeed ? SpeedColor(Mathf.Clamp01(speed / colorMaximum)) : solidColor;
                color.a = 1f;
                if (target.markers)
                {
                    for (int j = 0; j < 6; j++)
                    {
                        chunk.vertices[i * 6 + j] = position + MarkerOffsets[j] * nodeMarkerRadius;
                        chunk.colors[i * 6 + j] = color;
                    }
                }
                else { chunk.vertices[i] = position; chunk.colors[i] = color; }
            }
            chunk.mesh.vertices = chunk.vertices;
            chunk.mesh.colors = chunk.colors;
            if (!target.markers) chunk.mesh.RecalculateNormals();
            chunk.mesh.RecalculateBounds();
        }
        return maximum;
    }
    private static Vector3 Interpolate(Vector3 a, Vector3 b, double t)
        => new Vector3((float)((1d - t) * a.x + t * b.x), (float)((1d - t) * a.y + t * b.y), (float)((1d - t) * a.z + t * b.z));
    private static Color SpeedColor(float value)
        => value < 0.5f ? Color.Lerp(new Color(0.08f, 0.2f, 0.85f), new Color(0.1f, 0.9f, 0.65f), value * 2f)
                         : Color.Lerp(new Color(0.1f, 0.9f, 0.65f), new Color(1f, 0.16f, 0.04f), value * 2f - 1f);
    private void RequireLoaded() { if (!IsLoaded) throw new InvalidOperationException("Load a valid LS-DYNA dataset before playback."); }
    private void ValidateTransform()
    {
        for (Transform current = transform; current != null; current = current.parent)
        {
            Vector3 scale = current.localScale;
            if (!LsDynaDataset.Finite(scale.x) || !LsDynaDataset.Finite(scale.y) || !LsDynaDataset.Finite(scale.z) ||
                Mathf.Abs(scale.x - 1f) > 0.00001f || Mathf.Abs(scale.y - 1f) > 0.00001f || Mathf.Abs(scale.z - 1f) > 0.00001f)
                throw new InvalidOperationException("LS-DYNA registration allows position and rotation only. The player and every parent must have scale (1,1,1).");
        }
    }
    private void UpdateStatus(string activity)
    {
        string origin = Manifest.provenanceKind == "analytic-verification" ? "ANALYTIC VERIFICATION ONLY; not LS-DYNA results" : "LS-DYNA exported data (source model validation required)";
        Status = activity + " | " + origin + (state.markers ? " | Node glyphs are markers, not physical volumes." : " | Exported fixed surface topology.");
    }
    public void Clear()
    {
        IsPlaying = false;
        Release(state);
        state = null;
        SimulationTime = 0d; CurrentFrame = 0; MaxSpeed = 0f;
        Status = "No LS-DYNA dataset loaded.";
    }
    private static void Release(RenderState target)
    {
        if (target == null) return;
        if (target.root != null) target.root.SetActive(false);
        foreach (var chunk in target.chunks) DisposeObject(chunk.mesh);
        DisposeObject(target.material);
        DisposeObject(target.root);
    }
    private static void DisposeObject(UnityEngine.Object value)
    {
        if (value == null) return;
        if (Application.isPlaying) Destroy(value); else DestroyImmediate(value);
    }
    private void OnDestroy() => Clear();
}
