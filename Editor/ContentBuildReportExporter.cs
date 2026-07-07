using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PFound.ContentDelivery.Editor
{
    /// <summary>
    /// Post-process over a finished build: serializes the <see cref="ContentBuildReport"/> (per-bundle sizes +
    /// routing + the catalog version) and any <see cref="DuplicateDependency"/> findings to a JSON report file,
    /// so a build's output is auditable from CI or by hand. The full catalog JSON is intentionally NOT embedded
    /// (it already lives next to the bundles); the report is a summary. Editor-only.
    /// </summary>
    public static class ContentBuildReportExporter
    {
        public const string ReportFileName = "build-report.json";

        [Serializable]
        private struct BundleDto { public string name; public string hash; public long storedSize; public bool local; public string compression; }

        [Serializable]
        private struct DuplicateDto { public string asset; public string[] bundles; }

        [Serializable]
        private struct ReportDto
        {
            public string catalogVersion;
            public int bundleCount;
            public int localBundleCount;
            public int remoteBundleCount;
            public long totalStoredSize;
            public string publishDirectory;
            public string streamingAssetsDirectory;
            public BundleDto[] bundles;
            public DuplicateDto[] duplicateDependencies;
        }

        /// <summary>Serializes the report (+ optional duplicate findings) to the JSON document <see cref="Write"/> writes.</summary>
        public static string ToJson(
            ContentBuildReport report, IReadOnlyList<DuplicateDependency> duplicates = null, bool prettyPrint = true)
        {
            var bundles = report.Bundles ?? Array.Empty<BundleReportEntry>();
            long total = 0;
            var bundleDtos = new BundleDto[bundles.Length];
            for (int i = 0; i < bundles.Length; i++)
            {
                var b = bundles[i];
                total += b.StoredSize;
                bundleDtos[i] = new BundleDto
                {
                    name = b.Name, hash = b.Hash, storedSize = b.StoredSize, local = b.Local, compression = b.Compression,
                };
            }

            var dupDtos = Array.Empty<DuplicateDto>();
            if (duplicates != null && duplicates.Count > 0)
            {
                dupDtos = new DuplicateDto[duplicates.Count];
                for (int i = 0; i < duplicates.Count; i++)
                    dupDtos[i] = new DuplicateDto { asset = duplicates[i].Asset, bundles = duplicates[i].Bundles };
            }

            var dto = new ReportDto
            {
                catalogVersion = report.CatalogVersion,
                bundleCount = report.BundleCount,
                localBundleCount = report.LocalBundleCount,
                remoteBundleCount = report.RemoteBundleCount,
                totalStoredSize = total,
                publishDirectory = report.PublishDirectory,
                streamingAssetsDirectory = report.StreamingAssetsDirectory,
                bundles = bundleDtos,
                duplicateDependencies = dupDtos,
            };
            return JsonUtility.ToJson(dto, prettyPrint);
        }

        /// <summary>Writes the report JSON into <paramref name="directory"/> as <see cref="ReportFileName"/>; returns its path.</summary>
        public static string Write(
            ContentBuildReport report, IReadOnlyList<DuplicateDependency> duplicates, string directory)
        {
            if (string.IsNullOrEmpty(directory)) throw new ArgumentException("Report directory required.", nameof(directory));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, ReportFileName);
            File.WriteAllText(path, ToJson(report, duplicates));
            return path;
        }
    }
}
