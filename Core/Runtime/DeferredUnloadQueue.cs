using System;
using System.Collections.Generic;

namespace PFound.ContentDelivery.Core
{
    /// <summary>
    /// A frame-delayed release queue: keys enqueued for unload are held for a fixed number of frames before the
    /// release callback runs, and a re-acquire in the meantime can <see cref="Cancel"/> the pending release so the
    /// resource is never torn down and immediately rebuilt. This is the configurable alternative to unloading the
    /// instant a ref-count hits zero — a short grace window absorbs the common load / unload / re-load churn (e.g.
    /// a screen that drops an asset and a sibling re-takes it the same frame) without a bundle reload.
    /// <para>
    /// Pure C# (BCL only). The owner supplies a monotonically-increasing frame number and the release action, so the
    /// queue carries no engine dependency and is unit-testable off-engine.
    /// </para>
    /// </summary>
    /// <typeparam name="TKey">The residency key (an asset address, a bundle hash, …).</typeparam>
    public sealed class DeferredUnloadQueue<TKey>
    {
        private readonly int _frames;
        private readonly Dictionary<TKey, int> _queuedFrame;

        /// <param name="frames">Frames to wait before releasing a queued key. &lt;= 0 means release on the next pump.</param>
        public DeferredUnloadQueue(int frames, IEqualityComparer<TKey> comparer = null)
        {
            _frames = frames;
            _queuedFrame = new Dictionary<TKey, int>(comparer ?? EqualityComparer<TKey>.Default);
        }

        /// <summary>Frames the queue waits before a queued key becomes releasable.</summary>
        public int Frames => _frames;

        /// <summary>How many keys are currently awaiting release.</summary>
        public int Count => _queuedFrame.Count;

        public bool IsQueued(TKey key) => _queuedFrame.ContainsKey(key);

        /// <summary>Queues <paramref name="key"/> for release, timestamped with <paramref name="currentFrame"/>.
        /// Re-queuing resets the timer.</summary>
        public void Enqueue(TKey key, int currentFrame) => _queuedFrame[key] = currentFrame;

        /// <summary>Cancels a pending release (the resource was re-acquired). Returns whether one was pending.</summary>
        public bool Cancel(TKey key) => _queuedFrame.Remove(key);

        /// <summary>
        /// Releases every key whose wait has elapsed as of <paramref name="currentFrame"/> (running
        /// <paramref name="release"/> for each), and drops them from the queue. Keys still within their grace
        /// window remain queued.
        /// </summary>
        public void Pump(int currentFrame, Action<TKey> release)
        {
            if (release == null) throw new ArgumentNullException(nameof(release));
            if (_queuedFrame.Count == 0) return;

            List<TKey> due = null;
            foreach (var kv in _queuedFrame)
                if (currentFrame - kv.Value >= _frames)
                    (due ??= new List<TKey>()).Add(kv.Key);

            if (due == null) return;
            for (int i = 0; i < due.Count; i++)
            {
                _queuedFrame.Remove(due[i]);
                release(due[i]);
            }
        }

        /// <summary>Immediately releases every queued key (running <paramref name="release"/> for each) and empties
        /// the queue — used on teardown / low-memory to reclaim the pending residency at once.</summary>
        public void Flush(Action<TKey> release)
        {
            if (release == null) throw new ArgumentNullException(nameof(release));
            if (_queuedFrame.Count == 0) return;

            var all = new List<TKey>(_queuedFrame.Keys);
            _queuedFrame.Clear();
            for (int i = 0; i < all.Count; i++) release(all[i]);
        }

        /// <summary>Drops all pending entries WITHOUT releasing them (test/reset helper).</summary>
        public void Clear() => _queuedFrame.Clear();
    }
}
