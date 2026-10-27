/// <summary>
/// The stages a time-driven slope failure (landslide) goes through.
/// The landslide is played out alongside the RAMMS debris-flow animation and
/// describes the rock-slope detachment that feeds the flow.
/// </summary>
public enum LandslidePhase
{
    /// <summary>Nothing has happened yet, the slope is still intact.</summary>
    Dormant = 0,

    /// <summary>Cracks open, small rock-fall, growing rumble and dust. The slope is about to fail.</summary>
    Precursory,

    /// <summary>The unstable mass detaches from the hillside.</summary>
    Failure,

    /// <summary>The detached mass travels down the slope.</summary>
    Runout,

    /// <summary>The mass has come to rest, the dust settles.</summary>
    Deposited,

    /// <summary>The event was canceled / disabled and will not run.</summary>
    Disabled
}
