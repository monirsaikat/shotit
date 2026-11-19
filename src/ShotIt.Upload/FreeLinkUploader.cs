using System.Net.Http.Headers;
using ShotIt.Core.Upload;

namespace ShotIt.Upload;

/// <summary>
/// Free public hosting that needs no account or key. Permanent links go to Catbox; links that expire
/// go to Litterbox, run by the same people. Neither lets an anonymous uploader delete a file later,
/// which is why expiring links are the default.
/// </summary>
public sealed class FreeLinkUploader : IUploader
{
    public const string CatboxEndpoint = "https://catbox.moe/user/api.php";
    public const string LitterboxEndpoint = "https://litterbox.catbox.moe/resources/internals/api.php";

    /// <summary>The choices offered in settings, in order. "never" means a permanent link.</summary>
    public static IReadOnlyList<(string Value, string Label)> Expiries { get; } =
    [
        ("1h", "1 hour"),
        ("12h", "12 hours"),
        ("24h", "1 day"),
        ("72h", "3 days"),
        ("never", "Never (permanent)"),
    ];

    private readonly string? _expiry;
    private readonly Uri _endpoint;

    public FreeLinkUploader(string? expiry, string? endpoint = null)
    {
        string value = (expiry ?? string.Empty).Trim().ToLowerInvariant();
        _expiry = value == "never" ? null : Expiries.Any(e => e.Value == value) ? value : "72h";
        _endpoint = UploadHelpers.RequireSecure(endpoint ?? (_expiry is null ? CatboxEndpoint : LitterboxEndpoint), "The free link address");
    }

    public string Name => _expiry is null ? "Catbox (free, permanent link)" : "Litterbox (free link that expires after " + Label(_expiry) + ")";

    /// <summary>Catbox refuses executables and similar; images, GIFs and videos are fine up to these sizes.</summary>
    public long MaxBytes => _expiry is null ? 200L << 20 : 1L << 30;

    private static string Label(string value) => Expiries.First(e => e.Value == value).Label;

    public async Task<UploadResult> UploadAsync(UploadRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string host = _expiry is null ? "Catbox" : "Litterbox";
        if (request.Data.LongLength > MaxBytes) throw new UploadException($"The file is too large for {host} (the limit is {MaxBytes >> 20} MB).");
        try
        {
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent("fileupload"), "reqtype");
            if (_expiry is not null) content.Add(new StringContent(_expiry), "time");
            var file = new ByteArrayContent(request.Data);
            file.Headers.ContentType = new MediaTypeHeaderValue(request.ContentType);
            // A random name: the host keeps the extension, and the original name could reveal what the picture is.
            content.Add(file, "fileToUpload", UploadHelpers.RandomFileName(request));

            using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = content };
            message.Headers.UserAgent.ParseAdd("ShotIt/1.0");
            using var response = await UploadHelpers.Http.SendAsync(message, cancellationToken).ConfigureAwait(false);
            string body = (await UploadHelpers.ReadBodyAsync(response, cancellationToken).ConfigureAwait(false)).Trim();
            if (!response.IsSuccessStatusCode) throw UploadHelpers.HttpFailure(host, response.StatusCode, body);

            // Both hosts answer with the bare link, or with a sentence describing the problem.
            if (!UploadHelpers.IsWebUrl(body) || !body.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new UploadException($"{host} did not accept the file" + (body.Length > 0 ? ": " + (body.Length > 200 ? body[..200] + "…" : body).ReplaceLineEndings(" ") : "."));
            progress?.Report(1);
            return new UploadResult(body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var described = UploadHelpers.Describe(ex, host);
            // Some internet providers and networks block Catbox; say so, because the usual hint does not help.
            if (ex is HttpRequestException)
                throw new UploadException(described.Message + $" Some networks block {host}; if this keeps happening, choose another destination in Settings › Upload.", ex);
            throw described;
        }
    }
}
