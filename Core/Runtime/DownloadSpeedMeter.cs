using System;

namespace PFound.ContentDelivery.Core
{
    /// <summary>How the current transfer rate is classified against the configured slow threshold.</summary>
    public enum DownloadSpeedRating
    {
        Bad = 0,  // sustained rate below the slow threshold — a poor connection the UI may want to surface
        Good = 1, // rate at or above the threshold
    }

    /// <summary>
    /// Classifies an ongoing download's throughput as Good / Bad against a byte/sec threshold and raises an event
    /// only on a <em>transition</em> (Good→Bad or Bad→Good), so a UI can show/hide a "slow connection" hint without
    /// polling. Two ways to feed it: a pre-computed rate via <see cref="Sample(double)"/>, or a cumulative
    /// (time, totalBytes) pair via <see cref="Sample(double,long)"/> — the latter derives the rate itself and only
    /// re-evaluates once <see cref="CheckInterval"/> has elapsed (so a single fast burst can't flip the rating).
    /// Pure C# (BCL only) — testable without Unity; the <see cref="DownloadScheduler"/> can drive it from its
    /// aggregate bytes/sec, or a caller can drive it from any transport that reports progress.
    /// </summary>
    public sealed class DownloadSpeedMeter
    {
        /// <summary>Raised whenever the rating flips; the argument is the NEW rating.</summary>
        public event Action<DownloadSpeedRating> RatingChanged;
        /// <summary>Raised on a Good→Bad transition (convenience over <see cref="RatingChanged"/>).</summary>
        public event Action BadDetected;
        /// <summary>Raised on a Bad→Good transition (convenience over <see cref="RatingChanged"/>).</summary>
        public event Action GoodDetected;

        private readonly double _slowThresholdBytesPerSecond;
        private double _lastSampleTime = -1.0;
        private long _lastCumulativeBytes = -1;

        /// <summary>The slow-speed threshold in bytes/sec; a rate below it is <see cref="DownloadSpeedRating.Bad"/>.</summary>
        public double SlowThresholdBytesPerSecond => _slowThresholdBytesPerSecond;

        /// <summary>Minimum seconds between re-evaluations of the cumulative <see cref="Sample(double,long)"/> overload.</summary>
        public double CheckInterval { get; set; } = 1.0;

        /// <summary>The current rating; starts <see cref="DownloadSpeedRating.Good"/> (optimistic — no penalty before data flows).</summary>
        public DownloadSpeedRating Rating { get; private set; } = DownloadSpeedRating.Good;

        /// <summary>The last rate observed by <see cref="Sample(double)"/> / derived by the cumulative overload.</summary>
        public double LastBytesPerSecond { get; private set; }

        public DownloadSpeedMeter(double slowThresholdBytesPerSecond)
        {
            if (slowThresholdBytesPerSecond < 0) throw new ArgumentOutOfRangeException(nameof(slowThresholdBytesPerSecond));
            _slowThresholdBytesPerSecond = slowThresholdBytesPerSecond;
        }

        /// <summary>Feeds an already-computed transfer rate (bytes/sec) and re-classifies immediately.</summary>
        public void Sample(double bytesPerSecond)
        {
            LastBytesPerSecond = bytesPerSecond;
            Apply(bytesPerSecond);
        }

        /// <summary>
        /// Feeds a cumulative (elapsed-seconds, total-bytes) reading. The rate is derived from the delta since the
        /// previous accepted reading; a reading is only evaluated once <see cref="CheckInterval"/> has elapsed and
        /// bytes have advanced, so brief bursts/stalls between checks do not flip the rating.
        /// </summary>
        public void Sample(double timeSeconds, long cumulativeBytes)
        {
            // First reading (or a reset): seed the baseline, do not classify yet.
            if (_lastSampleTime < 0.0 || _lastCumulativeBytes < 0)
            {
                _lastSampleTime = timeSeconds;
                _lastCumulativeBytes = cumulativeBytes;
                return;
            }

            double deltaTime = timeSeconds - _lastSampleTime;
            if (deltaTime < CheckInterval) return; // not enough time elapsed to judge — wait for the next window

            long deltaBytes = cumulativeBytes - _lastCumulativeBytes;
            double rate = deltaTime > 0 ? deltaBytes / deltaTime : 0;
            LastBytesPerSecond = rate;

            _lastSampleTime = timeSeconds;
            _lastCumulativeBytes = cumulativeBytes;
            Apply(rate);
        }

        /// <summary>Clears the sampling baseline (call between distinct downloads); leaves the rating as-is.</summary>
        public void Reset()
        {
            _lastSampleTime = -1.0;
            _lastCumulativeBytes = -1;
        }

        private void Apply(double bytesPerSecond)
        {
            var next = bytesPerSecond < _slowThresholdBytesPerSecond ? DownloadSpeedRating.Bad : DownloadSpeedRating.Good;
            if (next == Rating) return; // only transitions fire events

            Rating = next;
            RatingChanged?.Invoke(next);
            if (next == DownloadSpeedRating.Bad) BadDetected?.Invoke();
            else GoodDetected?.Invoke();
        }
    }
}
