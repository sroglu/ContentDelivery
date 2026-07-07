namespace PFound.ContentDelivery
{
    /// <summary>
    /// Load-priority bands for phased delivery: lower values are provisioned first, so essential
    /// content can be brought up before the rest of the game. Mirrors <c>CatalogAsset.Phase</c>.
    /// </summary>
    public enum AssetPhase
    {
        /// <summary>Must be present before the player is interactive (boot UI, first scene).</summary>
        Essential = 0,

        /// <summary>Needed soon after boot but not on the critical path (HUD, common props).</summary>
        Early = 100,

        /// <summary>Default band: streamed on demand during play.</summary>
        Standard = 200,

        /// <summary>Deferred / background content (cosmetics, far-future levels).</summary>
        Deferred = 300,
    }
}
