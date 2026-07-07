# PFound.ContentDelivery

Addressable content delivery: author assets into groups, build AssetBundles + a content catalog,
ship them locally (StreamingAssets) or remotely (CDN), and load them at runtime by string address
with reference counting. The catalog model and bundle provisioning are engine-free pure C#; the
runtime and authoring layers sit on top.

## Model

- **Address** — an asset is loaded by an `AssetAddress` (a string key). `AssetReference<T>` is the
  serializable, inspector-friendly form that implicitly converts to `AssetAddress`.
- **Group** — an `AssetGroup` ScriptableObject lists the assets to ship and their distribution
  (Local/Remote), packing, and phase. Groups are the unit the build pipeline bundles.
- **Catalog** — the build emits a `Catalog` (JSON) mapping addresses to bundles + content hashes.
  It ships embedded in `StreamingAssets/PFoundContent/catalog.json` and/or on a CDN.
- **Sources** — loads resolve through a chain of `IAssetSource`s (registered on `AssetManager`);
  `RemoteBundleAssetSource` serves catalog bundles, `EditorAssetSource` serves live project assets
  in the editor fast-path.
- **Ref-counting** — every load takes a reference; the asset/bundle unloads when the count returns
  to zero. `AssetLoaderId` scopes references to an owner.

## Public API

**Async loads (`AssetManager`, static, main-thread):**
- `AsyncAssetLoadHandle<T> LoadAssetAsync<T>(AssetAddress)` / `(AssetAddress, AssetLoaderId)`
- `UniTask<T> InstantiateAsync<T>(AssetAddress)` / `(AssetAddress, AssetLoaderId)`
- `void UnloadAsset(AssetAddress)` / `(AssetAddress, AssetLoaderId)`, `void Destroy(Object instance)`
- `void RegisterSource(IAssetSource)`, `bool UnregisterSource(IAssetSource)`,
  `bool IsAssetLoaded(AssetAddress)`

`AsyncAssetLoadHandle<T>` exposes `Status`, `IsDone`, `Result`, `UniTask Task`, `Release()`, and is
directly awaitable (returns the asset, `null` on a clean miss, rethrows on failure).

**Sync loads (`ContentCatalogService`, `Current` singleton):**
- `T LoadAsset<T>(AssetAddress, int count = 1)`, `void Unload(AssetAddress)`
- `GameObject Instantiate(AssetAddress[, parent | position, rotation, parent])`,
  `T Instantiate<T>(AssetAddress)`, `void Destroy(Object instance)`

**Scoped loader (`AssetLoader : IDisposable`):** `new AssetLoader(FixedString64Bytes id)` with the
same `LoadAssetAsync`/`InstantiateAsync`/`UnloadAsset`/`Destroy`; `Dispose()` releases everything it
owns — use it for a screen/system that should drop all its assets at once.

**Diagnostics:** `ContentMemoryReporter.Snapshot(deep)` → `ContentMemoryReport` (`ToString()` /
`ToJson()`), per-asset and per-bundle ref-counts and sizes. `AssetPhase` (`Essential`/`Early`/
`Standard`/`Deferred`) bands phased delivery.

## Setup / wiring

There is **no MonoBehaviour host and no `DontDestroyOnLoad`** — `AssetManager` is a static seam and
`ContentCatalogService` is a plain C# singleton. Wiring is: author + build content in the editor,
then call **one initialize** at app startup before the first load.

**1. Author content (editor).** Create the ScriptableObjects (both under the `PFound/Content
Delivery/` create menu; place them anywhere in `Assets` — the pipeline finds them via
`AssetDatabase`):
- `AssetGroup` — one per logical bundle; set each entry's address, `Distribution` (Local/Remote),
  packing, and phase.
- `CatalogEditorConfig` — the master build config: platform, offline toggle, build scope,
  environments (dev/staging/prod origin URLs), and the upload gate.

**2. Build (editor).** Run `PFound/Content Delivery/Build Content (All Groups | Selected Groups |
Production)`. Output goes to `<project>/ContentBuild/`; Local bundles + the catalog are staged into
`Assets/StreamingAssets/PFoundContent/`, Remote bundles are uploaded to the configured CDN origin.
`Analyze Duplicate Dependencies` reports assets embedded in more than one bundle. Path constants
live in `ContentDeliveryPaths` (`ContentFolderName = "PFoundContent"`, `CatalogFileName =
"catalog.json"`, `StreamingAssetsContentDirectory`/`Url`, `DefaultCacheDirectory` =
`persistentDataPath/PFoundContent/cache`).

**3. Initialize once at startup.** Call from your bootstrap flow (e.g. a startup MonoBehaviour's
`Awake` / an async init step) **before** any `AssetManager` / `ContentCatalogService` load:

```csharp
// Remote bundles + CDN, catalog from StreamingAssets by default:
var source = await ContentDeliveryBootstrap.InitializeAsync(
    remoteBaseUrl: "https://cdn.example.com/game");   // registers RemoteBundleAssetSource

// then, anywhere:
var handle = AssetManager.LoadAssetAsync<Texture2D>("ui/hero");
Texture2D tex = await handle;
// ...
handle.Release();
```

An overload takes a `ContentEnvironments` for env-selected origins. For app-owned catalog control,
`ContentCatalogService.Initialize(name, catalog, options)` (deserialize the `Catalog` yourself), or
`ContentCatalogService.AcquireCatalogAsync(remoteConfig, options)` (fail-soft embedded → cached →
download) then `DownloadCatalogContentAsync(...)` to provision remote bundles.

**4. Transport.** `IDownloadTransport` is pluggable via `ContentDeliveryBootstrap.DefaultTransport`
(get/set). It lazily defaults to `UnityWebRequestTransport` (dependency-free). Adding
`PFOUND_BESTHTTP` to Scripting Define Symbols compiles the `PFound.ContentDelivery.BestHttp`
assembly (define-constrained), whose `BestHttpTransport` self-registers as the default via
`[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]` — no foundation-side hard reference. Assign
`DefaultTransport` yourself for a custom transport. The `IContentHasher` you pass must match the
hasher the catalog was built with (default xxHash3).

**5. Editor fast-path (iteration).** `EditorFastPathMode` (`[InitializeOnLoad]`, toggle at
`PFound/Content Delivery/Editor Fast-Path (project assets)`, `Enabled` persisted, default on)
registers an `EditorAssetSource` at the front of the chain so Play mode loads live project assets
instead of yesterday's bundles. Turn it off to validate the true bundle path before shipping.

## Testing

The engine-free catalog/provisioning core has a standalone csc/mono runner
(`Core/Tests/Program.cs`, asmdef `noEngineReferences:true`); the runtime + editor layers have NUnit
EditMode/PlayMode suites under `Tests/`.

## Layout

- `Core/Runtime/` — engine-free catalog model, `IDownloadTransport`, bundle provisioning (depends
  on `PFound.Compression`). Assembly `PFound.ContentDelivery.Core`.
- `Runtime/` — `AssetManager`, `AssetLoader`, `ContentCatalogService`, sources, bootstrap. Assembly
  `PFound.ContentDelivery` (UniTask, Unity.Collections/Mathematics).
- `Editor/` — `AssetGroup`, `CatalogEditorConfig`, build pipeline, menu, fast-path. Assembly
  `PFound.ContentDelivery.Editor` (Editor-only).
- `BestHttp/` — optional transport, define-constrained on `PFOUND_BESTHTTP`. Assembly
  `PFound.ContentDelivery.BestHttp`.

All assemblies are `autoReferenced:false` — reference explicitly.

Part of the PFound modular Unity foundation.
</content>
