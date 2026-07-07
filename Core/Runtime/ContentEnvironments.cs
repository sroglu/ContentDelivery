using System;
using System.Collections.Generic;

namespace PFound.ContentDelivery.Core
{
    /// <summary>
    /// Maps content environments (dev / staging / prod / …) to their remote CDN origin and tracks which one is
    /// active. Content itself stays environment-agnostic — the same bundles, the same content hashes — only the
    /// origin they are pulled from changes. So an environment is a load-path selection (like an Addressables
    /// profile), NOT a per-environment catalog variant. Pure Core (engine-free): the selection is unit-testable
    /// without Unity, and the Unity bootstrap resolves the active origin through it.
    /// </summary>
    public sealed class ContentEnvironments
    {
        private readonly Dictionary<string, string> _origins;

        /// <summary>The active environment name; always one of <see cref="Names"/>.</summary>
        public string Active { get; }

        /// <param name="origins">environment name → remote CDN base URL.</param>
        /// <param name="active">the initially selected environment; must be present in <paramref name="origins"/>.</param>
        public ContentEnvironments(IEnumerable<KeyValuePair<string, string>> origins, string active)
        {
            if (origins == null) throw new ArgumentNullException(nameof(origins));
            if (string.IsNullOrEmpty(active)) throw new ArgumentException("Active environment is required.", nameof(active));

            _origins = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in origins) _origins[kv.Key] = kv.Value;

            if (!_origins.ContainsKey(active))
                throw new ArgumentException(
                    "Active environment '" + active + "' is not among the configured environments.", nameof(active));
            Active = active;
        }

        /// <summary>All configured environment names.</summary>
        public IReadOnlyCollection<string> Names => _origins.Keys;

        /// <summary>The remote CDN origin for the <see cref="Active"/> environment.</summary>
        public string ResolveRemoteBaseUrl() => _origins[Active];

        /// <summary>The remote CDN origin for <paramref name="environment"/>; throws if it is not configured.</summary>
        public string ResolveRemoteBaseUrl(string environment)
        {
            if (environment == null) throw new ArgumentNullException(nameof(environment));
            if (!_origins.TryGetValue(environment, out var origin))
                throw new InvalidOperationException("Unknown content environment: " + environment + ".");
            return origin;
        }

        public bool TryGetOrigin(string environment, out string baseUrl) => _origins.TryGetValue(environment, out baseUrl);
    }
}
