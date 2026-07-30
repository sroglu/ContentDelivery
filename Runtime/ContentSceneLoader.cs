using System;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PFound.ContentDelivery
{
    /// <summary>
    /// A handle to a bundle-packed scene that has been acquired but not necessarily loaded into the engine: the
    /// <see cref="ScenePath"/> the engine scene loader needs, plus the <see cref="Token"/> that keeps its bundle
    /// resident. Obtained from <see cref="RemoteBundleAssetSource.AcquireSceneBundleAsync"/>.
    /// </summary>
    public readonly struct SceneBundleTicket
    {
        public readonly int Token;
        public readonly string ScenePath;

        public SceneBundleTicket(int token, string scenePath) { Token = token; ScenePath = scenePath; }

        public bool IsValid => Token != 0 && !string.IsNullOrEmpty(ScenePath);
        public static readonly SceneBundleTicket Invalid = new SceneBundleTicket(0, null);
    }

    /// <summary>
    /// A scene loaded from a content bundle, bound to the bundle that backs it. Unload through
    /// <see cref="UnloadAsync"/> (or <see cref="ReleaseBundleOnly"/>) so the scene AND its bundle are torn down
    /// together — releasing the bundle while its scene is still loaded would break the live scene's assets.
    /// </summary>
    public readonly struct LoadedContentScene
    {
        private readonly RemoteBundleAssetSource _source;
        private readonly SceneBundleTicket _ticket;

        public readonly Scene Scene;
        public string ScenePath => _ticket.ScenePath;
        public bool IsValid => _ticket.IsValid && Scene.IsValid();

        internal LoadedContentScene(RemoteBundleAssetSource source, SceneBundleTicket ticket, Scene scene)
        {
            _source = source; _ticket = ticket; Scene = scene;
        }

        /// <summary>Unloads the scene from the engine, then releases its backing bundle. Single-mode scenes cannot be
        /// unloaded this way (the engine forbids unloading the only scene) — load the next scene in Single mode to
        /// replace it, or call <see cref="ReleaseBundleOnly"/> after it is gone.</summary>
        public async UniTask UnloadAsync()
        {
            if (Scene.IsValid() && Scene.isLoaded)
                await SceneManager.UnloadSceneAsync(Scene).ToUniTask();
            _source?.ReleaseSceneBundle(_ticket);
        }

        /// <summary>Releases only the backing bundle reference (when the scene was already replaced, e.g. a Single-mode
        /// load elsewhere unloaded it for you).</summary>
        public void ReleaseBundleOnly() => _source?.ReleaseSceneBundle(_ticket);
    }

    /// <summary>
    /// Loads scenes packed into content bundles, wiring the two sides the engine keeps separate: making the scene's
    /// bundle resident (via <see cref="RemoteBundleAssetSource"/>) and driving the engine's own scene loader
    /// (<see cref="SceneManager"/>) by the in-bundle scene path. The returned <see cref="LoadedContentScene"/> owns
    /// the bundle reference for as long as the scene is live.
    /// </summary>
    public static class ContentSceneLoader
    {
        /// <summary>
        /// Provisions + loads the bundle for the scene at <paramref name="sceneAddress"/>, then loads the scene into
        /// the engine in <paramref name="mode"/>. The bundle stays resident until the returned handle is unloaded.
        /// Throws if the address does not resolve to a scene bundle (fail-fast — a bad scene address is a bug).
        /// </summary>
        public static async UniTask<LoadedContentScene> LoadSceneAsync(
            RemoteBundleAssetSource source, AssetAddress sceneAddress, LoadSceneMode mode = LoadSceneMode.Single)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            SceneBundleTicket ticket = await source.AcquireSceneBundleAsync(sceneAddress);
            if (!ticket.IsValid)
                throw new InvalidOperationException($"No scene bundle resolves for address '{sceneAddress}'.");

            await SceneManager.LoadSceneAsync(ticket.ScenePath, mode).ToUniTask();
            Scene scene = SceneManager.GetSceneByPath(ticket.ScenePath);
            return new LoadedContentScene(source, ticket, scene);
        }
    }
}
