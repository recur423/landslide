using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Represents the unstable rock mass on the hillside.
///
/// Every child of this object that owns a collider is treated as one block of the slope.
/// While the slope is still intact the blocks are kinematic; when the slope fails they are
/// released and handed over to the physics engine, so the mass really topples, slides and
/// grinds its way down the mountain instead of only being a shader animation.
/// </summary>
[DisallowMultipleComponent]
public class LandslideSlopeMass : MonoBehaviour
{
    [SerializeField] private LandslideReleaseZone zone;

    [Header("Physics")]
    [SerializeField] private float blockMass = 4000f;
    [SerializeField] private float detachmentSpeed = 2.5f;
    [SerializeField] private float detachmentSpin = 25f;
    [SerializeField] private float drag = 0.02f;
    [SerializeField] private float angularDrag = 0.5f;
    [Tooltip("Extra downhill acceleration applied while the mass is travelling (m/s^2). Physically this is the slope-parallel gravity component the fragmented mass still feels.")]
    [SerializeField] private float runoutAcceleration = 4f;

    [Header("Precursor")]
    [Tooltip("Maximum jitter applied to the blocks while the slope is cracking (meters).")]
    [SerializeField] private float precursorJitter = 0.25f;

    [SerializeField] private bool logEvents = true;

    private readonly List<Rigidbody> blocks = new List<Rigidbody>();
    private readonly List<Vector3> restingPositions = new List<Vector3>();
    private readonly List<float> volumes = new List<float>();

    private bool prepared;
    private bool detached;

    public int BlockCount => blocks.Count;
    public bool IsPrepared => prepared;
    public bool HasDetached => detached;

    /// <summary>World-space centre of the mass (used to position the dust cloud and the shake source).</summary>
    public Vector3 Centroid
    {
        get
        {
            if (blocks.Count == 0) return zone != null ? zone.Center : transform.position;

            var sum = Vector3.zero;
            var count = 0;
            foreach (var b in blocks)
            {
                if (b == null) continue;
                sum += b.worldCenterOfMass;
                count++;
            }
            return count > 0 ? sum / count : transform.position;
        }
    }

    public void SetZone(LandslideReleaseZone releaseZone) => zone = releaseZone;

    /// <summary>
    /// Collects all blocks and prepares them for the failure: they stay kinematic
    /// (i.e. glued to the hillside) but every rigidbody is already created so that the
    /// detachment later on does not cause a frame spike.
    /// </summary>
    public void Prepare()
    {
        if (prepared) return;

        blocks.Clear();
        restingPositions.Clear();
        volumes.Clear();

        var colliders = GetComponentsInChildren<Collider>(includeInactive: true);
        foreach (var col in colliders)
        {
            if (col == null) continue;

            var body = col.GetComponent<Rigidbody>();
            if (body == null) body = col.gameObject.AddComponent<Rigidbody>();

            body.mass = blockMass;
            body.isKinematic = true;
            body.useGravity = false;
            body.drag = drag;
            body.angularDrag = angularDrag;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            body.maxAngularVelocity = 4f;
            body.sleepThreshold = 0.25f;

            blocks.Add(body);
            restingPositions.Add(body.transform.localPosition);
            volumes.Add(EstimateVolume(col));
        }

        prepared = true;

        if (logEvents)
            Debug.Log($"[Landslide] Slope mass prepared with {blocks.Count} blocks " +
                      $"({TotalVolume():N0} m3 estimated volume).");
    }

    /// <summary>
    /// Animates the slow "cracking" of the hillside before the failure:
    /// the blocks shiver slightly and the whole mass creeps downhill.
    /// </summary>
    public void ApplyPrecursory(float progress01)
    {
        if (!prepared || detached) return;

        var strength = Mathf.Clamp01(progress01);
        var jitter = precursorJitter * strength;
        var creep = zone != null ? zone.SlideDirection * (0.2f * strength * strength) : Vector3.zero;

        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (b == null) continue;

            var offset = new Vector3(
                Mathf.Sin(Time.time * (1.7f + i % 7) + i) * jitter,
                0f,
                Mathf.Sin(Time.time * (1.3f + i % 5) + i * 0.7f) * jitter);

            b.transform.localPosition = restingPositions[i] + offset + creep;
        }
    }

    /// <summary>
    /// Releases a couple of individual blocks - the rockfall that announces the failure.
    /// </summary>
    public int ReleaseSpalling(int count)
    {
        if (!prepared) return 0;

        var released = 0;
        for (int i = blocks.Count - 1; i >= 0 && released < count; i--)
        {
            var b = blocks[i];
            if (b == null || !b.isKinematic) continue;

            Detach(b, 0.35f);
            released++;
        }

        if (released > 0 && logEvents)
            Debug.Log($"[Landslide] {released} blocks spalled off the slope.");

        return released;
    }

    /// <summary>
    /// The main failure: all remaining blocks are released over the given duration.
    /// </summary>
    public IEnumerator Detach(float duration)
    {
        if (!prepared)
            Prepare();

        var remaining = new List<Rigidbody>();
        foreach (var b in blocks)
            if (b != null && b.isKinematic)
                remaining.Add(b);

        if (remaining.Count == 0)
            yield break;

        detached = true;

        // Release the blocks bottom-up (downhill first), like a retrogressive failure,
        // and spread the release over the detach duration.
        remaining.Sort((a, b) => DepthAlongSlide(b).CompareTo(DepthAlongSlide(a)));

        var spread = Mathf.Max(0.01f, duration);
        var perBlock = spread / remaining.Count;

        foreach (var b in remaining)
        {
            if (b == null) continue;
            Detach(b, 1f);
            if (perBlock > 0.01f)
                yield return new WaitForSeconds(perBlock);
        }

        if (logEvents)
            Debug.Log($"[Landslide] Slope failure complete - {remaining.Count} blocks detached.");
    }

    /// <summary>
    /// Keeps pushing the debris down the slope while it is travelling.
    /// Call this every frame during the run-out phase.
    /// </summary>
    public void ApplyRunout(float progress01)
    {
        if (!prepared) return;

        var dir = zone != null ? zone.SlideDirection : transform.forward;
        var flattening = Mathf.Clamp01(progress01);

        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (b == null || b.isKinematic) continue;

            b.AddForce(dir * runoutAcceleration, ForceMode.Acceleration);

            // Once the debris reaches the flatter part of the valley it is damped so that
            // it comes to rest instead of sliding on forever.
            b.drag = Mathf.Lerp(drag, drag + 1.5f, flattening);
            b.angularDrag = Mathf.Lerp(angularDrag, angularDrag + 2f, flattening);
        }
    }

    /// <summary>
    /// Lets the debris settle down: slow blocks are frozen so that a large pile of
    /// rocks does not keep the physics engine busy forever (important for VR).
    /// </summary>
    public void Settle(float progress01)
    {
        if (!prepared) return;

        var freezeThreshold = Mathf.Lerp(0.05f, 0.6f, Mathf.Clamp01(progress01));

        foreach (var b in blocks)
        {
            if (b == null || b.isKinematic) continue;

            if (b.velocity.magnitude < freezeThreshold && b.angularVelocity.magnitude < 1f)
            {
                b.velocity = Vector3.zero;
                b.angularVelocity = Vector3.zero;
                b.isKinematic = true;
            }
        }
    }

    /// <summary>Freezes whatever is still moving. Called when the event has finished.</summary>
    public void FreezeAll()
    {
        foreach (var b in blocks)
        {
            if (b == null || b.isKinematic) continue;
            b.isKinematic = true;
        }
    }

    /// <summary>Puts the slope back the way it was (used by the editor preview).</summary>
    public void ResetMass()
    {
        detached = false;

        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (b == null) continue;

            b.isKinematic = true;
            b.useGravity = false;
            b.drag = drag;
            b.angularDrag = angularDrag;
            b.velocity = Vector3.zero;
            b.angularVelocity = Vector3.zero;
            b.transform.localPosition = restingPositions[i];
            b.transform.localRotation = Quaternion.identity;
        }
    }

    /// <summary>Number of blocks that have not been released yet.</summary>
    public int IntactBlockCount
    {
        get
        {
            var n = 0;
            foreach (var b in blocks)
                if (b != null && b.isKinematic) n++;
            return n;
        }
    }

    private void Detach(Rigidbody body, float strength)
    {
        var dir = zone != null ? zone.SlideVector.normalized : Vector3.down;
        var speed = detachmentSpeed * strength * Random.Range(0.6f, 1.4f);

        body.isKinematic = false;
        body.useGravity = true;
        body.drag = drag;
        body.angularDrag = angularDrag;
        body.WakeUp();

        body.velocity = dir * speed + Random.insideUnitSphere * (speed * 0.15f);

        if (detachmentSpin > 0f)
            body.angularVelocity = Random.insideUnitSphere * (detachmentSpin * Mathf.Deg2Rad * strength);
    }

    private float DepthAlongSlide(Rigidbody body)
    {
        if (zone == null) return body.transform.position.z;
        return zone.WorldToLocal(body.transform.position).y;
    }

    private static float EstimateVolume(Collider col)
    {
        var s = col.bounds.size;
        return Mathf.Max(0.01f, s.x * s.y * s.z);
    }

    private float TotalVolume()
    {
        var total = 0f;
        foreach (var v in volumes) total += v;
        return total;
    }
}

