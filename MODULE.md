# ContentDelivery

## Purpose

Addressable content delivery: author assets into groups, build AssetBundles + a content catalog,
ship them locally (StreamingAssets) or remotely (CDN), and load them at runtime by string address
with per-owner reference counting. The catalog model, bundle provisioning and download scheduling
are engine-free pure C#; the Unity runtime and the editor build pipeline layer on top.

## Assemblies

| Assembly | Folder | Kind | Notes |
| --- | --- | --- | --- |
| `PFound.ContentDelivery.Core` | `Core/Runtime/` | pure C# | `noEngineReferences: true`; catalog model, provisioning, download scheduling. References `PFound.Compression`. |
| `PFound.ContentDelivery` | `Runtime/` | runtime | The Unity layer: `AssetManager`, `AssetLoader`, `ContentCatalogService`, sources, bootstrap. `allowUnsafeCode: true`. |
| `PFound.ContentDelivery.Editor` | `Editor/` | editor-only | Authoring SOs + the Scriptable Build Pipeline build path (`includePlatforms: [Editor]`). |
| `PFound.ContentDelivery.BestHttp` | `BestHttp/` | runtime (optional) | Define-constrained on `PFOUND_BESTHTTP`; the BestHTTP transport adapter. |
| `PFound.ContentDelivery.Core.Tests` | `Core/Tests/` | pure C# tests | Standalone csc/mono runner (`Program.cs`), `noEngineReferences: true`. |
| `PFound.ContentDelivery.Tests` | `Tests/` | edit/play tests | NUnit suites over the runtime + editor layers. |

All shipped assemblies are `autoReferenced: false` — a consumer references them explicitly.

## Dependencies

- **PFound modules:** `PFound.Compression` (LZMA transfer compression + decompression, used by Core
  and the build pipeline).
- **Third-party (runtime):** UniTask (`Cysharp.Threading.Tasks`), `Unity.Collections`,
  `Unity.Mathematics`.
- **Third-party (editor):** `Unity.ScriptableBuildPipeline` / `Unity.ScriptableBuildPipeline.Editor`
  (SBP) for deterministic bundle builds.
- **Third-party (optional):** BestHTTP — only compiled when the `PFOUND_BESTHTTP` scripting define is
  set (see [Conditional Compilation](#conditional-compilation)).

## Key Types

**`PFound.ContentDelivery` (Runtime)**
- `AssetManager` — static, process-wide async resolution + a ref-counted asset cache over the source chain.
- `IAssetSource` — the resolve seam; `ResourcesAssetSource` is the built-in fallback.
- `RemoteBundleAssetSource` — the primary source: resolves through the catalog, provisions and loads bundles, phased preload.
- `ContentCatalogService` — app-owned singleton for *synchronous* catalog-driven load/instantiate. **Dormant synchronous featureset-parity capability**: all runtime loading is async (`AssetManager` / `RemoteBundleAssetSource`); no production code uses the sync service.
- `RemoteCatalogResolver` (+ `CatalogResolveResult`) — the shared, fail-soft remote→cache→embedded catalog resolution (decode only; does NOT init the sync service). Both the async runtime and the sync service route through it.
- `AssetLoader : IDisposable` — owner-scoped handle that releases everything it took on `Dispose`.
- `AssetLoaderId` — per-owner ref-count key (`FixedString64Bytes`); `Global` for un-owned calls.
- `AsyncAssetLoadHandle<T>` — allocation-free awaitable/pollable value handle; carries `Release()`.
- `AssetLoadingStatus` — `NotLoaded` / `LoadingBundles` / `LoadingAsset` / `Loaded` / `Failed`.
- `AssetAddress` — string-key load address (implicit from `string`); `AssetReference<T>` — its serializable, inspector-friendly form.
- `UnmanagedAssetAddress` — Burst-friendly `FixedString128Bytes` address (`ToManaged()`).
- `AssetPhase` — `Essential` / `Early` / `Standard` / `Deferred` phased-delivery bands.
- `ContentDeliveryBootstrap` — the wiring entry point; `DefaultTransport` seam. Installs `ContentDeliveryRuntime` + enables `SpriteAtlasBinder`.
- `ContentDeliveryRuntime` — optional `MonoBehaviour` host (hidden, `DontDestroyOnLoad`): pumps the deferred-unload queue each frame and forwards `Application.lowMemory` to `AssetManager.HandleLowMemory`. `Install()` / `Uninstall()`.
- `SpriteAtlasBinder` — late-binds bundle-packed `SpriteAtlas`es (Include-in-Build off) by hooking `SpriteAtlasManager.atlasRequested` and loading the atlas from its bundle. `Enable(resolver)` / `Disable()`.
- `ContentSceneLoader` (+ `SceneBundleTicket`, `LoadedContentScene`) — loads a scene packed in a content bundle: makes the bundle resident, drives `SceneManager` by the in-bundle scene path, and unloads scene + bundle together.
- `ContentDeliveryPaths` — well-known StreamingAssets / cache locations.
- `ContentPlatform` — active/editor platform folder + embedded/remote bundle path resolution.
- `ContentMemoryReporter` → `ContentMemoryReport` (`AssetMemoryRow` / `BundleMemoryRow` / `LoaderRef`) — residency diagnostics.
- `ContentServiceOptions`, `CatalogLoadResult` — `ContentCatalogService` init/acquire types.
- `ContentDeliveryInitResult` — outcome of `ContentDeliveryBootstrap.InitializeWithFallbackAsync` (async, fail-soft boot with catalog fallback).
- `IBundleMemorySource`, `LoadedBundleRegistry` (internal), `SyncBundleRegistry`, `EmbeddedCatalogReader`, `CatalogJson`, `UnityWebRequestTransport` (in `Transport/`).

**`PFound.ContentDelivery.Core`**
- `Catalog` — in-memory address→asset→bundle graph; `TryResolveAsset`, `GetBundleClosure`, `GetPackClosure`, `AssetsUpToPhase`, `AssetsInPhase`, `PhasesUpTo`, `AssetsWithLabel`, `Version`.
- `CatalogBundle` / `CatalogAsset` / `CatalogPack` / `BundleCompression` — catalog records.
- `BundleProvisioner` — download → hash-verify → (LZMA-)decompress → content-addressed disk cache (1×).
- `DownloadScheduler` (+ `DownloadSchedulerOptions`, `SchedulerProgress`, `SchedulerResult`, `SchedulerConcurrency`) — concurrent multi-bundle provisioning with a **split retry policy** (distinct transient-network vs integrity/corrupt budgets + backoffs), a per-transfer **stall watchdog** (`StallTimeout`), disk precheck, and an optional `DownloadSpeedMeter` feed.
- `DownloadSpeedMeter` (`DownloadSpeedRating`) — classifies throughput Good/Bad against a byte/sec threshold and raises transition events (`RatingChanged` / `GoodDetected` / `BadDetected`).
- `DeferredUnloadQueue<TKey>` — frame-delayed release queue (grace window before unload; `Enqueue` / `Cancel` / `Pump` / `Flush`); backs `AssetManager.DeferredUnloadFrames`.
- `CatalogContentDownloader` (`CatalogContentResult`) — downloads every missing remote bundle for the active catalog.
- `IDownloadTransport` — the thin HTTP fetch boundary; `ContentDeliveryException` (`Retryable`) + typed subclasses.
- `RemoteContentConfig` — (origin, platform folder, catalog file name) coordinates; `IsOffline`, `IsUsable`, URL resolvers.
- `ContentEnvironments` — dev/staging/prod → CDN origin selection.
- `IContentHasher` — `Sha256ContentHasher` (Core default) / `XxHash3ContentHasher` (runtime/build default); `ContentHash`.
- `CatalogCodec` — decodes catalog bytes (binary or JSON); `CatalogAcquisitionPlan` / `CatalogSource` — embedded-vs-cached-vs-download decision.
- `RemoteCatalogPointerReader` (`RemoteCatalogPointer`), `AssetBundleLayout`.

**`PFound.ContentDelivery.Editor`**
- `AssetGroup` (`AssetEntry`, `DistributionMode`, `BundlePackingMode`) — authored content unit.
- `ContentBuildManifest` (`ContentSet`, `BuildSelectionMode`) — curated set-based build entry point; `ResolveGroups`.
- `CatalogEditorConfig` (`ContentEnvironmentEntry`, `BuildMode`) — the build "how": platform, mode, env, catalog versioning.
- `BundleBuildPipeline` (`ContentBuildReport`, `BundleReportEntry`) — the SBP build.
- `CatalogBuilder` — catalog JSON emission; `ContentDeliveryMenu` / `CatalogEditorBuildRunner` — menu entry points.
- `BuildScope` / `BuildScopeFilter`, `BundleDuplicateAnalyzer`, `ContentBuildReportExporter`.
- `EditorFastPathMode` (`[InitializeOnLoad]`) + `EditorAssetSource` + `EditorAddressMap` — the play-mode fast path.
- `ICdnUploader` + `DirectoryUploader` / `FtpUploader` / `BunnyCdnUploader` / `S3Uploader`, `CdnUpload`.
- `AssetReferenceDrawer` — the `AssetReference<T>` inspector dropdown.

## Public API

**Async loads — `AssetManager` (static, main-thread):**
```csharp
AsyncAssetLoadHandle<T> LoadAssetAsync<T>(AssetAddress) where T : Object;
AsyncAssetLoadHandle<T> LoadAssetAsync<T>(AssetAddress, AssetLoaderId);
AsyncAssetLoadHandle<T> LoadAssetAsync<T>(UnmanagedAssetAddress[, AssetLoaderId]);   // Burst-friendly
UniTask<T>              InstantiateAsync<T>(AssetAddress[, AssetLoaderId]);
void UnloadAsset(AssetAddress[, AssetLoaderId]);
void Destroy(Object instance);
void RegisterSource(IAssetSource);      // inserts at the FRONT (index 0) — highest priority
bool UnregisterSource(IAssetSource);
bool IsAssetLoaded(AssetAddress);
int  RefCountFor(AssetAddress, AssetLoaderId);
int  RefCount(AssetAddress);            // TOTAL references across all owners (0 if not resident)

// Deferred unload (optional): keep zero-ref entries resident for N frames (re-load within the window
// revives them). ContentDeliveryRuntime pumps the queue; 0 = immediate unload (default).
int  DeferredUnloadFrames { get; set; }
void PumpDeferredUnloads(int currentFrame);   // release entries whose window elapsed (host-driven)
void FlushDeferredUnloads();                   // release all pending now
void HandleLowMemory();                        // flush non-pinned residency + UnloadUnusedAssets + GC
event Action LowMemoryHandled;
```
`AsyncAssetLoadHandle<T>` exposes `Status`, `IsDone` / `IsTerminal`, `Result`, `UniTask Task`,
`Release()`, and is directly awaitable — the awaiter returns the asset, `null` on a clean miss, and
rethrows the captured exception on a pipeline failure.

**Scoped loader — `AssetLoader : IDisposable`:**
```csharp
new AssetLoader(FixedString64Bytes id);           // labels the owner for diagnostics
// same LoadAssetAsync / InstantiateAsync / UnloadAsset / Destroy surface, all scoped to this owner
void Dispose();   // destroys its instances, then releases every address it still holds
```

**Shared catalog resolution — `RemoteCatalogResolver` (static):**
```csharp
static UniTask<CatalogResolveResult> ResolveAsync(RemoteContentConfig, ContentServiceOptions, CancellationToken = default);
// fail-soft embedded → cached → download; decode only, does NOT init the sync service.
// CatalogResolveResult: bool Success; Catalog Catalog; CatalogSource Source; string Error.
```

**Sync loads — `ContentCatalogService` (`Current` singleton, dormant parity capability):**
```csharp
static ContentCatalogService Initialize(string catalogFileNameNoExt, Catalog, ContentServiceOptions);
static UniTask<CatalogLoadResult> AcquireCatalogAsync(RemoteContentConfig, ContentServiceOptions, CancellationToken); // fail-soft embedded → cached → download (routes through RemoteCatalogResolver)
UniTask<CatalogContentResult>     DownloadCatalogContentAsync(RemoteContentConfig, IProgress<SchedulerProgress>, CancellationToken);
T          LoadAsset<T>(AssetAddress, int count = 1) where T : Object;   // blocks; null on a clean miss
void       Unload(AssetAddress);
GameObject Instantiate(AssetAddress[, Transform parent | Vector3, Quaternion, Transform]);
T          Instantiate<T>(AssetAddress) where T : Component;
void       Destroy(Object instance);
Catalog Catalog { get; }   string CatalogFileName { get; }
```

**Bootstrap — `ContentDeliveryBootstrap` (static):**
```csharp
static IDownloadTransport DefaultTransport { get; set; }   // lazily UnityWebRequestTransport
static UniTask<RemoteBundleAssetSource> InitializeAsync(
    string remoteBaseUrl, string catalogUrl = null, string cacheDirectory = null,
    IDownloadTransport transport = null, string localBaseUrl = null, IContentHasher hasher = null);
static UniTask<RemoteBundleAssetSource> InitializeAsync(
    ContentEnvironments environments, /* …same optionals… */);   // env-selected CDN origin
static UniTask<ContentDeliveryInitResult> InitializeWithFallbackAsync(
    RemoteContentConfig remoteConfig, ContentServiceOptions options = null, CancellationToken = default);
// async, fail-soft boot: resolves the catalog (embedded → cached → download) then registers the source +
// installs runtime wiring; returns { Success, RemoteBundleAssetSource Source, CatalogSource Origin, Error }
// so a consumer never touches the sync ContentCatalogService.
```

**Phased preload — `RemoteBundleAssetSource`:**
```csharp
UniTask PreloadAsync(AssetPhase | int maxPhaseInclusive);                  // bring content up to a phase onto disk
UniTask PreloadAsync(AssetPhase | int maxPhaseInclusive, IProgress<SchedulerProgress> progress, CancellationToken = default);
// concurrent (scheduler) fetch + retry with byte-level aggregate progress; the no-progress overloads route here too
UniTask PreloadPhasesSequentialAsync(AssetPhase | int maxPhaseInclusive);  // phase-by-phase, each gated on the last
```

**Scenes from bundles — `ContentSceneLoader` / `RemoteBundleAssetSource`:**
```csharp
// Load a scene packed in a content bundle: provisions + loads its bundle, then drives SceneManager by the
// in-bundle scene path. The handle owns the bundle reference until the scene is unloaded.
static UniTask<LoadedContentScene> ContentSceneLoader.LoadSceneAsync(
    RemoteBundleAssetSource source, AssetAddress sceneAddress, LoadSceneMode mode = Single);
// LoadedContentScene: Scene, ScenePath, IsValid; UnloadAsync() (unloads scene + releases bundle), ReleaseBundleOnly()
// Low-level seam: source.AcquireSceneBundleAsync(addr) → SceneBundleTicket (Token, ScenePath); source.ReleaseSceneBundle(ticket)
```

**Bundle-packed SpriteAtlases — `SpriteAtlasBinder` (static):**
```csharp
static void Enable(Func<string, AssetAddress> resolveAddress = null);  // hook SpriteAtlasManager.atlasRequested
static void Disable();                                                 // default resolver: address == atlas tag
// On a late-bound atlas request it loads the atlas (Include-in-Build off, packed into a bundle) via AssetManager
// under the SpriteAtlasBinder.AtlasLoader owner and hands it to the engine callback.
```

**Download policy — `DownloadSchedulerOptions` / `DownloadSpeedMeter`:**
```csharp
// split retry: transient-network vs integrity(corrupt) get INDEPENDENT budgets/backoffs + a stall watchdog
int      MaxItemRetries;         TimeSpan RetryBackoff;          // transient (timeout/reset/5xx), exponential
int      MaxIntegrityRetries;    TimeSpan IntegrityRetryBackoff; // corrupt bytes (hash mismatch / decompress), fixed
TimeSpan StallTimeout;           // per-attempt watchdog: no completion in the window → cancel + retry as transient
DownloadSpeedMeter SpeedMeter;   // fed the aggregate bytes/sec each progress tick

var meter = new DownloadSpeedMeter(slowThresholdBytesPerSecond);
meter.RatingChanged += r => ...;  meter.BadDetected += ...;  meter.GoodDetected += ...;   // fire on transitions only
meter.Sample(bytesPerSecond);              // or meter.Sample(elapsedSeconds, cumulativeBytes) with CheckInterval
```

**Residency under memory pressure — `AssetManager` + `ContentDeliveryRuntime`:**
```csharp
ContentDeliveryRuntime.Install();          // idempotent host (bootstrap calls it): frame pump + Application.lowMemory
AssetManager.DeferredUnloadFrames = 2;     // opt into a grace window before unload
// On Application.lowMemory the host calls AssetManager.HandleLowMemory() → flush deferred residency + sweep + GC.
```

**Diagnostics — `ContentMemoryReporter`:**
```csharp
static ContentMemoryReport Capture(bool deep = false);   // deep also measures runtime memory via the Profiler
// report: TotalLoadedAssets/Bundles, ref-count + size roll-ups, ByLoaderTotals, ToString(), ToJson()
```

## Setup / wiring

`AssetManager` is a static seam and `ContentCatalogService` is a plain C# singleton — the core needs
**no MonoBehaviour**. The lifecycle is: author + build content in the editor, then call **one
initialize** at app startup before the first load.

There is one **optional** engine-lifecycle host, `ContentDeliveryRuntime` (a hidden `DontDestroyOnLoad`
`MonoBehaviour`, installed idempotently by `ContentDeliveryBootstrap.InitializeAsync`): it pumps the
deferred-unload queue each frame and forwards `Application.lowMemory` to `AssetManager.HandleLowMemory`.
It is only needed when you set `AssetManager.DeferredUnloadFrames > 0` or want the low-memory hook —
immediate-unload use (the default) needs nothing. `ContentCatalogService`-only apps can call
`ContentDeliveryRuntime.Install()` themselves.

### 1. Author content (editor)

Create the ScriptableObjects (both under the `PFound/Content Delivery/` create menu; place them
anywhere under `Assets` — the pipeline finds them via `AssetDatabase`):

- **`AssetGroup`** — one per logical bundle. Set `BundleName` (defaults to the asset file name),
  `Distribution` (`Local`/`Remote`), `Packing` (`PackTogether`/`PackSeparately`), `Phase`, an optional
  content-`Pack` id, `ExcludeInProduction`, and the `Entries` (each an asset + stable `Address` +
  free-form `Labels`).
- **`CatalogEditorConfig`** — the "how" of a build, centered on the `OfflineBuild` switch (force every
  group into StreamingAssets and strip remote entries). Carries `BuildPlatform`, `Mode`
  (`Development`/`Production` — Production drops `ExcludeInProduction` groups), `GameId`, the shared
  `Environments` + `ActiveEnvironment`, the `UploadAfterBuild` gate, and catalog versioning:
  `BuildNumberOverride` (empty = auto-increment last+1; a number forces the next build #, clamped to
  ≥ last). `CatalogFileName()` = `catalog_<gameId>_v<appVersion>_b<build>.json`; `ToRemoteConfig()`
  produces the runtime `RemoteContentConfig` so build and runtime never drift. (Set-selection — the old
  `Scope`/`GroupsToBuild` — moved OUT to `ContentBuildManifest`.)
- **`ContentBuildManifest`** — the "what" of a build: the curated, set-based build **entry point**.
  `Sets` (each a `ContentSet{ string Id, List<AssetGroup> Groups }`) + `AlwaysIncluded` core groups.
  `ResolveGroups(mode, setId)` = union of all sets (`AllSets`) or one named set (`SingleSet`), each
  ∪ `AlwaysIncluded`. The `Build` button resolves the scope and hands it to `CatalogEditorBuildRunner`;
  the shared runner applies the Production `ExcludeInProd` filter (§`config.Mode`) and stamps the
  dev/prod posture into the catalog content as `buildMode`. Generic — a plain `string Id` set key, no
  game vocabulary (a game project maps its own ids onto `ContentSet.Id` in its own layer).

**Authoring-inspector convention (Odin selector is broken on this Unity):** the Odin enum/dropdown
selector window throws `MissingMethodException` when clicked on this Unity version. So EVERY dropdown in
a ContentDelivery authoring SO (`Mode`, `Selection`, `Distribution`, `Packing`, `Phase`, `BuildPlatform`,
`ActiveEnvironment`, `SelectedSetId`) uses `[CustomValueDrawer(nameof(DrawX))]` + a one-liner over Unity's
native IMGUI popup (`EditorGUILayout.EnumPopup` / `EditorGUILayout.Popup`) — never a plain Odin enum field
or `[ValueDropdown]`. Add new dropdowns the same way.

### 2. Build (editor)

Run a menu item under `PFound/Content Delivery/`:
- **`ContentBuildManifest` → Build button** — the authored path: pick `AllSets` or a `SingleSet`, hit
  Build. This is the normal way to build a set-scoped (or hub-wide) catalog.
- `Build Content (All Groups)` — raw "build every project group" debug shortcut (`ContentDeliveryMenu`).
- `App Build (from Catalog Editor Config)` — config-driven (`CatalogEditorBuildRunner`, all project
  groups), honoring the offline switch + upload gate; `App Clear Embedded Package` clears the staged
  StreamingAssets content. (The old `Selected Groups` / `Production` menus were removed — set-selection
  is the manifest's job, and Production is now `CatalogEditorConfig.Mode`.)
- `Analyze Duplicate Dependencies` — reports assets implicitly embedded in more than one bundle.

The build runs on the **Scriptable Build Pipeline** (`BundleBuildPipeline` → `ContentPipeline`):
groups become `AssetBundleBuild`s, each output is content-addressed by its hash, and the runtime
catalog JSON is emitted. Output goes to `<project>/ContentBuild/`; **Local** bundles + the catalog are
staged into `Assets/StreamingAssets/PFoundContent/`, **Remote** bundles land in `publish/` for upload
to the CDN origin. Bundle bytes are LZMA-compressed for transfer by default (`BundleCompression.Lzma`,
Unity-uncompressed underneath) — smaller downloads than Unity's LZ4; the runtime decompresses.

Path constants live in `ContentDeliveryPaths`: `ContentFolderName = "PFoundContent"`,
`CatalogFileName = "catalog.json"`, `StreamingAssetsContentDirectory`/`StreamingAssetsContentUrl`,
`DefaultCacheDirectory` = `persistentDataPath/PFoundContent/cache`.

### 3. Initialize once at startup

Call from your bootstrap flow (a startup MonoBehaviour `Awake` / an async init step) **before** any
`AssetManager` / `ContentCatalogService` load:

```csharp
// Remote bundles + CDN, catalog from StreamingAssets by default.
// Registers a RemoteBundleAssetSource at the FRONT of the source chain.
RemoteBundleAssetSource source = await ContentDeliveryBootstrap.InitializeAsync(
    remoteBaseUrl: "https://cdn.example.com/game");

// then, anywhere:
AsyncAssetLoadHandle<Texture2D> handle = AssetManager.LoadAssetAsync<Texture2D>("ui/hero");
Texture2D tex = await handle;
// ...
handle.Release();
```

An overload takes a `ContentEnvironments` for env-selected origins. For app-owned catalog control,
`ContentCatalogService.Initialize(name, catalog, options)` (you deserialize the `Catalog`), or
`ContentCatalogService.AcquireCatalogAsync(remoteConfig, options)` (fail-soft embedded → cached →
download), then `DownloadCatalogContentAsync(...)` to provision remote bundles up front.

The `IContentHasher` you pass must match the hasher the catalog was **built** with (default
`XxHash3ContentHasher`).

### 4. The IAssetSource chain (resolution order)

`AssetManager` resolves an address by walking an ordered `List<IAssetSource>` and taking the first
non-null result; a `null` from a source lets the next one try, and a miss is never cached (stays
retryable). `RegisterSource` inserts at index 0 (front), so the **most recently registered source has
the highest priority**:

```
[ EditorAssetSource ]      ← editor fast-path only, front-most when on (§5)
[ RemoteBundleAssetSource ]← primary; registered by ContentDeliveryBootstrap.InitializeAsync
[ ResourcesAssetSource ]   ← built-in fallback, always last (Resources.LoadAsync by address)
```

`RemoteBundleAssetSource` resolves through the `Catalog`, provisions the owning bundle **and its full
dependency closure** (`GetBundleClosure`, dependencies-first) via `BundleProvisioner`
(download → hash-verify → LZMA-decompress → content-addressed disk cache), loads them ref-counted
through `LoadedBundleRegistry`, and pulls the asset out by its in-bundle name (including `main[sub]`
sub-asset selectors for sprite-in-atlas / mesh-in-fbx). An address unknown to the catalog returns
`null` so the fallback can serve it.

### 5. Editor fast-path (iteration)

`EditorFastPathMode` (`[InitializeOnLoad]`, toggle at `PFound/Content Delivery/Editor Fast-Path
(project assets)`, `Enabled` persisted in `EditorPrefs`, default **on**) registers an
`EditorAssetSource` (built from `EditorAddressMap.Build()`) at the front of the chain, so Play mode
loads live project assets instead of yesterday's built bundles. It re-syncs on domain load, on
entering Play, and on toggle. Turn it **off** to validate the true built-bundle path before shipping.

## File Structure

```
ContentDelivery/
├─ README.md · MODULE.md
├─ Core/
│  ├─ Runtime/                       # PFound.ContentDelivery.Core — engine-free (noEngineReferences)
│  │  ├─ Catalog.cs                  # address→asset→bundle graph + closures/phases/labels
│  │  ├─ CatalogBinary.cs · CatalogCodec.cs     # binary/JSON catalog decode
│  │  ├─ ContentCatalogIndex.cs      # resolution index (alias/sub-asset)
│  │  ├─ BundleProvisioner.cs        # download → verify → decompress → 1× disk cache
│  │  ├─ DownloadScheduler.cs        # concurrent provisioning + split retry (network/integrity) + stall watchdog
│  │  ├─ DownloadSpeedMeter.cs       # Good/Bad throughput classification + transition events
│  │  ├─ DeferredUnloadQueue.cs      # frame-delayed release queue (grace window before unload)
│  │  ├─ CatalogContentDownloader.cs # download all missing remote bundles for a catalog
│  │  ├─ CatalogAcquisitionPlan.cs   # embedded vs cached vs download decision (CatalogSource)
│  │  ├─ RemoteContentConfig.cs · ContentEnvironments.cs · AssetBundleLayout.cs
│  │  ├─ RemoteCatalogPointerReader.cs
│  │  ├─ IDownloadTransport.cs · ContentDeliveryExceptions.cs
│  │  ├─ IContentHasher.cs · ContentHash.cs      # Sha256ContentHasher (Core default)
│  │  └─ PFound.ContentDelivery.Core.asmdef
│  └─ Tests/                         # standalone csc/mono runner (Program.cs)
├─ Runtime/                          # PFound.ContentDelivery — the Unity layer
│  ├─ AssetManager.cs · AssetLoader.cs · AssetLoaderId.cs
│  ├─ AsyncAssetLoadHandle.cs · IAssetSource.cs (+ ResourcesAssetSource)
│  ├─ RemoteBundleAssetSource.cs · LoadedBundleRegistry.cs · IBundleMemorySource.cs
│  ├─ ContentSceneLoader.cs          # scene-from-bundle load (SceneBundleTicket, LoadedContentScene)
│  ├─ SpriteAtlasBinder.cs           # late-bind bundle-packed SpriteAtlases (atlasRequested)
│  ├─ ContentDeliveryRuntime.cs      # optional host: deferred-unload pump + Application.lowMemory hook
│  ├─ ContentCatalogService.cs · SyncBundleRegistry.cs · EmbeddedCatalogReader.cs · CatalogJson.cs
│  ├─ ContentDeliveryBootstrap.cs · ContentDeliveryPaths.cs · ContentPlatform.cs
│  ├─ AssetAddress.cs · UnmanagedAssetAddress.cs · AssetReference.cs · AssetPhase.cs
│  ├─ XxHash3ContentHasher.cs        # runtime/build default hasher
│  ├─ ContentMemoryReporter.cs · ContentMemoryReport.cs
│  ├─ Transport/UnityWebRequestTransport.cs      # dependency-free default transport
│  └─ PFound.ContentDelivery.asmdef
├─ Editor/                           # PFound.ContentDelivery.Editor (Editor-only)
│  ├─ AssetGroup.cs · CatalogEditorConfig.cs · AssetReferenceDrawer.cs
│  ├─ BundleBuildPipeline.cs · CatalogBuilder.cs      # Scriptable Build Pipeline build + catalog emit
│  ├─ BuildScope.cs · BundleDuplicateAnalyzer.cs · ContentBuildReportExporter.cs
│  ├─ ContentDeliveryMenu.cs · CatalogEditorBuildRunner.cs   # menu entry points
│  ├─ EditorFastPathMode.cs · EditorAssetSource.cs · EditorAddressMap.cs
│  ├─ CdnUploader.cs                 # ICdnUploader: Directory/Ftp/BunnyCdn/S3
│  └─ PFound.ContentDelivery.Editor.asmdef
├─ BestHttp/                         # PFound.ContentDelivery.BestHttp — PFOUND_BESTHTTP only
│  ├─ BestHttpTransport.cs · BestHttpDefaultTransport.cs
│  └─ PFound.ContentDelivery.BestHttp.asmdef
└─ Tests/                            # PFound.ContentDelivery.Tests (NUnit edit/play)
```

## Conditional Compilation

The default download transport is `UnityWebRequestTransport` (dependency-free) — lazily assigned to
`ContentDeliveryBootstrap.DefaultTransport` the first time it is read.

Adding **`PFOUND_BESTHTTP`** to the project's Scripting Define Symbols compiles the
`PFound.ContentDelivery.BestHttp` assembly (define-constrained). Its `BestHttpDefaultTransport` runs a
`[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]` hook that sets `DefaultTransport = new
BestHttpTransport()` — making BestHTTP (HTTP/2, connection reuse) the default **without the foundation
taking a hard reference** to the optional library: the optional assembly reaches in, not the reverse.
Both source files are wrapped in `#if PFOUND_BESTHTTP`. Assign `DefaultTransport` yourself (or pass a
`transport:` to `InitializeAsync`) for any other custom transport.

## Threading

`AssetManager`, `AssetLoader`, `ContentCatalogService` and the sources are **main-thread only**.
`DownloadScheduler` and `BundleProvisioner` push download + LZMA decompression off-thread (under
concurrency gates that double as memory guards), then hand verified bytes back for main-thread bundle
loading. The Core layer is BCL-only and free of Unity types, so its logic is unit-testable off-engine.

## Downstream Dependents

None within PFound today — ContentDelivery is a leaf capability a game project consumes directly
(author groups, build, initialize at startup, load by address). A game's own bootstrap/startup flow is
the natural caller.

## Limitations / Known Gaps

- **Catalog updates are whole-catalog swaps**, not shard/diff: a changed `Catalog.Version` re-pulls
  content; there is no partial catalog merge.
- **Hasher must match end-to-end.** The runtime source and the build must use the same `IContentHasher`
  (runtime/build default `XxHash3ContentHasher`; `BundleProvisioner`'s bare default is
  `Sha256ContentHasher`). A mismatch fails hash verification.
- **Environments select a load path, not a catalog variant** — the same bundles/hashes, only the CDN
  origin differs.
- **Deep memory measurement** (`ContentMemoryReporter.Capture(deep: true)`) only yields real runtime
  sizes in the editor / development builds; elsewhere it reports unmeasured (`-1`).
- **A missing/failed resolve is not cached** — repeated loads of an unknown address re-walk the chain
  each time (intentional: keeps misses retryable).

## Testing

The engine-free catalog/provisioning/scheduling core has a standalone csc/mono runner
(`Core/Tests/Program.cs`, asmdef `noEngineReferences: true`); the runtime + editor layers have NUnit
EditMode/PlayMode suites under `Tests/`.
</content>
</invoke>
