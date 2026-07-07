using System;

namespace PFound.ContentDelivery.Core
{
    /// <summary>Where a required catalog should be sourced from, cheapest-first.</summary>
    public enum CatalogSource
    {
        /// <summary>The build-embedded catalog already IS the required one — load it, download nothing.</summary>
        Embedded,
        /// <summary>A previously-downloaded copy of the required catalog is on disk — load it, download nothing.</summary>
        Cached,
        /// <summary>Neither embedded nor cached matches — fetch the required catalog from the remote.</summary>
        Download,
    }

    /// <summary>
    /// The pure decision behind <c>DownloadCatalogIfRequiredAndLoad</c>: given what the app requires versus what is
    /// already on the device, pick the source. Embedded-matches-required wins (offline, zero cost); else a cached
    /// copy of the required catalog; else a download. Engine-free so the branch is unit-testable; the Unity service
    /// wraps it with the actual file reads and the fetch.
    /// </summary>
    public static class CatalogAcquisitionPlan
    {
        /// <param name="requiredCatalogFileName">Catalog file the app wants (from the remote pointer / config).</param>
        /// <param name="embeddedCatalogFileName">Catalog the embedded pointer names; null when none is embedded.</param>
        /// <param name="requiredCachedOnDisk">Whether the required catalog is already in the on-disk cache.</param>
        public static CatalogSource Decide(
            string requiredCatalogFileName, string embeddedCatalogFileName, bool requiredCachedOnDisk)
        {
            if (!string.IsNullOrEmpty(embeddedCatalogFileName) &&
                string.Equals(embeddedCatalogFileName, requiredCatalogFileName, StringComparison.Ordinal))
                return CatalogSource.Embedded;

            if (requiredCachedOnDisk)
                return CatalogSource.Cached;

            return CatalogSource.Download;
        }
    }
}
