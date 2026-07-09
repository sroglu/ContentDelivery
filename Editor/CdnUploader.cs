using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PFound.ContentDelivery.Core;

namespace PFound.ContentDelivery.Editor
{
    /// <summary>
    /// Publishes built content to an origin. Content-addressed bundles are immutable, so re-uploading the
    /// same hash is a no-op-by-overwrite; flipping live content is just uploading a new <c>catalog_…_&lt;hash&gt;.lzma</c>.
    /// Editor/standalone tool — never ships in the player. S3 / BunnyCDN plug in as further implementations.
    /// </summary>
    public interface ICdnUploader
    {
        Task UploadFileAsync(string localPath, string remoteKey, CancellationToken cancellationToken = default);
    }

    /// <summary>Upload helpers shared across uploaders.</summary>
    public static class CdnUpload
    {
        /// <summary>
        /// Uploads every file in a publish directory (the content-addressed bundles plus the single
        /// <c>catalog_…_&lt;hash&gt;.lzma</c>), keyed by file name. Bundles upload BEFORE the catalog so the catalog never
        /// advertises absent content. The catalog is identified by its <c>catalog</c> prefix, not a fixed name.
        /// </summary>
        public static async Task UploadPublishDirectoryAsync(
            ICdnUploader uploader, string publishDirectory, CancellationToken cancellationToken = default)
        {
            if (uploader == null) throw new ArgumentNullException(nameof(uploader));
            if (!Directory.Exists(publishDirectory)) throw new DirectoryNotFoundException(publishDirectory);

            var files = Directory.GetFiles(publishDirectory);

            // Bundles first. IsShippableBundle excludes the catalog artifact + pointer/meta sidecars, so this is exactly
            // the content-addressed bundle set.
            foreach (var path in files)
                if (AssetBundleLayout.IsShippableBundle(Path.GetFileName(path)))
                    await uploader.UploadFileAsync(path, Path.GetFileName(path), cancellationToken);

            // Then the single catalog (catalog_*.lzma) — after its bundles are live.
            foreach (var path in files)
            {
                string name = Path.GetFileName(path);
                if (name.StartsWith("catalog", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    await uploader.UploadFileAsync(path, name, cancellationToken);
            }

            // Finally the remote pointer — it advertises the now-live catalog, so it is uploaded LAST and never names
            // absent content. This is what RemoteCatalogResolver reads to discover the current catalog file name.
            string pointerPath = Path.Combine(publishDirectory, AssetBundleLayout.RemoteCatalogPointerFileName);
            if (File.Exists(pointerPath))
                await uploader.UploadFileAsync(pointerPath, AssetBundleLayout.RemoteCatalogPointerFileName, cancellationToken);
        }
    }

    /// <summary>
    /// Mirrors content into a local origin directory — a CI/dev stand-in for a CDN bucket (and the
    /// StreamingAssets target for <see cref="DistributionMode.Local"/> content). Fully offline-testable.
    /// </summary>
    public sealed class DirectoryUploader : ICdnUploader
    {
        private readonly string _originDirectory;

        public DirectoryUploader(string originDirectory) { _originDirectory = originDirectory; }

        public Task UploadFileAsync(string localPath, string remoteKey, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string dest = Path.Combine(_originDirectory, remoteKey);
            string dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(localPath, dest, true);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Uploads to an FTP origin via <see cref="FtpWebRequest"/> (built into .NET, no third-party SDK). The
    /// base URI is the target directory; <c>remoteKey</c> is appended as the stored object name.
    /// </summary>
    public sealed class FtpUploader : ICdnUploader
    {
        private readonly Uri _baseUri;
        private readonly NetworkCredential _credentials;

        public FtpUploader(string baseUri, string user, string password)
        {
            _baseUri = new Uri(baseUri.EndsWith("/", StringComparison.Ordinal) ? baseUri : baseUri + "/");
            _credentials = new NetworkCredential(user, password);
        }

        public async Task UploadFileAsync(string localPath, string remoteKey, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = new Uri(_baseUri, remoteKey);

            var request = (FtpWebRequest)WebRequest.Create(target);
            request.Method = WebRequestMethods.Ftp.UploadFile;
            request.Credentials = _credentials;
            request.UseBinary = true;
            request.KeepAlive = false;

            byte[] bytes = File.ReadAllBytes(localPath);
            request.ContentLength = bytes.Length;
            using (var stream = await request.GetRequestStreamAsync())
                await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);

            using (var response = (FtpWebResponse)await request.GetResponseAsync())
            {
                if (response.StatusCode != FtpStatusCode.ClosingData &&
                    response.StatusCode != FtpStatusCode.FileActionOK &&
                    response.StatusCode != FtpStatusCode.CommandOK)
                    throw new IOException("FTP upload of '" + remoteKey + "' failed: " + response.StatusCode);
            }
        }
    }

    /// <summary>
    /// Uploads to a BunnyCDN Edge Storage zone over its HTTP API: <c>PUT {baseUrl}/{remoteKey}</c> with the
    /// storage password in the <c>AccessKey</c> header. Content-addressed objects are immutable, so re-PUTting
    /// the same hash is a harmless overwrite. No SDK — built-in <see cref="HttpClient"/>.
    /// </summary>
    public sealed class BunnyCdnUploader : ICdnUploader
    {
        private static readonly HttpClient s_http = new HttpClient();
        private readonly string _baseUrl;
        private readonly string _accessKey;

        /// <param name="storageZoneBaseUrl">e.g. <c>https://storage.bunnycdn.com/{zone}</c> (region endpoint if not default).</param>
        /// <param name="accessKey">The storage zone password.</param>
        public BunnyCdnUploader(string storageZoneBaseUrl, string accessKey)
        {
            _baseUrl = storageZoneBaseUrl.EndsWith("/", StringComparison.Ordinal) ? storageZoneBaseUrl : storageZoneBaseUrl + "/";
            _accessKey = accessKey;
        }

        public async Task UploadFileAsync(string localPath, string remoteKey, CancellationToken cancellationToken = default)
        {
            using (var content = new ByteArrayContent(File.ReadAllBytes(localPath)))
            using (var request = new HttpRequestMessage(HttpMethod.Put, _baseUrl + remoteKey) { Content = content })
            {
                request.Headers.TryAddWithoutValidation("AccessKey", _accessKey);
                using (var response = await s_http.SendAsync(request, cancellationToken))
                    if (!response.IsSuccessStatusCode)
                        throw new IOException("BunnyCDN upload of '" + remoteKey + "' failed: " + (int)response.StatusCode);
            }
        }
    }

    /// <summary>
    /// Uploads to an Amazon S3 bucket with a virtual-hosted-style <c>PUT</c> object request, signed with AWS
    /// Signature Version 4. No AWS SDK — the signing is the standard SigV4 algorithm over the built-in
    /// <see cref="HttpClient"/> + <see cref="HMACSHA256"/>. Works against S3-compatible stores that accept SigV4.
    /// </summary>
    public sealed class S3Uploader : ICdnUploader
    {
        private static readonly HttpClient s_http = new HttpClient();
        private const string Service = "s3";

        private readonly string _bucket;
        private readonly string _region;
        private readonly string _accessKeyId;
        private readonly string _secretAccessKey;
        private readonly string _host;

        public S3Uploader(string bucket, string region, string accessKeyId, string secretAccessKey, string host = null)
        {
            _bucket = bucket;
            _region = region;
            _accessKeyId = accessKeyId;
            _secretAccessKey = secretAccessKey;
            // Virtual-hosted-style host; overridable for S3-compatible endpoints.
            _host = host ?? bucket + ".s3." + region + ".amazonaws.com";
        }

        public async Task UploadFileAsync(string localPath, string remoteKey, CancellationToken cancellationToken = default)
        {
            byte[] payload = File.ReadAllBytes(localPath);
            DateTime now = DateTime.UtcNow;
            string amzDate = now.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
            string dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            string payloadHash = Hex(Sha256(payload));
            string canonicalUri = "/" + UriEncodePath(remoteKey);

            // Canonical request: PUT, signed headers host;x-amz-content-sha256;x-amz-date.
            string canonicalHeaders = "host:" + _host + "\n" +
                                      "x-amz-content-sha256:" + payloadHash + "\n" +
                                      "x-amz-date:" + amzDate + "\n";
            const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";
            string canonicalRequest = "PUT\n" + canonicalUri + "\n\n" + canonicalHeaders + "\n" + signedHeaders + "\n" + payloadHash;

            string scope = dateStamp + "/" + _region + "/" + Service + "/aws4_request";
            string stringToSign = "AWS4-HMAC-SHA256\n" + amzDate + "\n" + scope + "\n" + Hex(Sha256(Encoding.UTF8.GetBytes(canonicalRequest)));

            byte[] signingKey = SigningKey(_secretAccessKey, dateStamp, _region, Service);
            string signature = Hex(HmacSha256(signingKey, stringToSign));
            string authorization = "AWS4-HMAC-SHA256 Credential=" + _accessKeyId + "/" + scope +
                                   ", SignedHeaders=" + signedHeaders + ", Signature=" + signature;

            using (var content = new ByteArrayContent(payload))
            using (var request = new HttpRequestMessage(HttpMethod.Put, "https://" + _host + canonicalUri) { Content = content })
            {
                request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
                request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
                request.Headers.TryAddWithoutValidation("Authorization", authorization);
                using (var response = await s_http.SendAsync(request, cancellationToken))
                    if (!response.IsSuccessStatusCode)
                        throw new IOException("S3 upload of '" + remoteKey + "' failed: " + (int)response.StatusCode);
            }
        }

        private static byte[] SigningKey(string secret, string dateStamp, string region, string service)
        {
            byte[] kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secret), dateStamp);
            byte[] kRegion = HmacSha256(kDate, region);
            byte[] kService = HmacSha256(kRegion, service);
            return HmacSha256(kService, "aws4_request");
        }

        // Each path segment percent-encoded per RFC 3986 (S3 SigV4 unreserved set), '/' preserved.
        private static string UriEncodePath(string key)
        {
            var sb = new StringBuilder(key.Length);
            foreach (char c in key)
            {
                if (c == '/') { sb.Append('/'); continue; }
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
                    c == '-' || c == '_' || c == '.' || c == '~')
                    sb.Append(c);
                else
                    foreach (byte b in Encoding.UTF8.GetBytes(c.ToString()))
                        sb.Append('%').Append(((int)b).ToString("X2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static byte[] Sha256(byte[] data) { using (var sha = SHA256.Create()) return sha.ComputeHash(data); }
        private static byte[] HmacSha256(byte[] key, string data) { using (var h = new HMACSHA256(key)) return h.ComputeHash(Encoding.UTF8.GetBytes(data)); }
        private static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
