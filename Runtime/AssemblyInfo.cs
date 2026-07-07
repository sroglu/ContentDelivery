using System.Runtime.CompilerServices;

// The play-mode parity suite drives AssetManager's static, ref-counted state and needs to reset it
// between tests; expose internals to the test assembly only.
[assembly: InternalsVisibleTo("PFound.ContentDelivery.Tests")]

// The editor fast-path source reuses the runtime sub-asset address parser (TrySplitSubAsset) so the
// editor and bundle paths split "main[sub]" addresses identically.
[assembly: InternalsVisibleTo("PFound.ContentDelivery.Editor")]
