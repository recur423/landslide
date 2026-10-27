using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// Trauma based camera shake for the landslide.
///
/// The effect is driven by a single "trauma" value in the range 0..1 that decays over
/// time, so a strong shake (the moment the slope fails) smoothly dies down into a faint
/// rumble. In VR the player rig is rotated instead of the head-tracked camera and only
/// the rig is translated - translating the head in VR is a fast way to make people sick.
/// </summary>
[DisallowMultipleComponent]
public class LandslideCameraShake : MonoBehaviour
{
    [Tooltip("Transform that is shaken. When left empty the player camera is located at runtime.")]
    [SerializeField] private Transform shakeTarget;
    [SerializeField] private bool autoFindPlayerCamera = true;
    [Tooltip("Rotate (instead of translate) the camera in VR and translate the rig it is attached to.")]
    [SerializeField] private bool shakeRigInVR = true;

    [Header("Strength")]
    [Tooltip("Maximum translation of the shake (meters).")]
    [SerializeField] private float maxTranslation = 0.22f;
    [Tooltip("Maximum rotation of the shake (degrees).")]
    [SerializeField] private float maxRotation = 0.65f;
    [Tooltip("How fast the shake oscillates.")]
    [SerializeField] private float frequency = 8f;
    [Tooltip("Trauma lost per second.")]
    [SerializeField] private float traumaDecay = 0.32f;
    [Tooltip("Overall multiplier coming from the landslide settings.")]
    [SerializeField] private float intensityScale = 1f;

    [Header("Falloff")]
    [Tooltip("Distance from the source at which the shake has faded away completely (meters).")]
    [SerializeField] private float maxDistance = 900f;
    [Tooltip("Within this distance from the source the shake is at full strength (meters).")]
    [SerializeField] private float fullStrengthDistance = 120f;

    [SerializeField] private bool logEvents = true;

    private float trauma;
    private Vector3 lastOffset;
    private Vector3 lastRotationOffset;
    private Vector3 noiseSeed;

    /// <summary>World space position the shake originates from (the failing slope).</summary>
    public Vector3 Source { get; set; }

    public float Trauma => trauma;
    public Transform Target => shakeTarget;

    public void SetIntensityScale(float scale) => intensityScale = Mathf.Clamp(scale, 0f, 3f);
    public void SetTarget(Transform target) => shakeTarget = target;
    public void SetSource(Vector3 source) => Source = source;

    /// <summary>Adds trauma (0..1). Values above 1 are clamped.</summary>
    public void AddTrauma(float amount)
    {
        trauma = Mathf.Clamp01(trauma + amount);
    }

    /// <summary>Sets the trauma directly (used for the sustained rumble during the run-out).</summary>
    public void SetTrauma(float value)
    {
        trauma = Mathf.Clamp01(value);
    }

    /// <summary>Removes the shake and puts the target back where it was.</summary>
    public void StopShake()
    {
        trauma = 0f;
        Restore();
    }

    private void Awake()
    {
        noiseSeed = new Vector3(Random.value * 100f, Random.value * 100f, Random.value * 100f);
        Source = transform.position;
    }

    private void OnDisable() => Restore();

    private void LateUpdate()
    {
        if (!ResolveTarget())
            return;

        trauma = Mathf.Max(0f, trauma - traumaDecay * Time.deltaTime);

        if (trauma <= 0.0001f)
        {
            Restore();
            return;
        }

        var attenuation = DistanceAttenuation();
        if (attenuation <= 0.0001f)
        {
            Restore();
            return;
        }

        // Trauma squared gives a nicer falloff than the raw value.
        var shake = trauma * trauma * Mathf.Clamp(intensityScale, 0f, 3f) * attenuation;
        var t = Time.time * Mathf.Max(0.1f, frequency);

        var noise = new Vector3(
            Noise(t, 0),
            Noise(t, 1),
            Noise(t, 2));

        var rotationOnly = shakeRigInVR && XRSettings.isDeviceActive && IsHeadTracked;

        var translation = rotationOnly
            ? Vector3.zero
            : new Vector3(noise.x, noise.y, noise.z) * (maxTranslation * shake);
        var rotation = new Vector3(noise.y, noise.x, noise.z) * (maxRotation * shake);

        ApplyOffset(translation, rotation);
    }

    /// <summary>True when the target transform is a head tracked camera (VR).</summary>
    private bool IsHeadTracked => shakeTarget != null && shakeTarget.GetComponent<Camera>() != null;

    private bool ResolveTarget()
    {
        if (shakeTarget != null) return true;
        if (!autoFindPlayerCamera) return false;

        var cam = Camera.main;
        if (cam == null)
        {
            var cams = FindObjectsOfType<Camera>();
            if (cams.Length > 0) cam = cams[0];
        }

        if (cam == null) return false;

        // In VR the controller/headset drives the camera transform every frame, so the
        // shake has to be applied to the rig the camera belongs to.
        if (shakeRigInVR && XRSettings.isDeviceActive && cam.transform.parent != null)
            shakeTarget = cam.transform.parent;
        else
            shakeTarget = cam.transform;

        if (logEvents)
            Debug.Log($"[Landslide] Camera shake target resolved to '{shakeTarget.name}'.");

        return shakeTarget != null;
    }

    private float DistanceAttenuation()
    {
        if (shakeTarget == null) return 0f;

        var distance = Vector3.Distance(shakeTarget.position, Source);
        if (maxDistance <= fullStrengthDistance)
            return 0f;

        return Mathf.Clamp01(1f - (distance - fullStrengthDistance) /
                                  (maxDistance - fullStrengthDistance));
    }

    /// <summary>
    /// Applies the shake as a delta to whatever the target does itself, so the effect does
    /// not fight with the player controller that owns the transform.
    /// </summary>
    private void ApplyOffset(Vector3 translation, Vector3 rotationDegrees)
    {
        var positionDelta = translation - lastOffset;
        var rotationDelta = rotationDegrees - lastRotationOffset;

        shakeTarget.localPosition += positionDelta;
        shakeTarget.localRotation *= Quaternion.Euler(rotationDelta);

        lastOffset = translation;
        lastRotationOffset = rotationDegrees;
    }

    private void Restore()
    {
        if (shakeTarget == null) return;

        if (lastOffset != Vector3.zero)
        {
            shakeTarget.localPosition -= lastOffset;
            lastOffset = Vector3.zero;
        }

        if (lastRotationOffset != Vector3.zero)
        {
            shakeTarget.localRotation *= Quaternion.Euler(-lastRotationOffset);
            lastRotationOffset = Vector3.zero;
        }
    }

    private float Noise(float t, int channel)
    {
        // Perlin noise is smooth, so the shake looks like a real ground vibration
        // instead of random jitter.
        return Mathf.PerlinNoise(t, noiseSeed[channel] + channel * 17.13f) * 2f - 1f;
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.6f, 0.1f, 0.6f);
        Gizmos.DrawWireSphere(Source, Mathf.Max(5f, fullStrengthDistance));
        Gizmos.color = new Color(1f, 0.3f, 0.1f, 0.25f);
        Gizmos.DrawWireSphere(Source, Mathf.Max(10f, maxDistance));
    }
}
