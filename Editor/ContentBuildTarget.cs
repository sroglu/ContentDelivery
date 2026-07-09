using UnityEditor;

namespace PFound.ContentDelivery.Editor
{
    /// <summary>
    /// The curated content build targets actually shipped — a small stand-in for the full
    /// <see cref="UnityEditor.BuildTarget"/> enum (dozens of platforms, most never built). Each member's underlying
    /// int value MATCHES the <see cref="BuildTarget"/> it maps to, so a <see cref="CatalogEditorConfig"/> asset that
    /// already serialized a <c>BuildPlatform</c> value survives the field type change with no data migration.
    /// Extend by adding a member + a <see cref="ContentBuildTargetExtensions.ToBuildTarget"/> arm.
    /// </summary>
    public enum ContentBuildTarget
    {
        StandaloneOSX     = (int)BuildTarget.StandaloneOSX,       // 2
        StandaloneWindows = (int)BuildTarget.StandaloneWindows64, // 19 (Win64)
        iOS               = (int)BuildTarget.iOS,                 // 9
        Android           = (int)BuildTarget.Android,             // 13
    }

    public static class ContentBuildTargetExtensions
    {
        /// <summary>Maps to the real Unity <see cref="BuildTarget"/> the pipeline + platform-folder resolution need.
        /// Goes through actual <see cref="BuildTarget"/>s, so the platform-folder strings stay identical to the
        /// runtime's <c>ContentPlatform.ActivePlatformFolder()</c> — no catalog/layout drift.</summary>
        public static BuildTarget ToBuildTarget(this ContentBuildTarget t) => t switch
        {
            ContentBuildTarget.StandaloneOSX     => BuildTarget.StandaloneOSX,
            ContentBuildTarget.StandaloneWindows => BuildTarget.StandaloneWindows64,
            ContentBuildTarget.iOS               => BuildTarget.iOS,
            ContentBuildTarget.Android           => BuildTarget.Android,
            _ => throw new System.ArgumentOutOfRangeException(nameof(t)),
        };
    }
}
