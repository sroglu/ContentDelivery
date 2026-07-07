using System.Collections.Generic;

namespace PFound.ContentDelivery
{
    /// <summary>
    /// An <see cref="IAssetSource"/> that holds bundles in memory and can report them. The memory reporter
    /// asks every registered source whether it implements this so bundle residency is collected without the
    /// reporter knowing each source's concrete type. Sources with no bundles (e.g. Resources) don't implement it.
    /// </summary>
    public interface IBundleMemorySource
    {
        IReadOnlyList<BundleMemoryRow> SnapshotBundleRows();
    }
}
