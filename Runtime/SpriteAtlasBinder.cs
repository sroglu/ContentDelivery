using System;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.U2D;

namespace PFound.ContentDelivery
{
    /// <summary>
    /// Serves bundle-packed <see cref="SpriteAtlas"/>es to the engine on demand. When a Sprite that lives in a
    /// "late-bound" atlas (built with <c>Include in Build</c> OFF and packed into a content bundle) is first
    /// referenced, Unity raises <see cref="SpriteAtlasManager.atlasRequested"/> with the atlas <em>tag</em> and a
    /// callback; the atlas is not in the player, so nothing satisfies it unless we load it from its owning bundle.
    /// <para>
    /// This binder hooks that event, maps the tag to a content <see cref="AssetAddress"/> (identity by default —
    /// the atlas is addressed by its tag — or a custom resolver), loads the atlas through <see cref="AssetManager"/>
    /// (which pulls + loads its bundle), and hands it back via the callback. The atlas keeps a reference on its
    /// bundle for the app's lifetime (atlases stay resident once bound), which is why it is loaded
    /// under a dedicated owner and never released here (atlases stay resident once bound). Enable it once at
    /// startup (the bootstrap does).
    /// </para>
    /// </summary>
    public static class SpriteAtlasBinder
    {
        /// <summary>The owner all late-bound atlases are loaded under (so residency is attributable in reports).</summary>
        public static readonly AssetLoaderId AtlasLoader = new AssetLoaderId("ContentDelivery.SpriteAtlas");

        private static bool s_enabled;
        private static Func<string, AssetAddress> s_resolveAddress;

        /// <summary>Whether the atlas-requested hook is currently installed.</summary>
        public static bool IsEnabled => s_enabled;

        /// <summary>
        /// Installs the <see cref="SpriteAtlasManager.atlasRequested"/> hook. Idempotent.
        /// </summary>
        /// <param name="resolveAddress">
        /// Maps an atlas tag to the content address of the atlas asset. Null uses identity (address == tag) — author
        /// the atlas's content address to match its tag, or supply a convention here.
        /// </param>
        public static void Enable(Func<string, AssetAddress> resolveAddress = null)
        {
            s_resolveAddress = resolveAddress ?? (tag => new AssetAddress(tag));
            if (s_enabled) return;
            SpriteAtlasManager.atlasRequested += OnAtlasRequested;
            s_enabled = true;
        }

        /// <summary>Removes the hook (teardown / tests).</summary>
        public static void Disable()
        {
            if (!s_enabled) return;
            SpriteAtlasManager.atlasRequested -= OnAtlasRequested;
            s_enabled = false;
        }

        private static void OnAtlasRequested(string tag, Action<SpriteAtlas> callback)
        {
            // The callback may be invoked asynchronously — Unity holds the sprite bind until it fires.
            BindAsync(tag, callback).Forget();
        }

        private static async UniTaskVoid BindAsync(string tag, Action<SpriteAtlas> callback)
        {
            AssetAddress address = s_resolveAddress(tag);
            var atlas = await AssetManager.LoadAssetAsync<SpriteAtlas>(address, AtlasLoader);
            if (atlas == null)
            {
                // Not ours (unknown to the catalog / not downloaded): leave the request unbound so any other
                // provider — or the built-in "master" atlas — can still serve it. Surface it for diagnosis.
                Debug.LogWarning($"[ContentDelivery] SpriteAtlas '{tag}' could not be resolved to a bundle-packed atlas (address '{address}').");
                return;
            }
            callback(atlas);
        }
    }
}
