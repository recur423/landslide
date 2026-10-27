using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Result of the automatic release-zone search.</summary>
public class LandslideSourceEstimate
{
    public bool success;
    public Vector3 center;
    public Vector2 size = new Vector2(55f, 75f);
    public Vector3 slideDirection = Vector3.forward;
    public float runoutDistance = 260f;
    public float slopeAngle = 30f;
    public bool fromSimulation;

    /// <summary>Human readable description of how the estimate was produced (used for logging).</summary>
    public string source = "none";

    public void ApplyTo(LandslideReleaseZone zone)
    {
        if (zone == null) return;
        zone.SetEstimate(center, size, slideDirection, runoutDistance, slopeAngle, fromSimulation);
    }
}

/// <summary>
/// Locates the release area of the landslide and the direction the debris travels.
///
/// Two strategies are combined:
///  1. <b>Simulation data</b> - the RAMMS frames contain velocity fields, so the area where the
///     mass starts to move and the direction it moves in can be read straight from the data.
///  2. <b>Terrain analysis</b> - when the simulation data is not available in time a steep,
///     high part of the terrain is selected and the downhill direction is derived from the
///     terrain normals.
/// </summary>
public static class LandslideSourceEstimator
{
    /// <summary>Frame indices probed while searching for the release area (kept small to limit I/O).</summary>
    private static readonly int[] ProbeFrames = { 0, 3, 10, 25 };

    private const float MinSpeed = 0.5f;
    private const int CellStride = 2;

    /// <summary>
    /// Runs the estimation. The callback is always invoked exactly once - if no estimate
    /// could be produced <see cref="LandslideSourceEstimate.success"/> is false.
    /// </summary>
    public static IEnumerator Estimate(LandslideSettings settings, Terrain terrain,
                                       System.Action<LandslideSourceEstimate> onDone)
    {
        settings.Validate();

        var estimate = new LandslideSourceEstimate();

        if (settings.overrideReleaseZone)
        {
            FillFromSettings(settings, terrain, estimate);
            onDone(estimate);
            yield break;
        }

        if (settings.useSimulationData)
        {
            yield return EstimateFromSimulation(settings, terrain, estimate);
            if (estimate.success)
            {
                onDone(estimate);
                yield break;
            }
        }

        if (EstimateFromTerrain(settings, terrain, estimate))
        {
            onDone(estimate);
            yield break;
        }

        // Last resort: a manually placed position (used by the setup tool / inspector).
        FillFromSettings(settings, terrain, estimate);
        estimate.source = "settings";
        onDone(estimate);
    }

    /// <summary>Uses the values configured on the settings object.</summary>
    public static void FillFromSettings(LandslideSettings settings, Terrain terrain, LandslideSourceEstimate estimate)
    {
        var rad = settings.slideDirectionDegrees * Mathf.Deg2Rad;
        estimate.center = settings.releaseZoneCenter;
        estimate.size = settings.releaseZoneSize;
        estimate.slideDirection = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
        estimate.runoutDistance = settings.runoutDistance;
        estimate.slopeAngle = settings.minSteepness;
        estimate.fromSimulation = false;
        estimate.success = true;
        estimate.source = "settings";

        if (terrain != null)
            estimate.center.y = terrain.SampleHeight(estimate.center);
    }

    /// <summary>
    /// Reads the release area out of the RAMMS frames by looking at where the mass starts
    /// moving (velocity fields) and how the moving area travels over time.
    /// </summary>
    private static IEnumerator EstimateFromSimulation(LandslideSettings settings, Terrain terrain,
                                                     LandslideSourceEstimate estimate)
    {
        estimate.success = false;

        var loaded = new Dictionary<int, MapInfo>();
        var distinctProbes = new HashSet<int>();
        var deadline = Time.realtimeSinceStartup + settings.dataEstimateTimeout;
        var numFrames = -1;
        var lastIndex = -1;

        while (Time.realtimeSinceStartup < deadline)
        {
            if (numFrames < 1)
            {
                try { numFrames = BinaryImporter.GetNumFrames(); }
                catch { numFrames = -1; }
            }

            if (numFrames > 1)
            {
                lastIndex = numFrames - 1;

                foreach (var probe in ProbeFrames)
                {
                    var f = Mathf.Min(probe, lastIndex);
                    distinctProbes.Add(f);

                    try
                    {
                        var info = BinaryImporter.GetFrame(f);
                        if (info == null)
                            BinaryImporter.EnqueueFrame(f);
                        else
                            loaded[f] = info;
                    }
                    catch
                    {
                        // The simulation manager has not set up its data path yet - retry next frame.
                    }
                }

                if (loaded.Count >= distinctProbes.Count)
                    break;
            }

            yield return null;
        }

        if (loaded.Count == 0)
        {
            Debug.LogWarning("[Landslide] Could not read any simulation frame - falling back to the terrain based estimate.");
            yield break;
        }

        if (AnalyzeFrames(loaded, lastIndex, terrain, estimate))
        {
            estimate.fromSimulation = true;
            estimate.source = "simulation data";
            Debug.Log($"[Landslide] Release area estimated from the simulation data: " +
                      $"centre {estimate.center}, direction {estimate.slideDirection}, runout {estimate.runoutDistance:N0} m.");
        }
    }

    public static bool AnalyzeFrames(Dictionary<int, MapInfo> loaded, int lastIndex, Terrain terrain,
                                      LandslideSourceEstimate estimate)
    {
        var indices = new List<int>(loaded.Keys);
        indices.Sort();

        // 1) Where does the mass start to move?
        var firstIndex = -1;
        MapInfo first = null;
        foreach (var i in indices)
        {
            if (!FrameHasMotion(loaded[i])) continue;
            firstIndex = i;
            first = loaded[i];
            break;
        }

        if (first == null)
        {
            Debug.LogWarning("[Landslide] The simulation data does not show any movement - using the terrain based estimate.");
            return false;
        }

        if (!MovingStats(first, out var centerI, out var centerJ, out var meanVx, out var meanVy,
                         out var minI, out var maxI, out var minJ, out var maxJ))
            return false;

        var cellSize = Mathf.Max(0.01f, first.cellSize);
        var center = new Vector3(centerI * cellSize, 0f, -centerJ * cellSize);

        var size = new Vector2(
            Mathf.Clamp((maxI - minI) * cellSize, 20f, 250f),
            Mathf.Clamp((maxJ - minJ) * cellSize, 20f, 250f));

        // 2) Which way is the mass moving?
        var direction = new Vector3(meanVx, 0f, -meanVy);

        // 3) How far does it travel? Compare the first moving frame with the last one.
        var lastMovingIndex = firstIndex;
        var lastMoving = first;
        for (int i = indices.Count - 1; i >= 0; i--)
        {
            if (indices[i] <= firstIndex) continue;
            if (!FrameHasMotion(loaded[indices[i]])) continue;

            lastMovingIndex = indices[i];
            lastMoving = loaded[indices[i]];
            break;
        }

        var runout = estimate.runoutDistance;
        if (lastMoving != first &&
            MovingStats(lastMoving, out var endI, out var endJ, out _, out _, out _, out _, out _, out _))
        {
            var dx = (endI - centerI) * cellSize;
            var dz = -(endJ - centerJ) * cellSize;
            var travelled = Mathf.Sqrt(dx * dx + dz * dz);

            if (travelled > 1f)
            {
                var extrapolation = lastIndex > 0
                    ? Mathf.Clamp(lastIndex / (float)Mathf.Max(1, lastMovingIndex - firstIndex), 1f, 6f)
                    : 1f;

                runout = Mathf.Clamp(travelled * extrapolation, 60f, 1200f);

                // Use displacement in the original mesh coordinate system. RAMMS velocity
                // component conventions can differ from its row/column orientation.
                direction = new Vector3(dx, 0f, dz);
            }
        }

        direction.y = 0f;
        if (direction.sqrMagnitude < 1e-4f) direction = Vector3.forward;
        direction.Normalize();

        estimate.center = center;
        estimate.size = size;
        estimate.slideDirection = direction;
        estimate.runoutDistance = runout;
        estimate.slopeAngle = TerrainSteepness(terrain, center);
        estimate.success = true;

        if (terrain != null)
            estimate.center.y = terrain.SampleHeight(center);
        else
            estimate.center.y = 0f;

        return true;
    }

    /// <summary>Quick scan for any movement above the detection threshold.</summary>
    private static bool FrameHasMotion(MapInfo info)
    {
        var data = info?.myMapData;
        if (data?.Velx == null || data.Vely == null) return false;

        var velx = data.Velx;
        var vely = data.Vely;
        if (velx.N != vely.N || velx.M != vely.M) return false;

        var threshold = MinSpeed * MinSpeed;

        for (int i = 0; i < velx.N; i += CellStride)
        {
            for (int j = 0; j < velx.M; j += CellStride)
            {
                var x = velx[i, j];
                var y = vely[i, j];
                if (!ValidVelocity(x, y, info.noValue)) continue;
                if (x * x + y * y > threshold)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Centroid, extents and mean velocity of the cells that are actually flowing in a frame.
    /// </summary>
    private static bool MovingStats(MapInfo info, out float centerI, out float centerJ,
                                    out float meanVx, out float meanVy,
                                    out int minI, out int maxI, out int minJ, out int maxJ)
    {
        centerI = centerJ = meanVx = meanVy = 0f;
        minI = minJ = int.MaxValue;
        maxI = maxJ = int.MinValue;

        var data = info?.myMapData;
        if (data?.Velx == null || data.Vely == null) return false;

        var velx = data.Velx;
        var vely = data.Vely;
        if (velx.N != vely.N || velx.M != vely.M) return false;

        var maxSpeed2 = 0f;
        for (int i = 0; i < velx.N; i += CellStride)
        {
            for (int j = 0; j < velx.M; j += CellStride)
            {
                var x = velx[i, j];
                var y = vely[i, j];
                if (!ValidVelocity(x, y, info.noValue)) continue;
                var speed2 = x * x + y * y;
                if (speed2 > maxSpeed2) maxSpeed2 = speed2;
            }
        }

        if (maxSpeed2 <= MinSpeed * MinSpeed) return false;

        // Only use the cells that are part of the main body of the flow.
        var cutOff = maxSpeed2 * 0.25f;
        var sumI = 0f;
        var sumJ = 0f;
        var sumVx = 0f;
        var sumVy = 0f;
        var count = 0;

        for (int i = 0; i < velx.N; i += CellStride)
        {
            for (int j = 0; j < velx.M; j += CellStride)
            {
                var x = velx[i, j];
                var y = vely[i, j];
                if (!ValidVelocity(x, y, info.noValue)) continue;
                if (x * x + y * y < cutOff) continue;

                sumI += i;
                sumJ += j;
                sumVx += x;
                sumVy += y;
                count++;

                if (i < minI) minI = i;
                if (i > maxI) maxI = i;
                if (j < minJ) minJ = j;
                if (j > maxJ) maxJ = j;
            }
        }

        if (count == 0) return false;

        centerI = sumI / count;
        centerJ = sumJ / count;
        meanVx = sumVx / count;
        meanVy = sumVy / count;

        return true;
    }

    /// <summary>
    /// Fallback: pick the steepest, highest spot of the terrain (optionally restricted to a
    /// search radius) as the release area and follow the slope downhill for the run-out.
    /// </summary>
    private static bool EstimateFromTerrain(LandslideSettings settings, Terrain terrain,
                                           LandslideSourceEstimate estimate)
    {
        if (terrain == null || terrain.terrainData == null)
        {
            if (!settings.overrideReleaseZone)
                Debug.LogWarning("[Landslide] No terrain available - cannot place the release area automatically.");
            return false;
        }

        var data = terrain.terrainData;
        var size = data.size;
        var searchCenter = new Vector2(settings.searchCenter.x, settings.searchCenter.z);

        const int samples = 40;
        var bestScore = float.NegativeInfinity;
        var bestU = 0.5f;
        var bestV = 0.5f;
        var found = false;

        for (int a = 0; a < samples; a++)
        {
            for (int b = 0; b < samples; b++)
            {
                var u = (a + 0.5f) / samples;
                var v = (b + 0.5f) / samples;
                var world = terrain.transform.rotation * new Vector3(u * size.x, 0f, v * size.z)
                            + terrain.transform.position;

                if (settings.searchRadius > 0f &&
                    Vector2.Distance(new Vector2(world.x, world.z), searchCenter) > settings.searchRadius)
                    continue;

                var steepness = data.GetSteepness(u, v);
                if (steepness < settings.minSteepness) continue;

                var downhill = Vector3.ProjectOnPlane(data.GetInterpolatedNormal(u, v), Vector3.up);
                if (downhill.sqrMagnitude < 0.01f) continue;

                var height = terrain.SampleHeight(world);
                var score = steepness * (height - terrain.transform.position.y + 1f);

                if (score <= bestScore) continue;

                bestScore = score;
                bestU = u;
                bestV = v;
                found = true;
            }
        }

        if (!found)
        {
            Debug.LogWarning($"[Landslide] No terrain slope steeper than {settings.minSteepness} deg was found - " +
                             "the release area could not be placed automatically.");
            return false;
        }

        var center = terrain.transform.rotation * new Vector3(bestU * size.x, 0f, bestV * size.z)
                     + terrain.transform.position;
        center.y = terrain.SampleHeight(center);

        var direction = Vector3.ProjectOnPlane(data.GetInterpolatedNormal(bestU, bestV), Vector3.up).normalized;
        var slopeAngle = data.GetSteepness(bestU, bestV);
        var runout = EstimateRunout(terrain, center, direction, 900f);

        estimate.center = center;
        estimate.size = settings.releaseZoneSize;
        estimate.slideDirection = direction;
        estimate.runoutDistance = runout;
        estimate.slopeAngle = slopeAngle;
        estimate.fromSimulation = false;
        estimate.success = true;
        estimate.source = "terrain analysis";

        Debug.Log($"[Landslide] Release area estimated from the terrain: centre {center}, " +
                  $"slope {slopeAngle:N0} deg, runout {runout:N0} m.");

        return true;
    }

    /// <summary>Walks downhill from the release area until the slope flattens out.</summary>
    private static float EstimateRunout(Terrain terrain, Vector3 from, Vector3 direction, float maxDistance)
    {
        const float step = 20f;

        var data = terrain.terrainData;
        var startHeight = terrain.SampleHeight(from);
        var travelled = 0f;
        var current = from;

        while (travelled < maxDistance)
        {
            var next = current + direction * step;
            var local = terrain.transform.InverseTransformPoint(next);
            var u = local.x / Mathf.Max(0.01f, data.size.x);
            var v = local.z / Mathf.Max(0.01f, data.size.z);

            if (u < 0f || u > 1f || v < 0f || v > 1f) break;

            var height = terrain.SampleHeight(next);
            var steepness = data.GetSteepness(u, v);

            travelled += step;
            current = next;

            // The debris stops once it reaches the (flatter) valley floor.
            if (steepness < 8f && (startHeight - height) > 5f) break;
        }

        return Mathf.Clamp(travelled, 40f, maxDistance);
    }

    private static bool ValidVelocity(float x, float y, float noValue)
    {
        return !float.IsNaN(x) && !float.IsNaN(y) && !float.IsInfinity(x) && !float.IsInfinity(y)
               && Mathf.Abs(x) < 250f && Mathf.Abs(y) < 250f
               && !(Mathf.Abs(noValue) > 0.001f && (x == noValue || y == noValue));
    }

    private static float TerrainSteepness(Terrain terrain, Vector3 world)
    {
        if (terrain == null || terrain.terrainData == null) return 30f;

        var data = terrain.terrainData;
        var local = terrain.transform.InverseTransformPoint(world);
        var u = Mathf.Clamp01(local.x / Mathf.Max(0.01f, data.size.x));
        var v = Mathf.Clamp01(local.z / Mathf.Max(0.01f, data.size.z));

        return data.GetSteepness(u, v);
    }
}
