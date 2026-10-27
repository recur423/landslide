using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Low-opacity sediment mist and rock dust, separate from original weather VFX.</summary>
public class LandslideDustCloud : MonoBehaviour
{
    private Transform plumeAnchor;
    private Material dustMaterial, runtimeMaterial;
    private Texture2D texture;
    private ParticleSystem plume, runout;
    private float intensityScale = 1f;
    public bool IsBuilt => plume != null;
    public float Intensity { get; private set; }
    public Material DustMaterial => dustMaterial != null ? dustMaterial : runtimeMaterial;
    public void SetPlumeAnchor(Transform value) => plumeAnchor = value;
    public void SetIntensityScale(float value) => intensityScale = Mathf.Clamp(value, 0f, 2f);
    public void SetMaterial(Material value) => dustMaterial = value;
    public void Build()
    {
        if (IsBuilt) return;
        if (dustMaterial == null)
        {
            var template = Resources.Load<Material>("LandslideLateStage/Dust");
            runtimeMaterial = template != null ? new Material(template) : CreateDustMaterial(Color.white);
            texture = CreateSoftParticleTexture(96, Color.white);
            runtimeMaterial.mainTexture = texture;
        }
        plume = CreateSystem("Secondary slope mist", 700, 36f, 11f, 3.2f, 27f);
        runout = CreateSystem("Travelling rock dust", 800, 50f, 9f, 1.4f, 32f);
        if (plumeAnchor != null) plume.transform.position = plumeAnchor.position;
        SetIntensity(0f);
    }
    private ParticleSystem CreateSystem(string label, int budget, float radius, float lifetime, float speed, float size)
    {
        var go = new GameObject(label); go.transform.SetParent(transform, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main;
        main.loop = true; main.playOnAwake = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(lifetime * 0.5f, lifetime);
        main.startSpeed = new ParticleSystem.MinMaxCurve(speed * 0.2f, speed);
        main.startSize = new ParticleSystem.MinMaxCurve(size * 0.35f, size);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.51f, 0.46f, 0.38f, 0.14f), new Color(0.73f, 0.68f, 0.57f, 0.22f));
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        main.maxParticles = budget; main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.useUnscaledTime = false;
        var shape = ps.shape; shape.shapeType = ParticleSystemShapeType.Box; shape.scale = new Vector3(radius * 2f, 5f, radius);
        var velocity = ps.velocityOverLifetime; velocity.enabled = true; velocity.space = ParticleSystemSimulationSpace.World;
        velocity.y = new ParticleSystem.MinMaxCurve(1f, 3f);
        var sizeLife = ps.sizeOverLifetime; sizeLife.enabled = true;
        sizeLife.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.4f, 1f, 1.7f));
        var colorLife = ps.colorOverLifetime; colorLife.enabled = true;
        var gradient = new Gradient();
        gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.7f, 0.15f), new GradientAlphaKey(0.35f, 0.65f), new GradientAlphaKey(0f, 1f) });
        colorLife.color = gradient;
        var emission = ps.emission; emission.rateOverTime = 0f;
        var renderer = ps.GetComponent<ParticleSystemRenderer>(); renderer.sharedMaterial = DustMaterial;
        renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
        if (Application.isPlaying) ps.Play();
        return ps;
    }
    public void SetIntensity(float value)
    {
        Intensity = Mathf.Clamp01(value) * intensityScale;
        if (!IsBuilt) return;
        var p = plume.emission; p.rateOverTime = 48f * Intensity;
        var r = runout.emission; r.rateOverTime = 82f * Intensity;
        // Zero emission lets existing particles fade, rather than disappearing abruptly.
    }
    public void Burst(int count) { if (IsBuilt) plume.Emit(Mathf.Clamp(count, 1, 500)); }
    public void SetPaused(bool value)
    {
        if (!IsBuilt) return;
        var a = plume.main; a.simulationSpeed = value ? 0f : 1f;
        var b = runout.main; b.simulationSpeed = value ? 0f : 1f;
    }
    public void SetSourcePosition(Vector3 value) { if (runout != null) runout.transform.position = value; }
    public void SetPlumePosition(Vector3 value) { if (plume != null) plume.transform.position = value; }
    public void SimulatePreview(float seconds)
    {
        if (!IsBuilt) return;
        plume.Simulate(seconds, true, true, true);
        runout.Simulate(seconds, true, true, true);
    }
    public static Material CreateDustMaterial(Color color)
    {
        var shader = Shader.Find("Hunnedalen/LateStageDust") ?? Shader.Find("Universal Render Pipeline/Particles/Unlit") ?? Shader.Find("Particles/Standard Unlit");
        var mat = new Material(shader) { name = "Secondary sediment mist" };
        mat.SetColor("_BaseColor", color);
        mat.renderQueue = (int)RenderQueue.Transparent;
        return mat;
    }
    public static Texture2D CreateSoftParticleTexture(int size, Color tint)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            float u = x / (float)(size - 1) * 2f - 1f, v = y / (float)(size - 1) * 2f - 1f;
            float a = Mathf.Pow(Mathf.Clamp01(1f - Mathf.Sqrt(u * u + v * v)), 1.8f);
            float noise = 0.5f + 0.5f * Mathf.PerlinNoise(x * 0.11f, y * 0.11f);
            pixels[y * size + x] = new Color(tint.r, tint.g, tint.b, a * noise);
        }
        tex.SetPixels(pixels); tex.Apply(); return tex;
    }
    private void OnDestroy()
    {
        if (runtimeMaterial != null) { if (Application.isPlaying) Destroy(runtimeMaterial); else DestroyImmediate(runtimeMaterial); }
        if (texture != null) { if (Application.isPlaying) Destroy(texture); else DestroyImmediate(texture); }
    }
}
