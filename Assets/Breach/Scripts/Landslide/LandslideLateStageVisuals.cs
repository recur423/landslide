using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Terrain-conforming secondary rock avalanche. Its trajectories are authored visual
/// trajectories, not RAMMS/LS-DYNA results. Never writes the original TerrainData.
/// </summary>
public class LandslideLateStageVisuals : MonoBehaviour
{
    private class Fragment
    {
        public Transform transform;
        public float across, along, delay, distance, size, spin, seed;
        public bool slab;
        public Vector3 rest;
        public Quaternion rotation;
    }
    private Terrain terrain;
    private LandslideReleaseZone zone;
    private LandslideSettings settings;
    private Transform root;
    private readonly List<Fragment> fragments = new List<Fragment>();
    private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
    private readonly List<LineRenderer> cracks = new List<LineRenderer>();
    private Vector3[] route;
    private Vector3[] tangents;
    private float routeStep;
    private Material rockMaterial, earthMaterial, crackMaterial;
    private LandslideDustCloud dust;
    private Mesh surface;
    private Vector3[] surfaceGround, surfaceVertices;
    private Color[] surfaceColors;
    private float[] surfaceAlong;
    private float nextSurfaceTime = -1f;
    private const int Rows = 260, Columns = 90;
    private float lastTime = -1f;
    public bool IsBuilt => root != null;
    public Vector3 FrontPosition { get; private set; }
    public int FragmentCount => fragments.Count;
    public Vector3[] Route => route;

    public void Build(Terrain target, LandslideReleaseZone release, LandslideSettings config)
    {
        Clear();
        terrain = target; zone = release; settings = config;
        var go = new GameObject("Secondary failure geometry");
        go.transform.SetParent(transform, false);
        root = go.transform;
        rockMaterial = MakeMaterial("Rock", new Color(0.48f, 0.43f, 0.34f));
        earthMaterial = MakeMaterial("Earth", new Color(0.32f, 0.25f, 0.17f));
        crackMaterial = MakeMaterial("Fracture", new Color(0.065f, 0.054f, 0.041f));
        BuildRoute();
        BuildFragments();
        BuildSurface();
        BuildCracks();
        var dustGo = new GameObject("Secondary dust and wet sediment mist");
        dustGo.transform.SetParent(root, false);
        dust = dustGo.AddComponent<LandslideDustCloud>();
        dust.SetPlumeAnchor(zone.transform);
        dust.SetIntensityScale(settings.dustIntensity);
        dust.Build();
        dust.SetPlumePosition(zone.Center + Vector3.up * 5f);
        nextSurfaceTime = -1f;
        lastTime = -1f;
        Evaluate(0f);
    }

    private Material MakeMaterial(string label, Color color)
    {
        var template = Resources.Load<Material>("LandslideLateStage/" + label);
        var shader = Shader.Find("Hunnedalen/LateStageRock") ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var mat = template != null ? new Material(template) : new Material(shader);
        mat.name = "Secondary " + label;
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.08f);
        mat.enableInstancing = true;
        owned.Add(mat);
        return mat;
    }

    private float Height(Vector3 point)
        => terrain.SampleHeight(point) + terrain.transform.position.y;

    private Vector3 ClampToTerrain(Vector3 p)
    {
        var origin = terrain.transform.position;
        var size = terrain.terrainData.size;
        p.x = Mathf.Clamp(p.x, origin.x + 4f, origin.x + size.x - 4f);
        p.z = Mathf.Clamp(p.z, origin.z + 4f, origin.z + size.z - 4f);
        p.y = Height(p);
        return p;
    }

    private void BuildRoute()
    {
        // Continue from the RAMMS release region along the actual existing mountainside.
        int count = Mathf.CeilToInt(zone.runoutDistance / 8f) + 1;
        route = new Vector3[count]; tangents = new Vector3[count];
        routeStep = zone.runoutDistance / (count - 1);
        route[0] = ClampToTerrain(zone.Center);
        var direction = zone.SlideDirection;
        for (int i = 1; i < count; i++)
        {
            float score = float.PositiveInfinity;
            Vector3 best = route[i - 1] + direction * routeStep;
            Vector3 bestDirection = direction;
            for (int j = -5; j <= 5; j++)
            {
                var candidateDirection = Quaternion.AngleAxis(j * 9f, Vector3.up) * direction;
                var candidate = ClampToTerrain(route[i - 1] + candidateDirection * routeStep);
                float candidateScore = candidate.y + Mathf.Abs(j) * 0.55f;
                if (candidateScore >= score) continue;
                score = candidateScore; best = candidate; bestDirection = candidateDirection;
            }
            route[i] = best;
            direction = Vector3.Slerp(direction, bestDirection, 0.5f).normalized;
            tangents[i - 1] = direction;
        }
        tangents[count - 1] = tangents[count - 2];
    }

    public Vector3 RoutePoint(float along, float across = 0f)
    {
        float sample = Mathf.Clamp(along / routeStep, 0f, route.Length - 1.001f);
        int i = Mathf.FloorToInt(sample);
        var point = Vector3.Lerp(route[i], route[i + 1], sample - i);
        var direction = Vector3.Lerp(tangents[i], tangents[i + 1], sample - i).normalized;
        if (along < 0f) point += tangents[0] * along;
        if (along > zone.runoutDistance) point += tangents[tangents.Length - 1] * (along - zone.runoutDistance);
        point += Vector3.Cross(Vector3.up, direction) * across;
        return ClampToTerrain(point);
    }

    private float RandomRange(System.Random rng, float a, float b) => Mathf.Lerp(a, b, (float)rng.NextDouble());
    private void BuildFragments()
    {
        var rng = new System.Random(1397);
        int nx = Mathf.Clamp(Mathf.CeilToInt(zone.size.x / settings.blockSize), 5, 18);
        int nz = Mathf.Clamp(Mathf.CeilToInt(zone.size.y / settings.blockSize), 6, 20);
        float sx = zone.size.x / nx, sz = zone.size.y / nz;
        for (int z = 0; z < nz; z++)
        for (int x = 0; x < nx; x++)
        {
            float across = (x + 0.5f - nx * 0.5f) * sx;
            float along = (z + 0.5f - nz * 0.5f) * sz;
            if (Mathf.Pow(across / (zone.size.x * 0.5f), 2f) + Mathf.Pow(along / (zone.size.y * 0.5f), 2f) > 1f) continue;
            var rest = RoutePoint(along, across);
            var mesh = SlabMesh(rest, across, along, sx * 0.97f, sz * 0.97f, rng);
            var piece = MakeMeshObject("Fractured slope slab", mesh, rockMaterial);
            piece.transform.position = rest;
            fragments.Add(new Fragment { transform = piece.transform, across = across, along = along, rest = rest,
                delay = Mathf.Lerp(settings.failureDuration * 0.65f, 0f, (along / zone.size.y) + 0.5f),
                distance = RandomRange(rng, 0.74f, 1.04f) * zone.runoutDistance,
                size = sx, seed = RandomRange(rng, 0f, 100f), spin = RandomRange(rng, -28f, 28f), slab = true });
        }
        var boulderMesh = BoulderMesh(rng);
        owned.Add(boulderMesh);
        int count = Mathf.Clamp(settings.maxBlocks, 100, 420);
        for (int i = 0; i < count; i++)
        {
            float across = RandomRange(rng, -zone.size.x * 0.42f, zone.size.x * 0.42f);
            float along = RandomRange(rng, -zone.size.y * 0.35f, zone.size.y * 0.44f);
            float size = RandomRange(rng, 1.4f, 7.5f);
            if (i % 13 == 0) size *= 1.65f;
            var piece = MakeMeshObject("Transported boulder", boulderMesh, rockMaterial);
            piece.transform.localScale = new Vector3(size * RandomRange(rng, 0.7f, 1.3f), size * RandomRange(rng, 0.6f, 1.1f), size);
            var rotation = Quaternion.Euler(RandomRange(rng, 0f, 360f), RandomRange(rng, 0f, 360f), RandomRange(rng, 0f, 360f));
            fragments.Add(new Fragment { transform = piece.transform, across = across, along = along, rest = RoutePoint(along, across),
                delay = i < 18 ? -settings.precursoryDuration * RandomRange(rng, 0.05f, 0.6f) : RandomRange(rng, 0f, settings.failureDuration),
                distance = RandomRange(rng, 0.7f, 1.15f) * zone.runoutDistance,
                size = size, seed = RandomRange(rng, 0f, 100f), spin = RandomRange(rng, -140f, 140f), rotation = rotation });
        }
    }

    private GameObject MakeMeshObject(string label, Mesh mesh, Material mat)
    {
        var go = new GameObject(label);
        go.transform.SetParent(root, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = mat;
        renderer.shadowCastingMode = ShadowCastingMode.On;
        return go;
    }

    private Mesh SlabMesh(Vector3 rest, float across, float along, float width, float length, System.Random rng)
    {
        var corners = new Vector3[4];
        float[] dx = { -0.5f, -0.5f, 0.5f, 0.5f };
        float[] dz = { -0.5f, 0.5f, 0.5f, -0.5f };
        for (int i = 0; i < 4; i++)
            corners[i] = RoutePoint(along + dz[i] * length, across + dx[i] * width) - rest + Vector3.up * RandomRange(rng, 0.15f, 0.55f);
        var vertices = new List<Vector3>();
        var colors = new List<Color>();
        for (int i = 0; i < 4; i++)
        {
            int j = (i + 1) % 4;
            AddTriangle(vertices, colors, Vector3.up * 0.35f, corners[i], corners[j], new Color(0.95f, 0.98f, 0.90f));
            var lowI = corners[i] - Vector3.up * settings.blockDepth;
            var lowJ = corners[j] - Vector3.up * settings.blockDepth;
            AddTriangle(vertices, colors, corners[i], lowI, lowJ, new Color(0.72f, 0.70f, 0.67f));
            AddTriangle(vertices, colors, corners[i], lowJ, corners[j], new Color(0.85f, 0.82f, 0.77f));
        }
        Mesh mesh = FinishMesh("Terrain conforming fractured slab", vertices, colors);
        owned.Add(mesh);
        return mesh;
    }
    private Mesh BoulderMesh(System.Random rng)
    {
        var ring = new Vector3[8];
        for (int i = 0; i < ring.Length; i++)
        {
            float angle = i * Mathf.PI * 0.25f;
            float r = RandomRange(rng, 0.35f, 0.6f);
            ring[i] = new Vector3(Mathf.Cos(angle) * r, RandomRange(rng, -0.10f, 0.10f), Mathf.Sin(angle) * r);
        }
        var vertices = new List<Vector3>(); var colors = new List<Color>();
        for (int i = 0; i < 8; i++)
        {
            AddTriangle(vertices, colors, new Vector3(0.09f, 0.53f, 0.05f), ring[(i + 1) % 8], ring[i], Color.white * RandomRange(rng, 0.78f, 1f));
            AddTriangle(vertices, colors, new Vector3(-0.08f, -0.43f, 0f), ring[i], ring[(i + 1) % 8], Color.white * RandomRange(rng, 0.68f, 0.9f));
        }
        return FinishMesh("Angular mountain rock", vertices, colors);
    }
    private void AddTriangle(List<Vector3> verts, List<Color> colors, Vector3 a, Vector3 b, Vector3 c, Color color)
    {
        verts.Add(a); verts.Add(b); verts.Add(c);
        color.a = 1f;
        colors.Add(color); colors.Add(color); colors.Add(color);
    }
    private Mesh FinishMesh(string name, List<Vector3> vertices, List<Color> colors)
    {
        var mesh = new Mesh { name = name };
        mesh.SetVertices(vertices); mesh.SetColors(colors);
        var indices = new int[vertices.Count]; for (int i = 0; i < indices.Length; i++) indices[i] = i;
        mesh.triangles = indices; mesh.RecalculateNormals(); mesh.RecalculateBounds();
        return mesh;
    }

    private void BuildSurface()
    {
        int count = (Rows + 1) * (Columns + 1);
        surfaceVertices = new Vector3[count]; surfaceGround = new Vector3[count];
        surfaceColors = new Color[count]; surfaceAlong = new float[count];
        for (int r = 0; r <= Rows; r++)
        {
            float along = Mathf.Lerp(-zone.size.y * 0.5f, zone.runoutDistance + zone.size.y * 0.5f, r / (float)Rows);
            float width = along < zone.size.y * 0.4f ? zone.size.x * Mathf.Sqrt(Mathf.Clamp01(1f - Mathf.Pow(along / (zone.size.y * 0.55f), 2f))) : zone.size.x * Mathf.Lerp(0.58f, 1.9f, Mathf.SmoothStep(0f, 1f, along / zone.runoutDistance));
            if (along > zone.runoutDistance * 0.92f)
            {
                float toe = (along - zone.runoutDistance * 0.92f) / (zone.runoutDistance * 0.08f + zone.size.y * 0.5f);
                width *= Mathf.Sqrt(Mathf.Clamp01(1f - toe * toe));
            }
            for (int c = 0; c <= Columns; c++)
            {
                int i = r * (Columns + 1) + c;
                float across = (c / (float)Columns - 0.5f) * width;
                surfaceGround[i] = RoutePoint(along, across);
                surfaceAlong[i] = along;
                surfaceVertices[i] = surfaceGround[i];
            }
        }
        var indices = new int[Rows * Columns * 6]; int n = 0;
        for (int r = 0; r < Rows; r++) for (int c = 0; c < Columns; c++)
        {
            int i = r * (Columns + 1) + c;
            indices[n++] = i; indices[n++] = i + Columns + 1; indices[n++] = i + 1;
            indices[n++] = i + 1; indices[n++] = i + Columns + 1; indices[n++] = i + Columns + 2;
        }
        surface = new Mesh { name = "Moving granular surge and final deposit" };
        surface.vertices = surfaceVertices; surface.colors = surfaceColors; surface.triangles = indices;
        surface.MarkDynamic(); surface.RecalculateNormals();
        owned.Add(surface);
        var surfaceObject = MakeMeshObject("Granular surge, exposed scar and deposition fan", surface, earthMaterial);
        // Surface vertices are terrain samples in world coordinates.
        surfaceObject.transform.position = Vector3.zero;
        surfaceObject.transform.rotation = Quaternion.identity;
    }

    private void BuildCracks()
    {
        for (int line = 0; line < 3; line++)
        {
            var go = new GameObject("Growing tension fracture"); go.transform.SetParent(root, false);
            var lr = go.AddComponent<LineRenderer>(); lr.sharedMaterial = crackMaterial;
            lr.positionCount = 41; lr.useWorldSpace = true;
            for (int i = 0; i <= 40; i++)
            {
                float t = i / 40f;
                float a = line == 0 ? Mathf.Lerp(-zone.size.x * 0.45f, zone.size.x * 0.45f, t) : (line == 1 ? -1f : 1f) * zone.size.x * 0.44f;
                float b = line == 0 ? -zone.size.y * 0.35f + Mathf.Sin(t * Mathf.PI) * -10f : Mathf.Lerp(-zone.size.y * 0.35f, zone.size.y * 0.25f, t);
                var point = RoutePoint(b + Mathf.Sin(i * 1.37f + line) * 1.9f, a + Mathf.Cos(i * 0.93f) * 1.5f);
                lr.SetPosition(i, point + Vector3.up * 0.18f);
            }
            lr.numCornerVertices = 1; lr.startWidth = lr.endWidth = 0f;
            lr.shadowCastingMode = ShadowCastingMode.Off;
            cracks.Add(lr);
        }
    }

    private float Travel(float age, float duration)
    {
        // Acceleration followed by deceleration; exact endpoints permit reproducible replay.
        float p = Mathf.Clamp01(age / Mathf.Max(1f, duration));
        return p * p * (3f - 2f * p);
    }
    public void Evaluate(float time)
    {
        if (!IsBuilt) return;
        float failure = time - settings.precursoryDuration;
        float precursor = Mathf.Clamp01(time / Mathf.Max(1f, settings.precursoryDuration));
        float travel = Travel(Mathf.Max(0f, failure), settings.failureDuration + settings.runoutDuration);
        float settling = Mathf.Clamp01((failure - settings.failureDuration - settings.runoutDuration) / Mathf.Max(1f, settings.settlingDuration));
        FrontPosition = RoutePoint(zone.runoutDistance * travel) + Vector3.up * 8f;
        foreach (var f in fragments)
        {
            float age = failure - f.delay;
            bool visible = f.slab ? failure >= 0f : age >= 0f;
            f.transform.gameObject.SetActive(visible);
            if (!visible) continue;
            float p = Travel(age, settings.runoutDuration + settings.failureDuration * 0.5f);
            float along = f.along + f.distance * p;
            float across = f.across * (1f + p * 0.8f) + Mathf.Sin(f.seed) * zone.size.x * 0.16f * p;
            Vector3 point = age <= 0f ? f.rest : RoutePoint(along, across);
            float bump = age <= 0f ? precursor * precursor * 0.20f : Mathf.Abs(Mathf.Sin(age * (f.slab ? 0.9f : 2.7f) + f.seed)) * (f.slab ? 1.6f : 3.4f) * Mathf.Sin(Mathf.PI * p);
            float elevation = f.slab ? settings.blockDepth * Mathf.Clamp01(age / 3f) * 0.55f : f.size * 0.47f;
            float deposit = settings.depositHeight * Mathf.Pow(Mathf.Sin(Mathf.PI * Mathf.Clamp01((along - zone.runoutDistance * 0.6f) / (zone.runoutDistance * 0.65f))), 2f) * p;
            f.transform.position = point + Vector3.up * (bump + elevation + deposit);
            float angle = Mathf.Max(0f, age) * f.spin * (1f - p * 0.75f);
            f.transform.rotation = age < 0f ? Quaternion.identity : (f.slab ? Quaternion.identity : f.rotation) * Quaternion.Euler(angle * 0.35f, angle * 0.2f, angle);
        }
        foreach (var crack in cracks)
        {
            crack.enabled = time > 0.01f && failure < settings.failureDuration;
            crack.startWidth = crack.endWidth = 0.07f + precursor * precursor * 1.6f;
        }
        if (time >= nextSurfaceTime || time < lastTime || !Application.isPlaying)
        {
            UpdateSurface(failure, travel);
            nextSurfaceTime = time + 1f / Mathf.Min(12f, settings.heightmapUpdatesPerSecond);
        }
        if (dust != null && settings.dustEnabled)
        {
            float intensity = failure < 0f ? precursor * 0.15f : Mathf.Lerp(0.45f, 0.9f, Mathf.Sin(Mathf.PI * travel)) * (1f - settling);
            dust.SetIntensity(intensity);
            dust.SetSourcePosition(FrontPosition);
        }
        lastTime = time;
    }

    private void UpdateSurface(float failure, float travel)
    {
        float front = zone.runoutDistance * travel + zone.size.y * 0.46f;
        float carve = Mathf.Clamp01(failure / settings.failureDuration);
        for (int r = 0; r <= Rows; r++) for (int c = 0; c <= Columns; c++)
        {
            int i = r * (Columns + 1) + c;
            float along = surfaceAlong[i];
            float edge = Mathf.Pow(Mathf.Max(0f, Mathf.Sin(c / (float)Columns * Mathf.PI)), 0.6f);
            float lateral = Mathf.Abs(c / (float)Columns * 2f - 1f);
            float localFront = front - zone.size.y * 0.45f * lateral * lateral * (1f - travel);
            float envelope = Mathf.Exp(-Mathf.Pow((along - localFront + 40f) / 70f, 2f));
            float deposit = Mathf.Exp(-Mathf.Pow((along - zone.runoutDistance * 0.94f) / (zone.size.y * 0.60f), 2f)) * settings.depositHeight * travel * travel;
            float trail = along > zone.size.y * 0.3f ? 0.40f : 0.16f;
            float thickness = (envelope * settings.blockDepth * 0.8f * Mathf.Sin(Mathf.PI * travel) + deposit + trail) * edge;
            float noise = (Mathf.PerlinNoise(surfaceGround[i].x * 0.15f, surfaceGround[i].z * 0.15f) - 0.5f) * 0.55f * edge;
            surfaceVertices[i] = surfaceGround[i] + Vector3.up * (0.60f + Mathf.Max(0f, thickness + noise));
            float reveal = Mathf.Clamp01((localFront - along) / 12f) * carve * Mathf.Clamp01(edge * 8f);
            float tint = 0.82f + Mathf.PerlinNoise(i * 0.17f, r * 0.31f) * 0.36f;
            surfaceColors[i] = new Color(tint, tint, tint, reveal);
        }
        surface.vertices = surfaceVertices; surface.colors = surfaceColors;
        surface.RecalculateNormals(); surface.RecalculateBounds();
    }
    public void SimulateDust(float seconds)
    {
        if (dust != null) dust.SimulatePreview(seconds);
    }
    public void SetPaused(bool value) { if (dust != null) dust.SetPaused(value); }
    public void Clear()
    {
        if (root != null) Dispose(root.gameObject);
        foreach (var item in owned) if (item != null) Dispose(item);
        owned.Clear(); fragments.Clear(); cracks.Clear();
        root = null; dust = null;
    }
    private void Dispose(UnityEngine.Object item)
    {
        if (Application.isPlaying) Destroy(item); else DestroyImmediate(item);
    }
    private void OnDestroy() => Clear();
}
