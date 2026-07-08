using System;

namespace PFound.ContentDelivery.Core
{
    /// <summary>
    /// The version stamped into a catalog file name: <c>catalog_&lt;gameId&gt;_v&lt;appVersion&gt;_b&lt;build&gt;.json</c>.
    /// The <see cref="AppVersion"/> (the player's <c>Application.version</c>) scopes a catalog to an app release; the
    /// monotonic <see cref="Build"/> counter orders catalogs within/across releases. Ordering is
    /// (appVersion, build) so a resolver can reject an OLDER catalog (rollback / stale pointer) — a downgrade guard.
    /// Content hashes stay purely content-derived (dedup); ordering lives here, in the name, not in the hash.
    /// </summary>
    public readonly struct CatalogNameVersion : IComparable<CatalogNameVersion>
    {
        public readonly string AppVersion;   // e.g. "1.0.0"
        public readonly int Build;           // monotonic build counter
        public readonly bool Parsed;         // false when the name carried no version postfix

        public CatalogNameVersion(string appVersion, int build)
        {
            AppVersion = appVersion ?? string.Empty;
            Build = build;
            Parsed = true;
        }

        public static readonly CatalogNameVersion None = default; // Parsed == false

        /// <summary>Builds the versioned catalog file name. <paramref name="appVersion"/> underscores → '-' (delimiter-safe).</summary>
        public static string Compose(string gameId, string appVersion, int build) =>
            $"catalog_{gameId}_v{Sanitize(appVersion)}_b{build}.json";

        private static string Sanitize(string appVersion) =>
            string.IsNullOrEmpty(appVersion) ? "0" : appVersion.Replace('_', '-').Replace(' ', '-');

        /// <summary>
        /// Parses the <c>_v&lt;appVersion&gt;_b&lt;build&gt;.json</c> postfix out of a catalog file name. Tolerant of a
        /// gameId that itself contains underscores (the build segment is the last <c>_b&lt;digits&gt;.json</c>).
        /// Returns <see cref="None"/> (Parsed == false) for an un-versioned legacy name.
        /// </summary>
        public static CatalogNameVersion Parse(string catalogFileName)
        {
            if (string.IsNullOrEmpty(catalogFileName)) return None;

            const string ext = ".json";
            string name = catalogFileName.EndsWith(ext, StringComparison.Ordinal)
                ? catalogFileName.Substring(0, catalogFileName.Length - ext.Length)
                : catalogFileName;

            int bAt = name.LastIndexOf("_b", StringComparison.Ordinal);
            if (bAt < 0) return None;
            string buildText = name.Substring(bAt + 2);
            if (buildText.Length == 0 || !int.TryParse(buildText, out int build)) return None;

            string head = name.Substring(0, bAt);              // catalog_<gameId>_v<appVersion>
            int vAt = head.LastIndexOf("_v", StringComparison.Ordinal);
            if (vAt < 0) return None;
            string appVersion = head.Substring(vAt + 2);
            if (appVersion.Length == 0) return None;

            return new CatalogNameVersion(appVersion, build);
        }

        /// <summary>(appVersion, build) order. App version compared numeric-component-wise (1.10 &gt; 1.9), then build.</summary>
        public int CompareTo(CatalogNameVersion other)
        {
            int v = CompareAppVersions(AppVersion, other.AppVersion);
            return v != 0 ? v : Build.CompareTo(other.Build);
        }

        // Dotted numeric compare with ordinal fallback for any non-numeric segment.
        private static int CompareAppVersions(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.Ordinal)) return 0;
            string[] pa = (a ?? string.Empty).Split('.');
            string[] pb = (b ?? string.Empty).Split('.');
            int n = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < n; i++)
            {
                string sa = i < pa.Length ? pa[i] : "0";
                string sb = i < pb.Length ? pb[i] : "0";
                if (int.TryParse(sa, out int na) && int.TryParse(sb, out int nb))
                {
                    if (na != nb) return na.CompareTo(nb);
                }
                else
                {
                    int c = string.Compare(sa, sb, StringComparison.Ordinal);
                    if (c != 0) return c;
                }
            }
            return 0;
        }

        public override string ToString() => Parsed ? $"v{AppVersion} b{Build}" : "<unversioned>";
    }
}
