using System;

namespace PFound.ContentDelivery.Core
{
    /// <summary>
    /// The version stamped into a catalog file name: <c>catalog_&lt;gameId&gt;_v&lt;appVersion&gt;_b&lt;build&gt;[_&lt;mode&gt;][_&lt;hash&gt;].lzma</c>.
    /// The <see cref="AppVersion"/> (the player's <c>Application.version</c>) scopes a catalog to an app release; the
    /// monotonic <see cref="Build"/> counter orders catalogs within/across releases. Ordering is
    /// (appVersion, build) so a resolver can reject an OLDER catalog (rollback / stale pointer) — a downgrade guard.
    /// Content hashes stay purely content-derived (dedup); ordering lives here, in the name, not in the hash.
    /// </summary>
    public readonly struct CatalogNameVersion : IComparable<CatalogNameVersion>
    {
        public readonly string AppVersion;   // e.g. "1.0.0"
        public readonly int Build;           // monotonic build counter
        public readonly string Mode;         // dev/prod posture token, or null on names without it
        public readonly bool Parsed;         // false when the name carried no version postfix

        public CatalogNameVersion(string appVersion, int build, string mode = null)
        {
            AppVersion = appVersion ?? string.Empty;
            Build = build;
            Mode = string.IsNullOrEmpty(mode) ? null : mode;
            Parsed = true;
        }

        public static readonly CatalogNameVersion None = default; // Parsed == false

        /// <summary>
        /// Builds the catalog file name — ONE binary <c>.lzma</c> file that carries ALL metadata in its name:
        /// <c>catalog_&lt;gameId&gt;_v&lt;appVersion&gt;_b&lt;build&gt;[_&lt;mode&gt;][_&lt;hash&gt;].lzma</c>.
        /// <paramref name="appVersion"/> underscores → '-' (delimiter-safe). <paramref name="mode"/> (e.g. "dev"/"prod")
        /// distinguishes dev &amp; prod builds; <paramref name="hash"/> is the catalog's content hash (its identity) and
        /// goes LAST — same shape as bundles (<c>bundlepack_…_&lt;hash&gt;.lzma</c>). Both are optional (omit → no token):
        /// the config emits a hash-less name it can predict, the build stamps the real hash.
        /// </summary>
        public static string Compose(string gameId, string appVersion, int build, string mode = null, string hash = null)
        {
            string modeToken = string.IsNullOrEmpty(mode) ? string.Empty : $"_{mode}";
            string hashToken = string.IsNullOrEmpty(hash) ? string.Empty : $"_{hash}";
            return $"catalog_{gameId}_v{Sanitize(appVersion)}_b{build}{modeToken}{hashToken}.lzma";
        }

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

            // Strip the extension (.lzma — the current single-file form; .json — legacy embedded catalogs).
            string name = catalogFileName;
            if (name.EndsWith(".lzma", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 5);
            else if (name.EndsWith(".json", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 5);

            int bAt = name.LastIndexOf("_b", StringComparison.Ordinal);
            if (bAt < 0) return None;
            // After "_b": the build digits, then OPTIONAL "_<mode>" and "_<hash>" tokens (e.g. "12", "12_prod",
            // or "12_prod_<hash>"). mode = the first token after the build; any further token is the content hash.
            string tail = name.Substring(bAt + 2);
            int us1 = tail.IndexOf('_');
            string buildText = us1 >= 0 ? tail.Substring(0, us1) : tail;
            string afterBuild = us1 >= 0 ? tail.Substring(us1 + 1) : null;  // "<mode>" or "<mode>_<hash>"
            string mode = afterBuild;
            if (afterBuild != null)
            {
                int us2 = afterBuild.IndexOf('_');
                if (us2 >= 0) mode = afterBuild.Substring(0, us2);          // drop the trailing _<hash>
            }
            if (buildText.Length == 0 || !int.TryParse(buildText, out int build)) return None;

            string head = name.Substring(0, bAt);              // catalog_<gameId>_v<appVersion>
            int vAt = head.LastIndexOf("_v", StringComparison.Ordinal);
            if (vAt < 0) return None;
            string appVersion = head.Substring(vAt + 2);
            if (appVersion.Length == 0) return None;

            return new CatalogNameVersion(appVersion, build, mode);
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

        public override string ToString() =>
            Parsed ? $"v{AppVersion} b{Build}{(Mode != null ? " " + Mode : string.Empty)}" : "<unversioned>";
    }
}
