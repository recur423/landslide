using System;
using UnityEngine;

/// <summary>
/// Serializable configuration for the time-driven slope failure (landslide) feature.
/// Secondary slope failure begins after the full original RAMMS debris-flow timeline.
///
/// All units are metric (meters, seconds, kilograms).
/// </summary>
[Serializable]
public class LandslideSettings
{
    [Header("Timeline (seconds)")]
    [Tooltip("Legacy field retained for serialization; late-stage timing uses transitionDelay.")]
    public float triggerOffset = 0f;
    [Tooltip("Seconds after the unchanged RAMMS sequence before secondary instability appears.")]
    public float transitionDelay = 4f;
    [Tooltip("Time the hillside spends cracking / spalling small rocks before the main failure.")]
    public float precursoryDuration = 12f;
    [Tooltip("Time it takes for the unstable mass to fully detach from the hillside.")]
    public float failureDuration = 8f;
    [Tooltip("Time the detached mass spends travelling down the slope before coming to rest.")]
    public float runoutDuration = 38f;
    [Tooltip("Time the dust cloud takes to fade out once the mass has stopped.")]
    public float settlingDuration = 20f;

    [Header("Release zone")]
    [Tooltip("Derive the release zone and slide direction from the RAMMS simulation data (velocity fields). Falls back to a terrain based estimate when the data is not available in time.")]
    public bool useSimulationData = true;
    [Tooltip("Maximum time to wait for the simulation frames used to locate the release area.")]
    public float dataEstimateTimeout = 45f;
    [Tooltip("When enabled the release zone is placed manually instead of being estimated.")]
    public bool overrideReleaseZone = false;
    [Tooltip("World-space centre of the release area (only used when 'overrideReleaseZone' is enabled).")]
    public Vector3 releaseZoneCenter = Vector3.zero;
    [Tooltip("Extent of the release area in meters. x = across the slope, y = along the slope (downhill).")]
    public Vector2 releaseZoneSize = new Vector2(150f, 180f);
    [Tooltip("Downhill direction of the slide in degrees (yaw, 0 = +Z). Only used when 'overrideReleaseZone' is enabled.")]
    public float slideDirectionDegrees = 0f;
    [Tooltip("Horizontal distance the mass travels before the debris comes to rest.")]
    public float runoutDistance = 600f;
    [Tooltip("Restricts the automatic search of a release area to this radius around the centre below. 0 = search the whole terrain.")]
    public float searchRadius = 0f;
    [Tooltip("Centre of the automatic search area.")]
    public Vector3 searchCenter = Vector3.zero;
    [Tooltip("Minimum terrain steepness (degrees) that is considered for a release area.")]
    public float minSteepness = 30f;

    [Header("Slope mass")]
    [Tooltip("Edge length of a single block of the unstable mass (meters).")]
    public float blockSize = 12f;
    [Tooltip("Thickness of the unstable slab (meters).")]
    public float blockDepth = 6f;
    [Tooltip("Mass of a single block (kg).")]
    public float blockMass = 4000f;
    [Tooltip("Hard limit on the number of simulated blocks. The block spacing grows automatically to stay below this limit.")]
    public int maxBlocks = 260;
    [Tooltip("Initial downhill speed given to the blocks when the slope fails (m/s).")]
    public float detachmentSpeed = 2.5f;
    [Tooltip("Random spin applied to each block when it detaches (deg/s).")]
    public float detachmentSpin = 25f;
    [Tooltip("Air drag of the blocks (grows while the mass travels so the debris decelerates naturally).")]
    public float runoutDrag = 0.15f;
    [Tooltip("Fraction of the blocks that spall off during the precursory phase.")]
    [Range(0f, 0.25f)] public float precursorSpallFraction = 0.05f;

    [Header("Terrain deformation")]
    [Tooltip("Carve the release scar into the Unity Terrain and pile up the deposit at the toe of the slope.")]
    public bool deformTerrain = false;
    [Tooltip("Depth of the release scar (meters).")]
    public float scarDepth = 7f;
    [Tooltip("Height of the accumulation lobe at the toe of the slope (meters).")]
    public float depositHeight = 5f;
    [Tooltip("Amount of times the height-map of the terrain may be updated per second.")]
    public float heightmapUpdatesPerSecond = 8f;

    [Header("Presentation")]
    public bool dustEnabled = true;
    [Tooltip("Overall dust/smoke opacity multiplier.")]
    [Range(0f, 2f)] public float dustIntensity = 1f;
    public bool cameraShakeEnabled = false;
    [Tooltip("Overall strength of the ground-shaking.")]
    [Range(0f, 3f)] public float shakeIntensity = 1f;
    [Tooltip("Warning message shown when the slope starts to fail. Leave empty to keep the existing UI untouched.")]
    public string warningMessage = "LANDSLIDE DETECTED - EVACUATE THE VALLEY";
    public bool showWarningMessage = true;

    [Header("Audio")]
    [Tooltip("Volume of the continuous rumble played while the slope fails and the debris travels.")]
    [Range(0f, 1f)] public float rumbleVolume = 0.9f;

    /// <summary>A configuration with values tuned for the Hunnedalen scene.</summary>
    public static LandslideSettings Default() => new LandslideSettings();

    /// <summary>
    /// Clamps all values into a sane range so that hand-edited configurations
    /// (or values coming from the setup tool) can never break the simulation.
    /// </summary>
    public void Validate()
    {
        transitionDelay = Mathf.Max(0f, transitionDelay);
        precursoryDuration = Mathf.Max(0f, precursoryDuration);
        failureDuration = Mathf.Max(0.5f, failureDuration);
        runoutDuration = Mathf.Max(1f, runoutDuration);
        settlingDuration = Mathf.Max(0f, settlingDuration);

        releaseZoneSize.x = Mathf.Max(5f, releaseZoneSize.x);
        releaseZoneSize.y = Mathf.Max(5f, releaseZoneSize.y);
        runoutDistance = Mathf.Clamp(runoutDistance, 10f, 3000f);

        blockSize = Mathf.Clamp(blockSize, 0.5f, 60f);
        blockDepth = Mathf.Clamp(blockDepth, 0.25f, 15f);
        blockMass = Mathf.Max(1f, blockMass);
        maxBlocks = Mathf.Clamp(maxBlocks, 4, 800);
        detachmentSpeed = Mathf.Clamp(detachmentSpeed, 0f, 40f);
        detachmentSpin = Mathf.Clamp(detachmentSpin, 0f, 180f);
        runoutDrag = Mathf.Clamp(runoutDrag, 0f, 5f);
        precursorSpallFraction = Mathf.Clamp(precursorSpallFraction, 0f, 0.25f);

        scarDepth = Mathf.Clamp(scarDepth, 0f, 60f);
        depositHeight = Mathf.Clamp(depositHeight, 0f, 60f);
        heightmapUpdatesPerSecond = Mathf.Clamp(heightmapUpdatesPerSecond, 1f, 60f);
        dataEstimateTimeout = Mathf.Clamp(dataEstimateTimeout, 1f, 300f);
        minSteepness = Mathf.Clamp(minSteepness, 5f, 80f);
        searchRadius = Mathf.Max(0f, searchRadius);
    }

    /// <summary>
    /// The horizontal (XZ) extent of the release area. Blocks are generated inside this
    /// rectangle, oriented along the slide direction (z) and across the slope (x).
    /// </summary>
    public Vector2 ReleaseZoneSize => releaseZoneSize;
}
