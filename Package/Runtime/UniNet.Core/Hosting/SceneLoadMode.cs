namespace UniNet.Core.Hosting
{
    /// <summary>
    /// Scene transition kind on wire 11 (ADR-0057). Wire format: [mode byte][scene name]. Replace keeps the
    /// legacy single-load semantics (the previous world unloads); Additive loads the scene alongside the active
    /// set; Unload removes a previously loaded additive scene (its registered scene objects are destroyed and
    /// propagated before the unload command on the same reliable stream).
    /// One transition may be in flight at a time — a second StartSceneTransition call throws until the current
    /// one completes (AllClientsSceneLoaded).
    /// </summary>
    public enum SceneLoadMode : byte
    {
        /// <summary>Single-mode load — replaces the whole active scene set (legacy ADR-0035 behavior).</summary>
        Replace = 0,

        /// <summary>Load the scene additively alongside the active set — existing objects are untouched; game data is held until the ready report, then a catch-up baseline covers the new scene's objects.</summary>
        Additive = 1,

        /// <summary>Unload a previously loaded additive scene — the server destroys that scene's registered scene objects (propagated) before the unload command.</summary>
        Unload = 2,
    }
}
