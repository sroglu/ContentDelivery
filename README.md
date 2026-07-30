# PFound.ContentDelivery

Addressable content delivery: author assets into groups, build AssetBundles + a content catalog, ship
them locally (StreamingAssets) or remotely (CDN), and load them at runtime by string address with
per-owner reference counting. `AssetManager` is a static seam (author + build in the editor, then call
one initialize at startup); an optional `ContentDeliveryRuntime` host adds the frame pump for deferred
unload and the `Application.lowMemory` hook.

## Quick reference

```csharp
// 1. Wire the remote content system once at app startup (before any load).
RemoteBundleAssetSource source = await ContentDeliveryBootstrap.InitializeAsync(
    remoteBaseUrl: "https://cdn.example.com/game");   // registers the primary IAssetSource

// 2. Load by address, ref-counted; release when done.
AsyncAssetLoadHandle<Texture2D> handle = AssetManager.LoadAssetAsync<Texture2D>("ui/hero");
Texture2D tex = await handle;
handle.Release();

// Scope a screen/system's loads so they all drop at once:
using var loader = new AssetLoader("MainMenu");
var hero = await loader.InstantiateAsync<Transform>("props/hero");
```

| Surface | Type |
| --- | --- |
| Async load / instantiate | `AssetManager` (static) → `AsyncAssetLoadHandle<T>` |
| Owner-scoped loads | `AssetLoader : IDisposable` |
| Sync catalog-driven load | `ContentCatalogService` (`Current`) |
| Startup wiring | `ContentDeliveryBootstrap.InitializeAsync` |
| Scenes from bundles | `ContentSceneLoader.LoadSceneAsync` → `LoadedContentScene` |
| Bundle-packed SpriteAtlases | `SpriteAtlasBinder.Enable()` (auto via bootstrap) |
| Memory pressure / grace unload | `AssetManager.DeferredUnloadFrames`, `HandleLowMemory` + `ContentDeliveryRuntime` |
| Download policy | `DownloadSchedulerOptions` (split retry + stall watchdog), `DownloadSpeedMeter` |
| Authoring (editor) | `AssetGroup`, `CatalogEditorConfig` SOs + `PFound/Content Delivery/…` menu |
| Diagnostics | `ContentMemoryReporter.Capture(deep)` → `ContentMemoryReport` |

## Dependencies

`PFound.Compression`; UniTask, `Unity.Collections`/`Unity.Mathematics`; SBP (editor); BestHTTP
(optional, `PFOUND_BESTHTTP`). All assemblies `autoReferenced: false`.

## Docs

Deep reference: [MODULE.md](MODULE.md) — assemblies, the full API, the IAssetSource chain, the SBP
build path, editor fast-path, and `PFOUND_BESTHTTP` transport selection.

Part of the PFound modular Unity foundation.
</content>
