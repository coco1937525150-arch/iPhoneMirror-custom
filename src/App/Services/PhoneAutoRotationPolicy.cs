namespace IPhoneMirror.App.Services;

/// <summary>
/// Select a single whole-phone quarter-turn from the currently committed
/// orientation AND the destination of any in-progress DirectComposition
/// animation. AirPlay content is never manipulated by this policy.
/// </summary>
internal static class PhoneAutoRotationPolicy
{
    /// <returns>-1 for a left turn, +1 for a right turn, 0 if the
    /// device shell already has the correct orientation parity.</returns>
    // The renderer's early video UV orientation is relative to the ORIGINAL
    // (not yet turned) phone shell. A change from 1920x1080 to 1280x720
    // during an active -90-degree animation must NOT clear the override just
    // because the pending destination already matches landscape.
    internal static int DecideStagedVideoTurns(int committedTurns, int pendingTurns,
        bool sourceLandscape)
    {
        if (!sourceLandscape || (committedTurns & 1) != 0)
            return -1; // automatic UV orientation
        var intendedTurn = pendingTurns != 0 ? pendingTurns
            : DecideDelta(committedTurns, 0, sourceLandscape);
        return intendedTurn == 0 ? -1
            : ((-intendedTurn % 4) + 4) % 4;
    }

    internal static int DecideDelta(int committedTurns, int pendingTurns,
        bool sourceLandscape)
    {
        var destination = ((committedTurns + pendingTurns) % 4 + 4) % 4;
        if (((destination & 1) != 0) == sourceLandscape)
            return 0;

        // From upright portrait, video landscape always turns LEFT.
        // From upside-down portrait, turn right instead of making an
        // unnecessary 180/270 degree sweep.
        if (sourceLandscape)
            return destination == 2 ? 1 : -1;

        // On return from a manually-right landscape, undo that RIGHT
        // rotation instead of turning farther away from upright portrait.
        return destination == 1 ? -1 : 1;
    }
}
