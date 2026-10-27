using UnityEngine;

/// <summary>
/// Marker that describes where the hillside fails and where the debris travels.
///
/// The transform position is the centre of the release area (top of the failure surface).
/// x = across the slope, z = along the slide direction (downhill).
///
/// Every landslide component (<see cref="LandslideSlopeMass"/>,
/// <see cref="LandslideTerrainScar"/>, <see cref="LandslideDustCloud"/>,
/// <see cref="LandslideEvent"/>) reads its geometry from this single component, so the
/// whole event can be re-positioned by moving one object.
/// </summary>
[DisallowMultipleComponent]
public class LandslideReleaseZone : MonoBehaviour
{
    [Tooltip("Extent of the release area in meters. x = across the slope, y = along the slope (downhill).")]
    public Vector2 size = new Vector2(55f, 75f);

    [Tooltip("Downhill direction of the slide in degrees (yaw). 0 = +Z, 90 = +X.")]
    public float slideDirectionDegrees = 0f;

    [Tooltip("Horizontal distance the debris travels before it comes to rest (meters).")]
    public float runoutDistance = 260f;

    [Tooltip("Average steepness of the slope that fails (degrees). Purely informational - used for logging and the mass estimate.")]
    public float slopeAngleDegrees = 30f;

    [Tooltip("True when the geometry was derived from the simulation data instead of a manual/terrain based estimate.")]
    public bool estimatedFromSimulation;

    /// <summary>World-space centre of the release area.</summary>
    public Vector3 Center => transform.position;

    /// <summary>Normalised, horizontal downhill direction of the slide.</summary>
    public Vector3 SlideDirection
    {
        get
        {
            var rad = slideDirectionDegrees * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
        }
    }

    /// <summary>Horizontal direction across the slope (right-handed with <see cref="SlideDirection"/>).</summary>
    public Vector3 AcrossDirection => Vector3.Cross(Vector3.up, SlideDirection).normalized;

    /// <summary>Downhill direction including the average slope angle.</summary>
    public Vector3 SlideVector => Quaternion.AngleAxis(slopeAngleDegrees, AcrossDirection) * SlideDirection;

    /// <summary>Where the debris ends up (toe of the slope).</summary>
    public Vector3 DepositCenter => Center + SlideDirection * runoutDistance;

    /// <summary>
    /// Fills the zone with a freshly computed estimate.
    /// </summary>
    public void SetEstimate(Vector3 center, Vector2 zoneSize, Vector3 slideDirection, float runout, float slopeAngle, bool fromSimulation)
    {
        transform.position = center;
        size = new Vector2(Mathf.Max(5f, zoneSize.x), Mathf.Max(5f, zoneSize.y));
        slideDirectionDegrees = Mathf.Atan2(slideDirection.x, slideDirection.z) * Mathf.Rad2Deg;
        runoutDistance = Mathf.Max(10f, runout);
        slopeAngleDegrees = slopeAngle;
        estimatedFromSimulation = fromSimulation;
    }

    /// <summary>
    /// Converts a position expressed in the local frame of the zone
    /// (x across the slope, z downhill) into world-space.
    /// </summary>
    public Vector3 LocalToWorld(float across, float along)
        => Center + AcrossDirection * across + SlideDirection * along;

    /// <summary>
    /// Converts a world position into the local frame of the zone.
    /// </summary>
    public Vector2 WorldToLocal(Vector3 world)
    {
        var offset = world - Center;
        return new Vector2(Vector3.Dot(offset, AcrossDirection), Vector3.Dot(offset, SlideDirection));
    }

    private void OnDrawGizmosSelected()
    {
        var across = AcrossDirection;
        var dir = SlideDirection;

        // Release area (red box)
        Gizmos.color = new Color(0.9f, 0.2f, 0.1f, 0.9f);
        DrawBox(Center, across, dir, size.x, size.y);

        // Run-out path (yellow line) and deposit area (orange box)
        Gizmos.color = new Color(1f, 0.85f, 0.1f, 0.9f);
        Gizmos.DrawLine(Center, DepositCenter);
        Gizmos.color = new Color(0.95f, 0.55f, 0.1f, 0.9f);
        DrawBox(DepositCenter, across, dir, size.x * 1.4f, size.y * 1.6f);

        // Downhill arrow
        Gizmos.color = Color.cyan;
        Gizmos.DrawRay(Center, SlideVector.normalized * Mathf.Max(20f, size.y * 0.5f));
    }

    private static void DrawBox(Vector3 center, Vector3 across, Vector3 along, float width, float length)
    {
        var a = across * (width * 0.5f);
        var b = along * (length * 0.5f);

        var p0 = center - a - b;
        var p1 = center + a - b;
        var p2 = center + a + b;
        var p3 = center - a + b;

        Gizmos.DrawLine(p0, p1);
        Gizmos.DrawLine(p1, p2);
        Gizmos.DrawLine(p2, p3);
        Gizmos.DrawLine(p3, p0);
    }
}
