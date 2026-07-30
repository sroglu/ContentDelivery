using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sirenix.OdinInspector;
using UnityEditor;
using UnityEngine;
using PFound.ContentDelivery.Core;

namespace PFound.ContentDelivery.Editor
{
    /// <summary>One content environment: a name and its CDN origin (may carry {PROJECT}/{persistentDataPath} tokens).</summary>
    [System.Serializable]
    public struct ContentEnvironmentEntry
    {
        [HorizontalGroup("env", 0.30f), LabelWidth(40)] public string Name;
        [HorizontalGroup("env"), LabelWidth(60), Tooltip("May use tokens (see 'BaseUrl tokens' below). Empty = offline.")]
        public string BaseUrl;
    }

    /// <summary>Which dev/prod posture an App Build targets (see <see cref="AssetGroup.ExcludeInProduction"/>).</summary>
    public enum BuildMode { Development, Production }

    /// <summary>
    /// The editor build-configuration for content delivery, centered on <see cref="OfflineBuild"/>:
    /// <b>true</b> embeds every group into StreamingAssets (no CDN); <b>false</b> honors per-group distribution and
    /// stages remote groups for upload. Carries the platform/scope/group-selection, game id, the monotonic catalog
    /// build number (folded into the versioned catalog name), the upload target and the shared environments. Odin
    /// inspector; the selector-window drawers are replaced with plain popups (Odin's OdinEditorWindow is broken on
    /// this Unity version). Editor-only SO.
    /// </summary>
    [CreateAssetMenu(menuName = "PFound/Content Delivery/Catalog Editor Config", fileName = "CatalogEditorConfig")]
    public sealed class CatalogEditorConfig : ScriptableObject, IContentAuthoringValidation
    {
        [InfoBox("@AuthoringValidation.Errors(this)", InfoMessageType.Error, "@AuthoringValidation.HasErrors(this)")]
        [InfoBox("@AuthoringValidation.Warnings(this)", InfoMessageType.Warning, "@AuthoringValidation.HasWarnings(this)")]
        [InfoBox("Ready — no authoring issues.", InfoMessageType.Info, "@AuthoringValidation.Ready(this)")]
        [InfoBox("@OfflineBuild ? \"OFFLINE: every group is embedded into StreamingAssets; remote/env/upload are ignored.\" " +
                 ": \"ONLINE: per-group distribution is honored; remote groups upload to the active environment.\"", InfoMessageType.None)]
        [Tooltip("Force ALL groups into StreamingAssets and strip remote entries from the catalog (offline package).")]
        public bool OfflineBuild = false;

        [FoldoutGroup("Environments (shared source — no drift)", expanded: true)]
        [DisableIf(nameof(OfflineBuild)), ListDrawerSettings(ShowFoldout = false)]
        public List<ContentEnvironmentEntry> Environments = new List<ContentEnvironmentEntry>();

        // CustomValueDrawer (plain popup) instead of [ValueDropdown]: Odin's selector window is broken here.
        [FoldoutGroup("Environments (shared source — no drift)")]
        [DisableIf(nameof(OfflineBuild)), CustomValueDrawer(nameof(DrawActiveEnvironment))]
        public string ActiveEnvironment = "prod";

        [FoldoutGroup("Environments (shared source — no drift)"), ShowInInspector, ReadOnly]
        [DictionaryDrawerSettings(KeyLabel = "Token", ValueLabel = "Resolves To", IsReadOnly = true)]
        [Tooltip("Tokens you can put in a BaseUrl above — shown with what they resolve to on THIS machine.")]
        Dictionary<string, string> BaseUrlTokens => new Dictionary<string, string>
        {
            { "{PROJECT}",              ProjectRoot() },
            { "{persistentDataPath}",   Application.persistentDataPath },
            { "{streamingAssetsPath}",  Application.streamingAssetsPath },
        };

        // Curated content targets (not the full BuildTarget enum). Plain popup — Odin's enum selector is broken here.
        [FoldoutGroup("Build", expanded: true), CustomValueDrawer(nameof(DrawBuildPlatform))]
        public ContentBuildTarget BuildPlatform = ContentBuildTarget.Android;
        [FoldoutGroup("Build"), CustomValueDrawer(nameof(DrawMode))]
        [Tooltip("Production drops dev-only groups (AssetGroup.ExcludeInProduction) from a build.")]
        public BuildMode Mode = BuildMode.Development;
        [FoldoutGroup("Build"), Tooltip("Game identifier folded into the catalog file name / upload path.")]
        public string GameId = "game";

        [FoldoutGroup("Catalog Version", expanded: true), ShowInInspector, ReadOnly, DisplayAsString, LabelText("App Version")]
        string AppVersionDisplay => AppVersion();
        [FoldoutGroup("Catalog Version"), ShowInInspector, ReadOnly, LabelText("Last Built #")]
        int LastBuiltDisplay => CatalogBuildNumber;
        [FoldoutGroup("Catalog Version"), LabelText("Build # override")]
        [Tooltip("Empty = auto-increment (last built + 1). Enter a number to force the NEXT build number; it is " +
                 "clamped to ≥ the last built # so it never regresses (min = last build).")]
        public string BuildNumberOverride = "";
        [FoldoutGroup("Catalog Version"), ShowInInspector, ReadOnly, DisplayAsString, LabelText("Next Catalog (preview)")]
        string NextCatalogPreview => CatalogNameVersion.Compose(GameId, AppVersion(), ResolveNextBuildNumber(), ModeToken());
        // Current Catalog = the catalog the runtime actually resolves — read from the LIVE embedded pointer (the real
        // file on disk), NOT reconstructed from this config. Reflects the version+build+mode actually in use (§1.4e).
        [FoldoutGroup("Catalog Version"), ShowInInspector, ReadOnly, DisplayAsString, LabelText("Current Catalog (live pointer)")]
        [Tooltip("What the runtime resolves now, from the embedded catalog pointer. 'none / not built' until an App Build stages one.")]
        string CurrentCatalogDisplay
        {
            get
            {
                string dir = ContentPlatform.GetEmbeddedAssetBundlePath(PlatformFolder());
                string pointer = System.IO.Path.Combine(dir, AssetBundleLayout.EmbeddedCatalogPointerFileName);
                if (!System.IO.File.Exists(pointer)) return "none / not built";
                string file = System.IO.File.ReadAllText(pointer).Trim();
                return string.IsNullOrEmpty(file) ? "none / not built" : file;
            }
        }

        // Per-platform embedded build table (Platform → ✓/✗ + size + catalog + build mode). dir stat + pointer read;
        // the mode is parsed from the catalog FILE NAME (all metadata lives in the name). LOCAL build state — NOT the CDN.
        [FoldoutGroup("Build Readiness (LOCAL only — not the CDN)", expanded: true), ShowInInspector, ReadOnly]
        [DictionaryDrawerSettings(KeyLabel = "Platform", ValueLabel = "Embedded build (shipped, offline)", IsReadOnly = true)]
        Dictionary<string, string> BuildReadiness
        {
            get
            {
                var d = new Dictionary<string, string>();
                foreach (var p in new[] { "StandaloneOSX", "iOS", "Android", "StandaloneWindows64" })
                {
                    string dir = ContentPlatform.GetEmbeddedAssetBundlePath(p);
                    if (!ContentPlatform.HasEmbeddedBundles(p)) { d[p] = "✗ none"; continue; }
                    string pointer = System.IO.Path.Combine(dir, AssetBundleLayout.EmbeddedCatalogPointerFileName);
                    string cat = System.IO.File.Exists(pointer) ? System.IO.File.ReadAllText(pointer).Trim() : null;
                    string mode = cat != null ? CatalogNameVersion.Parse(cat).Mode : null;   // mode from the FILE NAME, not content
                    d[p] = $"✓ ({FormatSize(DirFilesSize(dir))})"
                         + (cat != null ? $"  {cat}" : "  (no catalog pointer)")
                         + (mode != null ? $"  [{mode}]" : "");
                }
                return d;
            }
        }


        [FoldoutGroup("Build Readiness (LOCAL only — not the CDN)")]
        [Button("Check Remote / CDN availability (network)")]
        void CheckRemote()
        {
            _remoteStatus.Clear();
            var reader = new RemoteCatalogPointerReader(ContentDeliveryBootstrap.DefaultTransport);
            foreach (var env in Environments)
            {
                string origin = ResolveTokens(env.BaseUrl);
                if (string.IsNullOrEmpty(origin)) continue;
                foreach (var p in new[] { "StandaloneOSX", "iOS", "Android" })
                {
                    var ptr = reader.TryReadPointerAsync(origin, p).GetAwaiter().GetResult();
                    _remoteStatus[$"{env.Name} / {p}"] = ptr.Resolved ? $"✓ {ptr.CatalogFileName}" : "✗ missing / unreachable";
                }
            }
            Debug.Log($"[ContentDelivery] Remote availability checked — {_remoteStatus.Count} env/platform combo(s).");
        }

        [FoldoutGroup("Build Readiness (LOCAL only — not the CDN)"), ShowInInspector, ReadOnly]
        [LabelText("Env deployment — last check (CDN)")]
        [DictionaryDrawerSettings(KeyLabel = "Env / Platform", ValueLabel = "CDN catalog", IsReadOnly = true)]
        Dictionary<string, string> _remoteStatus = new Dictionary<string, string>();

        [FoldoutGroup("Build Readiness (LOCAL only — not the CDN)"), EnableIf(nameof(HasBuildToReveal))]
        [Button("Reveal Build Folder")]
        void RevealBuildFolder()
        {
            // Prefer reveal-SELECTING the live embedded catalog for the active platform (highlights the real file the
            // runtime resolves); fall back to the raw build-output dir. RevealInFinder is cross-platform (Finder/Explorer).
            string dir = ContentPlatform.GetEmbeddedAssetBundlePath(PlatformFolder());
            string pointer = System.IO.Path.Combine(dir, AssetBundleLayout.EmbeddedCatalogPointerFileName);
            if (System.IO.File.Exists(pointer))
            {
                string catalog = System.IO.Path.Combine(dir, System.IO.File.ReadAllText(pointer).Trim());
                EditorUtility.RevealInFinder(System.IO.File.Exists(catalog) ? catalog : dir);
                return;
            }
            EditorUtility.RevealInFinder(CatalogEditorBuildRunner.OutputDirectory);
        }

        // Enable the reveal button only when there is something to open: an embedded package or the raw build output.
        bool HasBuildToReveal =>
            ContentPlatform.HasEmbeddedBundles(PlatformFolder()) || System.IO.Directory.Exists(CatalogEditorBuildRunner.OutputDirectory);

        [FoldoutGroup("Build Readiness (LOCAL only — not the CDN)"), GUIColor(0.9f, 0.5f, 0.4f)]
        [Button("Clear Build (embedded, this platform)")]
        void ClearBuild()
        {
            if (!EditorUtility.DisplayDialog("Clear Build",
                    $"Delete the embedded build for {PlatformFolder()} from StreamingAssets?", "Clear", "Cancel")) return;
            CatalogEditorBuildRunner.ClearEmbedded(PlatformFolder());
            AssetDatabase.Refresh();
        }

        [SerializeField, HideInInspector] int _catalogBuildNumber = 0;

        [FoldoutGroup("Upload"), DisableIf(nameof(OfflineBuild))]
        [Tooltip("Authored gate for the post-build upload step (OFF = build only).")]
        public bool UploadAfterBuild = false;
        [FoldoutGroup("Upload"), DisableIf(nameof(OfflineBuild)), ShowIf(nameof(UploadAfterBuild))]
        [Tooltip("Index into an app-provided uploader list.")]
        public int UploadTargetIndex = 0;

        [FoldoutGroup("Upload"), DisableIf(nameof(OfflineBuild))]
        [InfoBox("Remote upload runs via an app-provided ICdnUploader (DirectoryUploader / FtpUploader / BunnyCdnUploader " +
                 "/ S3Uploader) — call CatalogEditorBuildRunner.UploadAsync(config, uploader, publishDir) from your build " +
                 "pipeline. This button uploads directly for a local file:// environment (dev); a CDN env needs the app to supply the uploader.")]
        [Button("Upload last published build → active env")]
        void UploadLastBuild()
        {
            string origin = ResolveTokens(ActiveBaseUrl());
            if (string.IsNullOrEmpty(origin)) { Debug.Log("[ContentDelivery] Active environment is offline — nothing to upload."); return; }
            if (!origin.StartsWith("file://"))
            {
                Debug.LogWarning($"[ContentDelivery] '{ActiveEnvironment}' is a network env ({origin}) — supply an ICdnUploader " +
                                 "in your build pipeline (CatalogEditorBuildRunner.UploadAsync). Direct upload here only covers file:// dev envs.");
                return;
            }
            string publish = System.IO.Path.Combine(System.IO.Directory.GetParent(Application.dataPath).FullName, "ContentBuild", "publish");
            string dest = origin.Substring("file://".Length);
            if (!System.IO.Directory.Exists(publish)) { Debug.LogWarning($"[ContentDelivery] No published build at {publish} — run an online App Build first."); return; }
            CdnUpload.UploadPublishDirectoryAsync(new DirectoryUploader(dest), publish).GetAwaiter().GetResult();
            Debug.Log($"[ContentDelivery] Uploaded {publish} → {dest}");
        }

        // Editor token resolution — {PROJECT} = the PROJECT ROOT folder (matches the legacy convention for
        // file:// dev servers), not the app product name.
        static string ProjectRoot() => Directory.GetParent(Application.dataPath).FullName;
        static string ResolveTokens(string url) => string.IsNullOrEmpty(url) ? url :
            url.Replace("{PROJECT}", ProjectRoot())
               .Replace("{persistentDataPath}", Application.persistentDataPath)
               .Replace("{streamingAssetsPath}", Application.streamingAssetsPath);

        static long DirFilesSize(string dir)
        {
            if (!Directory.Exists(dir)) return 0;
            long total = 0;
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                if (!f.EndsWith(".meta", System.StringComparison.Ordinal)) total += new FileInfo(f).Length;
            return total;
        }

        static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024d / 1024 / 1024:0.#} GB";
            if (bytes >= 1024L * 1024)        return $"{bytes / 1024d / 1024:0.#} MB";
            if (bytes >= 1024)                return $"{bytes / 1024d:0.#} KB";
            return $"{bytes} B";
        }

        // --- plain-popup drawers (avoid Odin's broken selector window) ---
        string DrawActiveEnvironment(string value, GUIContent label)
        {
            var names = Environments.Select(e => e.Name).ToArray();
            if (names.Length == 0) { EditorGUILayout.LabelField(label, new GUIContent("(add an environment above)")); return value; }
            int cur = System.Array.IndexOf(names, value);
            if (cur >= 0)
            {
                int picked = EditorGUILayout.Popup(label.text, cur, names);
                return picked != cur ? names[picked] : value;
            }
            var opts = new[] { $"⚠ '{value}' (not in list)" }.Concat(names).ToArray();
            int p = EditorGUILayout.Popup(label.text, 0, opts);
            return p > 0 ? names[p - 1] : value;
        }

        ContentBuildTarget DrawBuildPlatform(ContentBuildTarget value, GUIContent label) =>
            (ContentBuildTarget)EditorGUILayout.EnumPopup(label, value);

        BuildMode DrawMode(BuildMode value, GUIContent label) =>
            (BuildMode)EditorGUILayout.EnumPopup(label, value);

        /// <summary>The number of the LAST App Build (0 = never built). The next build uses <c>+1</c>.</summary>
        public int CatalogBuildNumber => _catalogBuildNumber;

        /// <summary>The player app version stamped into the catalog name (scopes a catalog to a release).</summary>
        public string AppVersion() => PlayerSettings.bundleVersion;

        /// <summary>
        /// The build number the NEXT App Build will use: the explicit <see cref="BuildNumberOverride"/> when set
        /// (clamped to ≥ the last built number so it never regresses), else auto-increment (last + 1) when empty or
        /// unparseable. Pure — does not mutate state (call <see cref="CommitBuildNumber"/> to persist).
        /// </summary>
        public int ResolveNextBuildNumber()
        {
            if (!string.IsNullOrWhiteSpace(BuildNumberOverride) && int.TryParse(BuildNumberOverride.Trim(), out int n))
                return Mathf.Max(n, _catalogBuildNumber);   // min = last built; a below-last entry is raised, never regresses
            return _catalogBuildNumber + 1;                 // empty / unparseable → auto-increment
        }

        /// <summary>Persists <paramref name="number"/> as the last built number. Call once per App Build, before
        /// naming the catalog (<see cref="CatalogFileName"/> reads it).</summary>
        public void CommitBuildNumber(int number)
        {
            _catalogBuildNumber = number;
            EditorUtility.SetDirty(this);
            AssetDatabase.SaveAssets();
        }

        /// <summary>The active environment's base URL, or empty when offline / unconfigured.</summary>
        public string ActiveBaseUrl()
        {
            if (OfflineBuild) return string.Empty; // offline package has no remote origin
            for (int i = 0; i < Environments.Count; i++)
                if (string.Equals(Environments[i].Name, ActiveEnvironment, System.StringComparison.Ordinal))
                    return Environments[i].BaseUrl;
            return string.Empty;
        }

        /// <summary>
        /// The config-predictable catalog file name (NO content hash): <c>catalog_&lt;gameId&gt;_v&lt;appVersion&gt;_b&lt;build&gt;_&lt;dev|prod&gt;.lzma</c>.
        /// Uses the CURRENT <see cref="CatalogBuildNumber"/> — call <see cref="CommitBuildNumber"/> first. The staged
        /// embedded file additionally carries the content hash (see <see cref="CatalogFileName(string)"/>); the pointer
        /// names the real file, so the hash-less form here is what the config can predict (e.g. for the remote origin).
        /// </summary>
        public string CatalogFileName() => CatalogNameVersion.Compose(GameId, AppVersion(), CatalogBuildNumber, ModeToken());

        /// <summary>The full staged catalog name INCLUDING the content <paramref name="contentHash"/>:
        /// <c>catalog_…_&lt;dev|prod&gt;_&lt;hash&gt;.lzma</c>. Only the build knows the hash, so this is the name written to
        /// disk + into the pointer; runtime discovers it via the pointer, not by predicting it.</summary>
        public string CatalogFileName(string contentHash) =>
            CatalogNameVersion.Compose(GameId, AppVersion(), CatalogBuildNumber, ModeToken(), contentHash);

        /// <summary>The dev/prod token — written into BOTH the catalog file name and the catalog content Mode field
        /// (from this one source, so they cannot diverge). "prod" / "dev".</summary>
        internal string ModeToken() => Mode == BuildMode.Production ? "prod" : "dev";

        /// <summary>The platform folder name for <see cref="BuildPlatform"/> (matches the runtime layout).</summary>
        public string PlatformFolder() => ContentPlatform.EditorPlatformFolder(BuildPlatform.ToBuildTarget());

        /// <summary>The runtime remote config this build targets (empty base URL = offline).</summary>
        public RemoteContentConfig ToRemoteConfig() =>
            new RemoteContentConfig(ActiveBaseUrl(), PlatformFolder(), CatalogFileName());

        /// <summary>Inspector self-validation (see <see cref="IContentAuthoringValidation"/>).</summary>
        public IEnumerable<AuthoringIssue> Validate()
        {
            if (string.IsNullOrWhiteSpace(GameId))
                yield return AuthoringIssue.Warning("Game Id is empty — set a game id (it names the catalog).");

            // Build-number override (applies to offline + online): flag a non-number or a below-last entry.
            if (!string.IsNullOrWhiteSpace(BuildNumberOverride))
            {
                if (!int.TryParse(BuildNumberOverride.Trim(), out int n))
                    yield return AuthoringIssue.Warning($"Build # override '{BuildNumberOverride}' is not a number — the next build will auto-increment (last + 1).");
                else if (n < _catalogBuildNumber)
                    yield return AuthoringIssue.Warning($"Build # override {n} is below the last built #{_catalogBuildNumber} — it will be raised to {_catalogBuildNumber} (min = last build).");
            }

            if (OfflineBuild) yield break; // env/upload ignored offline

            if (Environments.Count == 0)
                yield return AuthoringIssue.Warning("No environments defined — online builds have no CDN origin to target.");
            else if (!Environments.Any(e => string.Equals(e.Name, ActiveEnvironment, System.StringComparison.Ordinal)))
                yield return AuthoringIssue.Error($"Active Environment '{ActiveEnvironment}' is not one of the environments — pick a valid one.");

            // Mode ↔ active-environment posture (non-blocking footgun guard): a Production build usually targets the
            // remote CDN, a Development build a local/dev origin. Heuristic on the active BaseUrl scheme (no explicit
            // per-env prod flag exists). Guards BOTH build paths since Mode is honored in the shared runner.
            string activeUrl = ActiveBaseUrl();
            if (!string.IsNullOrEmpty(activeUrl))
            {
                bool remote = LooksLikeRemoteOrigin(activeUrl);
                if (Mode == BuildMode.Production && !remote)
                    yield return AuthoringIssue.Warning($"Production mode but active environment '{ActiveEnvironment}' looks local/dev ({activeUrl}) — a production build usually targets the remote CDN.");
                else if (Mode == BuildMode.Development && remote)
                    yield return AuthoringIssue.Warning($"Development mode but active environment '{ActiveEnvironment}' points at a remote CDN ({activeUrl}) — a dev build would publish to production.");
            }
        }

        // A network/prod-looking origin: http(s) and not localhost. file:// and localhost read as local/dev; empty = offline.
        static bool LooksLikeRemoteOrigin(string baseUrl)
        {
            if (string.IsNullOrEmpty(baseUrl)) return false;
            string u = ResolveTokens(baseUrl);
            if (u.StartsWith("file://", System.StringComparison.OrdinalIgnoreCase)) return false;
            if (u.Contains("localhost") || u.Contains("127.0.0.1")) return false;
            return u.StartsWith("http://", System.StringComparison.OrdinalIgnoreCase)
                || u.StartsWith("https://", System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
