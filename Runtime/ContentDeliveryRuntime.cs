using UnityEngine;

namespace PFound.ContentDelivery
{
    /// <summary>
    /// The optional engine-lifecycle host for the content system's two frame/OS-driven concerns that a static
    /// <see cref="AssetManager"/> cannot service on its own:
    /// <list type="bullet">
    /// <item>pumping the deferred-unload queue every frame (so <see cref="AssetManager.DeferredUnloadFrames"/> can
    /// release entries once their grace window elapses), and</item>
    /// <item>forwarding <c>Application.lowMemory</c> to <see cref="AssetManager.HandleLowMemory"/> (release
    /// non-pinned residency + unused-asset sweep + GC under memory pressure).</item>
    /// </list>
    /// A single hidden, <c>DontDestroyOnLoad</c> instance is created by <see cref="Install"/> (idempotent; the
    /// bootstrap calls it). It survives scene loads and is not required for immediate-unload use — install it only
    /// when you enable deferred unload or want the low-memory hook.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ContentDeliveryRuntime : MonoBehaviour
    {
        private static ContentDeliveryRuntime s_instance;

        /// <summary>Whether the host is currently installed.</summary>
        public static bool IsInstalled => s_instance != null;

        /// <summary>
        /// Ensures the singleton host exists (creating a hidden <c>DontDestroyOnLoad</c> GameObject on first call).
        /// Safe to call repeatedly and from any of the init entry points. No-op outside play mode.
        /// </summary>
        public static void Install()
        {
            if (s_instance != null || !Application.isPlaying) return;
            var go = new GameObject("PFound.ContentDeliveryRuntime") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            s_instance = go.AddComponent<ContentDeliveryRuntime>();
        }

        /// <summary>Removes the host (used on teardown / tests).</summary>
        public static void Uninstall()
        {
            if (s_instance == null) return;
            var go = s_instance.gameObject;
            s_instance = null;
            if (Application.isPlaying) Destroy(go); else DestroyImmediate(go);
        }

        private void OnEnable() => Application.lowMemory += OnLowMemory;
        private void OnDisable() => Application.lowMemory -= OnLowMemory;

        private void OnDestroy()
        {
            if (s_instance == this) s_instance = null;
        }

        private void Update()
        {
            // Only walks the queue when something is actually deferred (cheap guard inside AssetManager).
            if (AssetManager.DeferredUnloadFrames > 0) AssetManager.PumpDeferredUnloads(Time.frameCount);
        }

        private static void OnLowMemory() => AssetManager.HandleLowMemory();
    }
}
