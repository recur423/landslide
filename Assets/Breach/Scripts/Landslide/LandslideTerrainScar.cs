using UnityEngine;

/// <summary>
/// Deforms the Unity <see cref="Terrain"/> while the landslide is running:
/// the release area is carved away (the scar that is left behind after the slope failed)
/// and the debris that arrives at the toe of the slope builds up a deposit lobe.
///
/// The height-map is only touched in the area that the event actually affects, and it is
/// only written a limited number of times per second, so the update does not wreck the
/// frame-rate of the (VR) experience.
/// </summary>
[DisallowMultipleComponent]
public class LandslideTerrainScar : MonoBehaviour
{
    [SerializeField] private Terrain terrain;
    [SerializeField] private LandslideReleaseZone zone;

    [Header("Release scar")]
    [Tooltip("Depth of the scar in the middle of the release area (meters).")]
    [SerializeField] private float scarDepth = 7f;
    [Tooltip("Higher values make the scar blend in with the surrounding terrain more smoothly.")]
    [SerializeField] private float scarFalloff = 1.6f;

    [Header("Deposit")]
    [Tooltip("Height of the accumulation lobe in the middle of the deposit (meters).")]
    [SerializeField] private float depositHeight = 5f;
    [Tooltip("How far down the run-out path the deposit starts growing (0 = at the release area, 1 = at the very end).")]
    [Range(0f, 0.95f)] [SerializeField] private float depositStart = 0.45f;
    [SerializeField] private float depositFalloff = 1.5f;

    [Header("Performance")]
    [Tooltip("Height-map updates per second while the landslide is running.")]
    [SerializeField] private float updatesPerSecond = 8f;
    [Tooltip("Maximum number of height-map samples touched in one update.")]
    [SerializeField] private int maxRegionSamples = 512;

    [SerializeField] private bool logEvents = true;

    private TerrainData data;
    private float[,] region;      // normalised heights of the affected area (working copy)
    private float[,] original;    // normalised heights as they were before the landslide
    private int xBase, yBase, width, height;
    private int resolution;
    private bool captured;

    private float carveProgress;
    private float depositProgress;
    private float nextUpdateTime;
    private bool dirty;

    // Linear mapping from height-map coordinates (u, v) to the release-zone frame (across, along).
    private float originAcross, ax, az;
    private float originAlong, bx, bz;
    private float acrossRadius, alongRadius, depositAlongRadius;

    private const float MetersToNormalised = 1f;

    public bool IsReady => captured;
    public Terrain Terrain => terrain;

    public void SetZone(LandslideReleaseZone releaseZone)
    {
        zone = releaseZone;
        captured = false;
    }

    public void SetTerrain(Terrain value)
    {
        terrain = value;
        captured = false;
    }

    public void Configure(float carveDepth, float deposit, float updates)
    {
        scarDepth = carveDepth;
        depositHeight = deposit;
        if (updates > 0f) updatesPerSecond = updates;
    }

    /// <summary>
    /// Reads the current height-map of the affected area. Must be called before the
    /// landslide starts so the original terrain can be restored afterwards.
    /// </summary>
    public bool Capture()
    {
        captured = false;

        if (terrain == null)
            terrain = Terrain.activeTerrain;

        if (terrain == null || terrain.terrainData == null)
        {
            if (logEvents) Debug.LogWarning("[Landslide] No terrain found - the scar will not be carved.");
            return false;
        }

        if (zone == null)
        {
            if (logEvents) Debug.LogWarning("[Landslide] No release zone assigned - the scar will not be carved.");
            return false;
        }

        data = terrain.terrainData;
        resolution = data.heightmapResolution;

        // The deposit is an elongated lobe at the toe of the slope.
        acrossRadius = Mathf.Max(zone.size.x * 0.5f, 10f);
        alongRadius = Mathf.Max(zone.size.y * 0.5f, 10f);
        depositAlongRadius = Mathf.Max(zone.size.y, zone.runoutDistance * 0.3f);

        var acrossExtent = Mathf.Max(acrossRadius, zone.size.x * 0.9f) * 1.2f;
        var alongMin = -alongRadius * 1.2f;
        var alongMax = zone.runoutDistance + depositAlongRadius * 1.2f;

        var min = Vector3.positiveInfinity;
        var max = Vector3.negativeInfinity;
        foreach (var corner in new[]
        {
            new Vector2(-acrossExtent, alongMin),
            new Vector2(acrossExtent, alongMin),
            new Vector2(acrossExtent, alongMax),
            new Vector2(-acrossExtent, alongMax)
        })
        {
            var world = zone.LocalToWorld(corner.x, corner.y);
            min = Vector3.Min(min, world);
            max = Vector3.Max(max, world);
        }

        if (!WorldToHeightmapRect(min, max, out xBase, out yBase, out width, out height))
            return false;

        original = data.GetHeights(xBase, yBase, width, height);
        region = new float[height, width];
        System.Array.Copy(original, region, original.Length);

        CacheZoneMapping();
        captured = true;

        if (logEvents)
            Debug.Log($"[Landslide] Terrain scar captured: {width}x{height} height-map samples " +
                      $"at ({xBase}, {yBase}).");

        return true;
    }

    /// <summary>Sets how far the scar has been excavated (0 = intact slope, 1 = fully carved).</summary>
    public void SetCarveProgress(float progress01)
    {
        carveProgress = Mathf.Clamp01(progress01);
        Touch();
    }

    /// <summary>Sets how much of the deposit has been built up (0 = nothing, 1 = complete lobe).</summary>
    public void SetDepositProgress(float progress01)
    {
        depositProgress = Mathf.Clamp01(progress01);
        Touch();
    }

    /// <summary>Restores the terrain to the state it had before the landslide.</summary>
    public void Restore()
    {
        carveProgress = 0f;
        depositProgress = 0f;

        if (!captured || data == null) return;

        data.SetHeightsDelayLOD(xBase, yBase, original);
        terrain.Flush();
        dirty = false;
    }

    private void Update()
    {
        if (!captured || !dirty || Time.time < nextUpdateTime) return;

        Apply();
    }

    private void Touch()
    {
        if (!captured) return;
        dirty = true;
    }

    private void Apply()
    {
        var sizeY = Mathf.Max(0.01f, data.size.y);
        var scarDelta = scarDepth * (MetersToNormalised / sizeY);
        var moundDelta = depositHeight * (MetersToNormalised / sizeY);

        var alongStart = depositStart * zone.runoutDistance;

        for (int j = 0; j < height; j++)
        {
            // Height-map v (z) for this row
            var v = (yBase + j) / (float)(resolution - 1);
            var alongRow = originAlong + v * bz;
            var acrossRow = originAcross + v * az;

            for (int i = 0; i < width; i++)
            {
                var u = (xBase + i) / (float)(resolution - 1);
                var across = acrossRow + u * ax;
                var along = alongRow + u * bx;

                var h = original[j, i];

                if (carveProgress > 0f)
                {
                    var d = Mathf.Sqrt(Sqr(across / acrossRadius) + Sqr(along / alongRadius));
                    var w = Mathf.Pow(Mathf.Clamp01(1f - d), scarFalloff);
                    h -= scarDelta * w * carveProgress;
                }

                if (depositProgress > 0f)
                {
                    var rel = along - alongStart;
                    var d = Mathf.Sqrt(Sqr(across / (acrossRadius * 1.6f)) +
                                       Sqr(rel / Mathf.Max(1f, depositAlongRadius)));
                    var w = Mathf.Pow(Mathf.Clamp01(1f - d), depositFalloff);
                    h += moundDelta * w * depositProgress;
                }

                region[j, i] = h;
            }
        }

        data.SetHeightsDelayLOD(xBase, yBase, region);
        terrain.Flush();
        dirty = false;
        nextUpdateTime = Time.time + 1f / Mathf.Max(1f, updatesPerSecond);
    }

    /// <summary>
    /// Maps a world-space AABB onto a (clamped) block of height-map samples.
    /// </summary>
    private bool WorldToHeightmapRect(Vector3 min, Vector3 max, out int x, out int y, out int w, out int h)
    {
        x = y = w = h = 0;

        var res = resolution;
        var invX = 1f / Mathf.Max(0.01f, data.size.x);
        var invZ = 1f / Mathf.Max(0.01f, data.size.z);

        var localMin = terrain.transform.InverseTransformPoint(min);
        var localMax = terrain.transform.InverseTransformPoint(max);

        var uMin = Mathf.Clamp01(Mathf.Min(localMin.x, localMax.x) * invX);
        var uMax = Mathf.Clamp01(Mathf.Max(localMin.x, localMax.x) * invX);
        var vMin = Mathf.Clamp01(Mathf.Min(localMin.z, localMax.z) * invZ);
        var vMax = Mathf.Clamp01(Mathf.Max(localMin.z, localMax.z) * invZ);

        x = Mathf.Clamp(Mathf.FloorToInt(uMin * (res - 1)), 0, res - 1);
        y = Mathf.Clamp(Mathf.FloorToInt(vMin * (res - 1)), 0, res - 1);
        w = Mathf.Clamp(Mathf.CeilToInt(uMax * (res - 1)) - x + 1, 1, res - x);
        h = Mathf.Clamp(Mathf.CeilToInt(vMax * (res - 1)) - y + 1, 1, res - y);

        if (w > maxRegionSamples || h > maxRegionSamples)
        {
            if (logEvents)
                Debug.LogWarning($"[Landslide] The configured scar/deposit area is larger than the " +
                                 $"'{maxRegionSamples}' sample budget ({w}x{h}). Reduce the release area " +
                                 "or raise 'maxRegionSamples'.");
            w = Mathf.Min(w, maxRegionSamples);
            h = Mathf.Min(h, maxRegionSamples);
        }

        return w > 0 && h > 0;
    }

    /// <summary>
    /// Pre-computes the affine mapping from height-map coordinates to the release-zone frame
    /// so the per-sample work inside <see cref="Apply"/> is a couple of multiply-adds.
    /// </summary>
    private void CacheZoneMapping()
    {
        var t = terrain.transform;
        var origin = t.TransformPoint(Vector3.zero);
        var ex = t.TransformVector(Vector3.right) * data.size.x;
        var ez = t.TransformVector(Vector3.forward) * data.size.z;

        var across = zone.AcrossDirection;
        var along = zone.SlideDirection;
        var rel = origin - zone.Center;

        originAcross = Vector3.Dot(rel, across);
        originAlong = Vector3.Dot(rel, along);

        ax = Vector3.Dot(ex, across);
        az = Vector3.Dot(ez, across);
        bx = Vector3.Dot(ex, along);
        bz = Vector3.Dot(ez, along);
    }

    private static float Sqr(float v) => v * v;
}
