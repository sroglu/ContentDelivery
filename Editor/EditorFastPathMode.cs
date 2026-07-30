using UnityEditor;

namespace PFound.ContentDelivery.Editor
{
    /// <summary>
    /// Toggle + bootstrap for the editor fast-path. When ON (the default), an <see cref="EditorAssetSource"/> is
    /// registered ahead of the bundle source so loads resolve to live project assets — fast iteration, and the fix
    /// for the stale-bundle play-mode trap (Play loading yesterday's built bundles while the project moved on).
    /// Turn it OFF to validate the true built-bundle path before shipping. The mode is persisted in
    /// <see cref="EditorPrefs"/>; the source is (re)synced on domain load, on entering Play, and when toggled.
    /// </summary>
    [InitializeOnLoad]
    public static class EditorFastPathMode
    {
        private const string EnabledKey = "PFound.ContentDelivery.EditorFastPath.Enabled";
        private const string MenuPath = "PFound/Content Delivery/Editor Fast-Path (project assets)";

        // The currently-registered source, so a toggle/refresh can remove exactly it.
        private static EditorAssetSource s_registered;

        static EditorFastPathMode()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            Sync();
        }

        /// <summary>Whether the fast-path is active. Defaults to ON so iteration uses project truth, not bundles.</summary>
        public static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledKey, true);
            set { EditorPrefs.SetBool(EnabledKey, value); Sync(); }
        }

        [MenuItem(MenuPath)]
        private static void Toggle() => Enabled = !Enabled;

        [MenuItem(MenuPath, validate = true)]
        private static bool ToggleValidate()
        {
            Menu.SetChecked(MenuPath, Enabled);
            return true;
        }

        // Rebuild + re-register just before entering Play so the run loads CURRENT project assets, never a stale
        // bundle. Essential under "Enter Play Mode (no domain reload)", where statics persist and the
        // InitializeOnLoad sync below would not re-run.
        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode) Sync();
        }

        /// <summary>
        /// Brings the registered source in line with the toggle: removes any stale registration, then — if enabled —
        /// registers a freshly-built address map. Idempotent; safe to call repeatedly.
        /// </summary>
        public static void Sync()
        {
            if (s_registered != null)
            {
                AssetManager.UnregisterSource(s_registered);
                s_registered = null;
            }
            if (!Enabled) return;

            s_registered = new EditorAssetSource(EditorAddressMap.Build());
            AssetManager.RegisterSource(s_registered);
        }
    }
}
