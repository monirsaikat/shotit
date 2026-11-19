using System.Net.Http.Headers;
using ShotIt.Core.Upload;

namespace ShotIt.Upload;

/// <summary>
/// Free public hosting that needs no account or key. Links that expire go to Litterbox, permanent ones
/// to Catbox (both run by the same volunteers), and three-hour links to Uguu. Catbox sits behind a
/// firewall that some networks trip, so when it cannot be used the picture goes to Uguu instead and the
/// result says the link is shorter-lived. No host lets an anonymous uploader delete a file later.
/// </summary>
public sealed class FreeLinkUploader : IUploader
{
    public const string CatboxEndpoint = "https://catbox.moe/user/api.php";
    public const string LitterboxEndpoint = "https://litterbox.catbox.moe/resources/internals/api.php";
    public const string UguuEndpoint = "https://uguu.se/upload";

    private const long UguuMaxBytes = 128L << 20;

    /// <summary>The choices offered in settings, in order. "never" means a permanent link.</summary>
    public static IReadOnlyList<(string Value, string Label)> Expiries { get; } =
    [
        ("1h", "1 hour"),
        ("3h", "3 hours"),
        ("12h", "12 hours"),
        ("24h", "1 day"),
        ("72h", "3 days"),
        ("never", "Never (permanent)"),
    ];

    private readonly string _expiry;
    private readonly Uri _catbox;
    private readonly Uri _uguu;

    /// <param name="endpoint">Replaces the Catbox or Litterbox address (tests).</param>
    /// <param name="fallbackEndpoint">Replaces the Uguu address (tests).</param>
    public FreeLinkUploader(string? expiry, string? endpoint = null, string? fallbackEndpoint = null)
    {
        string value = (expiry ?? string.Empty).Trim().ToLowerInvariant();
        _expiry = Expiries.Any(e => e.Value == value) ? value : "72h";
        _catbox = UploadHelpers.RequireSecure(endpoint ?? (_expiry == "never" ? CatboxEndpoint : LitterboxEndpoint), "The free link address");
        _uguu = UploadHelpers.RequireSecure(fallbackEndpoint ?? UguuEndpoint, "The free link address");
    }

    private bool UsesUguuOnly => _expiry == "3h";

    private string PrimaryHost => UsesUguuOnly ? "Uguu" : _expiry == "never" ? "Catbox" : "Litterbox";

    public string Name => _expiry == "never"
        ? "Catbox (free, permanent link)"
        : $"{PrimaryHost} (free link that expires after {Label(_expiry)})";

    private static string Label(string value) => Expiries.First(e => e.Value == value).Label;

    public async Task<UploadResult> UploadAsync(UploadRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (UsesUguuOnly) return await UguuAsync(request, progress, cancellationToken).ConfigureAwait(false);

        long limit = _expiry == "never" ? 200L << 20 : 1L << 30;
        if (request.Data.LongLength > limit) throw new UploadException($"The file is too large for {PrimaryHost} (the limit is {limit >> 20} MB).");
        try
        {
            return await CatboxAsync(request, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (HostUnavailableException primary) when (request.Data.LongLength <= UguuMaxBytes)
        {
            try
            {
                var fallback = await UguuAsync(request, progress, cancellationToken).ConfigureAwait(false);
                return fallback with { Note = $"{PrimaryHost} could not be used from this network, so the link is from Uguu and works for 3 hours." };
            }
            catch (UploadException)
            {
                throw new UploadException(primary.Message + " Uguu, the backup host, could not be used either.", primary);
            }
        }
        catch (HostUnavailableException primary)
        {
            throw new UploadException(primary.Message, primary);
        }
    }

    private async Task<UploadResult> CatboxAsync(UploadRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        string host = PrimaryHost;
        // The host's form parser only finds parts whose names are quoted, and it does not understand the
        // extra filename* parameter .NET adds by default, so every part's header is written out here.
        using var content = new MultipartFormDataContent();
        content.Add(Field("reqtype", new StringContent("fileupload")));
        if (_expiry != "never") content.Add(Field("time", new StringContent(_expiry)));
        content.Add(Field("fileToUpload", FileContent(request), UploadHelpers.RandomFileName(request)));

        string body = await PostAsync(host, _catbox, content, cancellationToken).ConfigureAwait(false);
        // The host answers with the bare link, or with a sentence describing the problem.
        if (!UploadHelpers.IsWebUrl(body) || !body.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new UploadException($"{host} did not accept the file" + (body.Length > 0 ? ": " + Shorten(body) : "."));
        progress?.Report(1);
        return new UploadResult(body);
    }

    private async Task<UploadResult> UguuAsync(UploadRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (request.Data.LongLength > UguuMaxBytes) throw new UploadException("The file is too large for Uguu (the limit is 128 MB).");
        using var content = new MultipartFormDataContent();
        content.Add(Field("files[]", FileContent(request), UploadHelpers.RandomFileName(request)));
        string body;
        try
        {
            body = await PostAsync("Uguu", _uguu, content, cancellationToken).ConfigureAwait(false);
        }
        catch (HostUnavailableException ex)
        {
            throw new UploadException(ex.Message, ex);
        }

        string? link = UploadHelpers.ReadJsonPath(body, "files.0.url");
        if (!UploadHelpers.IsWebUrl(link) || !link!.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new UploadException("Uguu did not accept the file" + (UploadHelpers.ReadJsonPath(body, "description") is { Length: > 0 } why ? ": " + Shorten(why) : "."));
        progress?.Report(1);
        return new UploadResult(link);
    }

    /// <summary>Sends the form and returns the answer. A host that cannot be reached or refuses the request throws <see cref="HostUnavailableException"/>.</summary>
    private static async Task<string> PostAsync(string host, Uri endpoint, HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
            message.Headers.UserAgent.ParseAdd("ShotIt/1.0");
            using var response = await UploadHelpers.Http.SendAsync(message, cancellationToken).ConfigureAwait(false);
            string body = (await UploadHelpers.ReadBodyAsync(response, cancellationToken).ConfigureAwait(false)).Trim();
            if (response.IsSuccessStatusCode) return body;

            // A firewall answers with a whole web page; quoting it would only bury the message.
            bool page = body.StartsWith('<') || body.Contains("<html", StringComparison.OrdinalIgnoreCase);
            var failure = UploadHelpers.HttpFailure(host, response.StatusCode, page ? string.Empty : body);
            throw new HostUnavailableException(failure.Message + $" Some networks are blocked by {host}.", failure);
        }
        catch (Exception ex) when (ex is not HostUnavailableException && (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
        {
            var described = UploadHelpers.Describe(ex, host);
            throw new HostUnavailableException(described.Message, ex);
        }
    }

    private static ByteArrayContent FileContent(UploadRequest request)
    {
        var file = new ByteArrayContent(request.Data);
        file.Headers.ContentType = new MediaTypeHeaderValue(request.ContentType);
        return file;
    }

    private static string Shorten(string text) => (text.Length > 200 ? text[..200] + "…" : text).ReplaceLineEndings(" ");

    private static HttpContent Field(string name, HttpContent content, string? fileName = null)
    {
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "\"" + name + "\"",
            FileName = fileName is null ? null : "\"" + fileName + "\"",
        };
        return content;
    }

    /// <summary>The host could not be used at all (unreachable, blocked, or refused the request), as opposed to refusing this file.</summary>
    private sealed class HostUnavailableException(string message, Exception inner) : Exception(message, inner);
}
