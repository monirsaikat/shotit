using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentFTP;
using Renci.SshNet;
using ShotIt.Core.Settings;
using ShotIt.Core.Upload;

namespace ShotIt.Upload;

internal static class UploadHelpers
{
    /// <summary>One shared client: sockets are pooled and DNS changes are still picked up.</summary>
    public static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    })
    {
        Timeout = TimeSpan.FromSeconds(100),
    };

    /// <summary>
    /// A 128-bit random name. Uploaded files must not be guessable: sequential or short names let
    /// strangers enumerate other people's screenshots.
    /// </summary>
    public static string RandomName()
    {
        const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        Span<byte> bytes = stackalloc byte[22];
        RandomNumberGenerator.Fill(bytes);
        return string.Create(22, bytes.ToArray(), (span, source) =>
        {
            for (int i = 0; i < span.Length; i++) span[i] = alphabet[source[i] % alphabet.Length];
        });
    }

    public static string RandomFileName(UploadRequest request) => RandomName() + Path.GetExtension(request.FileName).ToLowerInvariant();

    /// <summary>Uploads leave the machine, so only encrypted transports are accepted (loopback is allowed for testing).</summary>
    public static Uri RequireSecure(string url, string what)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new UploadException($"{what} is not a valid web address.");
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
            throw new UploadException($"{what} must use https so your screenshot is encrypted in transit.");
        return uri;
    }

    public static string JoinUrl(string baseUrl, string name) => baseUrl.TrimEnd('/') + "/" + Uri.EscapeDataString(name);

    /// <summary>Turns transport failures into messages a user can act on.</summary>
    public static UploadException Describe(Exception ex, string destination) => ex switch
    {
        UploadException u => u,
        TaskCanceledException or TimeoutException => new UploadException($"{destination} did not answer in time. Check your connection and try again.", ex),
        HttpRequestException { InnerException: SocketException } or SocketException => new UploadException($"Could not reach {destination}. Check your internet connection and the server address.", ex),
        HttpRequestException h => new UploadException($"{destination} could not be reached: {h.Message}", ex),
        _ => new UploadException($"Upload to {destination} failed: {ex.Message}", ex),
    };

    public static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return body.Length > 4000 ? body[..4000] : body;
    }

    public static UploadException HttpFailure(string destination, HttpStatusCode status, string body)
    {
        string hint = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "The server rejected the credentials. Check them in Settings › Upload.",
            HttpStatusCode.RequestEntityTooLarge => "The image is too large for this server.",
            HttpStatusCode.TooManyRequests => "The server is rate limiting uploads. Try again later.",
            >= HttpStatusCode.InternalServerError => "The server reported an internal error. Try again later.",
            _ => "Check the upload settings.",
        };
        string detail = body.Length > 200 ? body[..200] + "…" : body;
        return new UploadException($"{destination} answered {(int)status} ({status}). {hint}" + (detail.Length > 0 ? " Server said: " + detail.ReplaceLineEndings(" ") : string.Empty));
    }

    /// <summary>Reads a value such as "data.link" or "files.0.url" from a JSON document.</summary>
    public static string? ReadJsonPath(string json, string path)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var element = document.RootElement;
            foreach (string part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(part, out var child)) element = child;
                else if (element.ValueKind == JsonValueKind.Array && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < element.GetArrayLength()) element = element[index];
                else return null;
            }

            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.GetRawText(),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool IsWebUrl(string? text) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}

/// <summary>Anonymous upload to Imgur with the user's own API client id.</summary>
public sealed class ImgurUploader : IUploader
{
    private readonly string _clientId;
    private readonly Uri _endpoint;

    public ImgurUploader(string clientId, string endpoint = "https://api.imgur.com/3/image")
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new UploadException("Imgur needs a client ID. Add one in Settings › Upload.");
        _clientId = clientId.Trim();
        _endpoint = UploadHelpers.RequireSecure(endpoint, "The Imgur address");
    }

    public string Name => "Imgur";

    public async Task<UploadResult> UploadAsync(UploadRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            using var content = new MultipartFormDataContent();
            var file = new ByteArrayContent(request.Data);
            file.Headers.ContentType = new MediaTypeHeaderValue(request.ContentType);
            content.Add(file, "image", request.FileName);
            using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = content };
            message.Headers.Authorization = new AuthenticationHeaderValue("Client-ID", _clientId);

            using var response = await UploadHelpers.Http.SendAsync(message, cancellationToken).ConfigureAwait(false);
            string body = await UploadHelpers.ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw UploadHelpers.HttpFailure(Name, response.StatusCode, UploadHelpers.ReadJsonPath(body, "data.error") ?? body);

            string? link = UploadHelpers.ReadJsonPath(body, "data.link");
            if (!UploadHelpers.IsWebUrl(link)) throw new UploadException("Imgur accepted the upload but did not return a link.");
            string? deleteHash = UploadHelpers.ReadJsonPath(body, "data.deletehash");
            progress?.Report(1);
            return new UploadResult(link!, string.IsNullOrEmpty(deleteHash) ? null : "https://imgur.com/delete/" + Uri.EscapeDataString(deleteHash));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw UploadHelpers.Describe(ex, Name);
        }
    }
}

/// <summary>AWS Signature Version 4 for a single-chunk request.</summary>
public static class AwsSigV4
{
    public static string HexSha256(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    /// <summary>Percent-encodes exactly as the specification requires (unreserved characters only stay literal).</summary>
    public static string UriEncode(string value, bool encodeSlash)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (byte b in Encoding.UTF8.GetBytes(value))
        {
            char c = (char)b;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or '~' || (c == '/' && !encodeSlash)) sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Computes the Authorization header value.
    /// </summary>
    /// <param name="method">HTTP method in upper case.</param>
    /// <param name="absolutePath">Unencoded request path starting with "/".</param>
    /// <param name="query">Unencoded query parameters (may be empty).</param>
    /// <param name="headers">Every header to sign, including host and x-amz-date. Names are case-insensitive.</param>
    /// <param name="payloadHash">Lower-case hex SHA-256 of the body.</param>
    /// <param name="accessKey">Access key id.</param>
    /// <param name="secretKey">Secret access key.</param>
    /// <param name="region">Signing region, for example us-east-1.</param>
    /// <param name="service">Service name, "s3".</param>
    /// <param name="utcNow">Request time; must match x-amz-date.</param>
    public static string Authorization(
        string method, string absolutePath, IEnumerable<KeyValuePair<string, string>> query, IEnumerable<KeyValuePair<string, string>> headers,
        string payloadHash, string accessKey, string secretKey, string region, string service, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(query);
        string date = utcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string timestamp = utcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        var sorted = headers
            .Select(h => new KeyValuePair<string, string>(h.Key.ToLowerInvariant(), string.Join(' ', h.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))))
            .OrderBy(h => h.Key, StringComparer.Ordinal)
            .ToList();
        string canonicalHeaders = string.Concat(sorted.Select(h => h.Key + ":" + h.Value + "\n"));
        string signedHeaders = string.Join(';', sorted.Select(h => h.Key));
        string canonicalQuery = string.Join('&', query
            .Select(q => UriEncode(q.Key, true) + "=" + UriEncode(q.Value, true))
            .OrderBy(q => q, StringComparer.Ordinal));

        string canonicalRequest = string.Join('\n', method, UriEncode(absolutePath, false), canonicalQuery, canonicalHeaders, signedHeaders, payloadHash);
        string scope = $"{date}/{region}/{service}/aws4_request";
        string stringToSign = string.Join('\n', "AWS4-HMAC-SHA256", timestamp, scope, HexSha256(Encoding.UTF8.GetBytes(canonicalRequest)));

        byte[] key = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), date), region), service), "aws4_request");
        string signature = Convert.ToHexStringLower(Hmac(key, stringToSign));
        return $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope},SignedHeaders={signedHeaders},Signature={signature}";
    }
}

/// <summary>Upload to Amazon S3 or any S3-compatible store (Cloudflare R2, MinIO, Backblaze B2, Wasabi).</summary>
public sealed class S3Uploader : IUploader
{
    private readonly Uri _endpoint;
    private readonly string _region, _bucket, _accessKey, _secretKey, _prefix, _publicBase;
    private readonly bool _pathStyle;
    private readonly Func<DateTime> _clock;

    public S3Uploader(UploadSettings settings, string secretKey, Func<DateTime>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.S3Bucket)) throw new UploadException("S3 needs a bucket name. Add one in Settings › Upload.");
        if (string.IsNullOrWhiteSpace(settings.S3AccessKey) || string.IsNullOrWhiteSpace(secretKey))
            throw new UploadException("S3 needs an access key and secret key. Add them in Settings › Upload.");

        _region = string.IsNullOrWhiteSpace(settings.S3Region) ? "us-east-1" : settings.S3Region.Trim();
        string endpoint = string.IsNullOrWhiteSpace(settings.S3Endpoint) ? $"https://s3.{_region}.amazonaws.com" : settings.S3Endpoint.Trim();
        _endpoint = UploadHelpers.RequireSecure(endpoint, "The S3 endpoint");
        _bucket = settings.S3Bucket.Trim();
        _accessKey = settings.S3AccessKey.Trim();
        _secretKey = secretKey;
        _prefix = settings.S3PathPrefix.Trim().Trim('/');
        _publicBase = settings.S3PublicUrlBase.Trim();
        _pathStyle = settings.S3PathStyle;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public string Name => "S3";

    public async Task<UploadResult> UploadAsync(UploadRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string key = (_prefix.Length > 0 ? _prefix + "/" : string.Empty) + UploadHelpers.RandomFileName(request);
        string host = _pathStyle ? _endpoint.Authority : _bucket + "." + _endpoint.Authority;
        string path = (_pathStyle ? "/" + _bucket : string.Empty) + "/" + key;
        var url = new Uri($"{_endpoint.Scheme}://{host}{AwsSigV4.UriEncode(path, false)}");

        try
        {
            var now = _clock();
            string payloadHash = AwsSigV4.HexSha256(request.Data);
            string timestamp = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            var headers = new Dictionary<string, string>
            {
                ["host"] = host,
                ["content-type"] = request.ContentType,
                ["x-amz-content-sha256"] = payloadHash,
                ["x-amz-date"] = timestamp,
            };
            string authorization = AwsSigV4.Authorization("PUT", path, [], headers, payloadHash, _accessKey, _secretKey, _region, "s3", now);

            using var message = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(request.Data) };
            message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
            message.Headers.Host = host;
            message.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
            message.Headers.TryAddWithoutValidation("x-amz-date", timestamp);
            message.Headers.TryAddWithoutValidation("Authorization", authorization);

            using var response = await UploadHelpers.Http.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string body = await UploadHelpers.ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                throw UploadHelpers.HttpFailure(Name, response.StatusCode, ExtractXmlMessage(body));
            }

            progress?.Report(1);
            string publicUrl = _publicBase.Length > 0
                ? _publicBase.TrimEnd('/') + "/" + string.Join('/', key.Split('/').Select(Uri.EscapeDataString))
                : url.AbsoluteUri;
            return new UploadResult(publicUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw UploadHelpers.Describe(ex, Name);
        }
    }

    private static string ExtractXmlMessage(string body)
    {
        int start = body.IndexOf("<Message>", StringComparison.OrdinalIgnoreCase);
        int end = body.IndexOf("</Message>", StringComparison.OrdinalIgnoreCase);
        return start >= 0 && end > start ? body[(start + 9)..end] : body;
    }
}

/// <summary>POSTs the image as multipart form data to any HTTP endpoint (compatible with ShareX-style custom uploaders).</summary>
public sealed class CustomHttpUploader : IUploader
{
    private readonly Uri _url;
    private readonly string _field, _urlPath, _deletePath;
    private readonly List<KeyValuePair<string, string>> _headers = [];

    public CustomHttpUploader(UploadSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.CustomUrl)) throw new UploadException("The custom uploader needs a URL. Add one in Settings › Upload.");
        _url = UploadHelpers.RequireSecure(settings.CustomUrl, "The upload URL");
        _field = string.IsNullOrWhiteSpace(settings.CustomFileField) ? "file" : settings.CustomFileField.Trim();
        _urlPath = settings.CustomResponseUrlPath.Trim();
        _deletePath = settings.CustomResponseDeletePath.Trim();

        foreach (string line in settings.CustomHeaders.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) throw new UploadException($"The header line “{line}” is not in “Name: value” form.");
            string name = line[..colon].Trim(), value = line[(colon + 1)..].Trim();
            // A line break inside a header value would let a pasted secret inject extra headers.
            if (name.Any(char.IsWhiteSpace) || value.Any(char.IsControl)) throw new UploadException($"The header “{name}” contains characters that are not allowed.");
            _headers.Add(new(name, value));
        }
    }

    public string Name => "Custom uploader";

    public async Task<UploadResult> UploadAsync(UploadRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            using var content = new MultipartFormDataContent();
            var file = new ByteArrayContent(request.Data);
            file.Headers.ContentType = new MediaTypeHeaderValue(request.ContentType);
            content.Add(file, _field, request.FileName);
            using var message = new HttpRequestMessage(HttpMethod.Post, _url) { Content = content };
            foreach (var (name, value) in _headers)
                if (!message.Headers.TryAddWithoutValidation(name, value)) throw new UploadException($"The header “{name}” cannot be set on an upload request.");

            using var response = await UploadHelpers.Http.SendAsync(message, cancellationToken).ConfigureAwait(false);
            string body = await UploadHelpers.ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw UploadHelpers.HttpFailure(Name, response.StatusCode, body);

            string? link = _urlPath.Length > 0 ? UploadHelpers.ReadJsonPath(body, _urlPath) : body.Trim();
            if (!UploadHelpers.IsWebUrl(link))
                throw new UploadException(_urlPath.Length > 0
                    ? $"The server's reply has no link at “{_urlPath}”. Check “Response URL path” in Settings › Upload."
                    : "The server's reply is not a link. Set “Response URL path” in Settings › Upload if the reply is JSON.");
            string? delete = _deletePath.Length > 0 ? UploadHelpers.ReadJsonPath(body, _deletePath) : null;
            progress?.Report(1);
            return new UploadResult(link!.Trim(), UploadHelpers.IsWebUrl(delete) ? delete : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw UploadHelpers.Describe(ex, Name);
        }
    }
}

/// <summary>Copies the image into a folder (a synced folder or network share) under an unguessable name.</summary>
public sealed class LocalFolderUploader : IUploader
{
    private readonly string _folder, _publicBase;

    public LocalFolderUploader(UploadSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.LocalFolder)) throw new UploadException("Choose the destination folder in Settings › Upload.");
        _folder = settings.LocalFolder.Trim();
        _publicBase = settings.LocalPublicUrlBase.Trim();
    }

    public string Name => "Folder";

    public async Task<UploadResult> UploadAsync(UploadRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            Directory.CreateDirectory(_folder);
            string name = UploadHelpers.RandomFileName(request);
            string path = Path.Combine(_folder, name);
            await File.WriteAllBytesAsync(path, request.Data, cancellationToken).ConfigureAwait(false);
            progress?.Report(1);
            return new UploadResult(_publicBase.Length > 0 ? UploadHelpers.JoinUrl(_publicBase, name) : path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new UploadException($"Could not write to “{_folder}”. {ex.Message}", ex);
        }
    }
}

/// <summary>Upload over FTP with explicit TLS when the server offers it.</summary>
public sealed class FtpUploader : IUploader
{
    private readonly UploadSettings _settings;
    private readonly string _password;

    public FtpUploader(UploadSettings settings, string password)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _password = password ?? string.Empty;
        if (string.IsNullOrWhiteSpace(settings.FtpHost)) throw new UploadException("FTP needs a server name. Add one in Settings › Upload.");
    }

    public string Name => "FTP";

    public async Task<UploadResult> UploadAsync(UploadRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string name = UploadHelpers.RandomFileName(request);
        string folder = "/" + _settings.FtpRemoteFolder.Trim().Trim('/');
        string remote = (folder == "/" ? string.Empty : folder) + "/" + name;
        try
        {
            var config = new FtpConfig { EncryptionMode = FtpEncryptionMode.Auto, ConnectTimeout = 15000, ReadTimeout = 30000, DataConnectionConnectTimeout = 15000, ValidateAnyCertificate = false };
            using var client = new AsyncFtpClient(_settings.FtpHost.Trim(), _settings.FtpUser, _password, _settings.FtpPort > 0 ? _settings.FtpPort : 21, config);
            await client.Connect(cancellationToken).ConfigureAwait(false);
            var status = await client.UploadBytes(request.Data, remote, FtpRemoteExists.Overwrite, createRemoteDir: true, token: cancellationToken).ConfigureAwait(false);
            await client.Disconnect(cancellationToken).ConfigureAwait(false);
            if (status == FtpStatus.Failed) throw new UploadException("The FTP server refused the file.");
            progress?.Report(1);
            return new UploadResult(PublicUrl(_settings, name, remote, "ftp"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw UploadHelpers.Describe(ex, "the FTP server");
        }
    }

    internal static string PublicUrl(UploadSettings settings, string name, string remote, string scheme) =>
        settings.FtpPublicUrlBase.Trim().Length > 0
            ? UploadHelpers.JoinUrl(settings.FtpPublicUrlBase.Trim(), name)
            : $"{scheme}://{settings.FtpHost.Trim()}{remote}";
}

/// <summary>Upload over SFTP (SSH).</summary>
public sealed class SftpUploader : IUploader
{
    private readonly UploadSettings _settings;
    private readonly string _password;

    public SftpUploader(UploadSettings settings, string password)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _password = password ?? string.Empty;
        if (string.IsNullOrWhiteSpace(settings.FtpHost)) throw new UploadException("SFTP needs a server name. Add one in Settings › Upload.");
        if (string.IsNullOrWhiteSpace(settings.FtpUser)) throw new UploadException("SFTP needs a user name. Add one in Settings › Upload.");
    }

    public string Name => "SFTP";

    public Task<UploadResult> UploadAsync(UploadRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // SSH.NET's upload is synchronous; keep it off the caller's thread.
        return Task.Run(() =>
        {
            string name = UploadHelpers.RandomFileName(request);
            string folder = "/" + _settings.FtpRemoteFolder.Trim().Trim('/');
            string remote = (folder == "/" ? string.Empty : folder) + "/" + name;
            try
            {
                using var client = new SftpClient(_settings.FtpHost.Trim(), _settings.FtpPort > 0 ? _settings.FtpPort : 22, _settings.FtpUser, _password);
                client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);
                client.OperationTimeout = TimeSpan.FromSeconds(60);
                client.Connect();
                cancellationToken.ThrowIfCancellationRequested();
                if (folder != "/" && !client.Exists(folder)) CreateDirectories(client, folder);
                using (var stream = new MemoryStream(request.Data, writable: false))
                    client.UploadFile(stream, remote, canOverride: true);
                client.Disconnect();
                progress?.Report(1);
                return new UploadResult(FtpUploader.PublicUrl(_settings, name, remote, "sftp"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                throw UploadHelpers.Describe(ex, "the SFTP server");
            }
        }, cancellationToken);
    }

    private static void CreateDirectories(SftpClient client, string folder)
    {
        string current = string.Empty;
        foreach (string part in folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + part;
            if (!client.Exists(current)) client.CreateDirectory(current);
        }
    }
}

public static class UploaderFactory
{
    /// <summary>Builds the uploader the user configured. Throws <see cref="UploadException"/> describing what is missing.</summary>
    public static IUploader Create(UploadSettings settings, ISecretProtector secrets)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(secrets);
        return settings.Provider switch
        {
            UploadProvider.Imgur => new ImgurUploader(settings.ImgurClientId),
            UploadProvider.S3 => new S3Uploader(settings, secrets.Unprotect(settings.S3SecretKeyProtected)),
            UploadProvider.Ftp => new FtpUploader(settings, secrets.Unprotect(settings.FtpPasswordProtected)),
            UploadProvider.Sftp => new SftpUploader(settings, secrets.Unprotect(settings.FtpPasswordProtected)),
            UploadProvider.CustomHttp => new CustomHttpUploader(settings),
            UploadProvider.LocalFolder => new LocalFolderUploader(settings),
            _ => throw new UploadException("No upload destination is set up yet. Choose one in Settings › Upload."),
        };
    }
}
