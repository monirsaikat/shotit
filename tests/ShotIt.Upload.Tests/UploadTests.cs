using System.Net;
using System.Text;
using ShotIt.Core.Settings;
using ShotIt.Core.Upload;
using ShotIt.Upload;

namespace ShotIt.Upload.Tests;

/// <summary>A loopback web server that records what it received.</summary>
internal sealed class LoopbackServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _stop = new();

    public LoopbackServer(Func<HttpListenerRequest, byte[], (int Status, string Body)> handler)
    {
        HttpListener? listener = null;
        for (int attempt = 0; listener is null; attempt++)
        {
            int port = Random.Shared.Next(20000, 60000);
            Url = $"http://127.0.0.1:{port}";
            var candidate = new HttpListener();
            candidate.Prefixes.Add(Url + "/");
            try
            {
                candidate.Start();
                listener = candidate;
            }
            catch (HttpListenerException) when (attempt < 20)
            {
                candidate.Close();
            }
        }

        _listener = listener;
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    using var ms = new MemoryStream();
                    await context.Request.InputStream.CopyToAsync(ms);
                    var (status, body) = handler(context.Request, ms.ToArray());
                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.StatusCode = status;
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
                catch (Exception)
                {
                    if (!_listener.IsListening) return;
                }
            }
        });
    }

    public string Url { get; private set; } = string.Empty;

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        _stop.Dispose();
    }
}

internal sealed class PlainSecrets : ISecretProtector
{
    public string Protect(string plainText) => plainText;

    public string Unprotect(string protectedText) => protectedText;
}

public class SigV4Tests
{
    private const string AccessKey = "AKIAIOSFODNN7EXAMPLE";
    private const string SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    private const string EmptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private static readonly DateTime When = new(2013, 5, 24, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MatchesAmazonGetObjectExample()
    {
        var headers = new Dictionary<string, string>
        {
            ["Host"] = "examplebucket.s3.amazonaws.com",
            ["Range"] = "bytes=0-9",
            ["x-amz-content-sha256"] = EmptyHash,
            ["x-amz-date"] = "20130524T000000Z",
        };
        string auth = AwsSigV4.Authorization("GET", "/test.txt", [], headers, EmptyHash, AccessKey, SecretKey, "us-east-1", "s3", When);
        Assert.Equal(
            "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request,SignedHeaders=host;range;x-amz-content-sha256;x-amz-date,Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41",
            auth);
    }

    [Fact]
    public void MatchesAmazonPutObjectExample()
    {
        const string hash = "44ce7dd67c959e0d3524ffac1771dfbba87d2b6b4b4e99e42034a8b803f8b072";
        Assert.Equal(hash, AwsSigV4.HexSha256(Encoding.UTF8.GetBytes("Welcome to Amazon S3.")));
        var headers = new Dictionary<string, string>
        {
            ["Date"] = "Fri, 24 May 2013 00:00:00 GMT",
            ["Host"] = "examplebucket.s3.amazonaws.com",
            ["x-amz-content-sha256"] = hash,
            ["x-amz-date"] = "20130524T000000Z",
            ["x-amz-storage-class"] = "REDUCED_REDUNDANCY",
        };
        string auth = AwsSigV4.Authorization("PUT", "/test$file.text", [], headers, hash, AccessKey, SecretKey, "us-east-1", "s3", When);
        Assert.EndsWith("Signature=98ad721746da40c64f1a55b78f14c238d841ea1380cd77a1b5971af0ece108bd", auth, StringComparison.Ordinal);
    }

    [Fact]
    public void MatchesAmazonListObjectsExampleWithQuery()
    {
        var headers = new Dictionary<string, string>
        {
            ["Host"] = "examplebucket.s3.amazonaws.com",
            ["x-amz-content-sha256"] = EmptyHash,
            ["x-amz-date"] = "20130524T000000Z",
        };
        var query = new[] { new KeyValuePair<string, string>("prefix", "J"), new KeyValuePair<string, string>("max-keys", "2") };
        string auth = AwsSigV4.Authorization("GET", "/", query, headers, EmptyHash, AccessKey, SecretKey, "us-east-1", "s3", When);
        Assert.EndsWith("Signature=34b48302e7b5fa45bde8084f4b7868a86f0a534bc59db6670ed5711ef69dc6f7", auth, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("abc-_.~XYZ019", "abc-_.~XYZ019")]
    [InlineData("a b", "a%20b")]
    [InlineData("a+b=c", "a%2Bb%3Dc")]
    [InlineData("é", "%C3%A9")]
    public void UriEncodeFollowsTheSpecification(string input, string expected) => Assert.Equal(expected, AwsSigV4.UriEncode(input, true));

    [Fact]
    public void UriEncodeKeepsSlashesInPaths()
    {
        Assert.Equal("/a/b%20c", AwsSigV4.UriEncode("/a/b c", false));
        Assert.Equal("%2Fa", AwsSigV4.UriEncode("/a", true));
    }
}

public class HelperTests
{
    [Fact]
    public void RandomNamesAreLongUniqueAndUrlSafe()
    {
        var names = Enumerable.Range(0, 2000).Select(_ => UploadHelpers.RandomName()).ToHashSet();
        Assert.Equal(2000, names.Count);
        Assert.All(names, n =>
        {
            Assert.Equal(22, n.Length);
            Assert.All(n, c => Assert.True(char.IsAsciiLetterOrDigit(c)));
        });
    }

    [Fact]
    public void RandomFileNameKeepsTheExtensionOnly()
    {
        string name = UploadHelpers.RandomFileName(new UploadRequest([1], "Secret Project Plan.PNG", "image/png"));
        Assert.EndsWith(".png", name, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret", name, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"data\":{\"link\":\"https://x/y.png\"}}", "data.link", "https://x/y.png")]
    [InlineData("{\"files\":[{\"url\":\"https://a\"},{\"url\":\"https://b\"}]}", "files.1.url", "https://b")]
    [InlineData("{\"id\":42}", "id", "42")]
    [InlineData("{\"a\":{\"b\":1}}", "a.c", null)]
    [InlineData("{\"a\":[1]}", "a.5", null)]
    [InlineData("not json", "a", null)]
    [InlineData("{\"a\":{\"b\":1}}", "a", null)]
    public void ReadJsonPath(string json, string path, string? expected) => Assert.Equal(expected, UploadHelpers.ReadJsonPath(json, path));

    [Theory]
    [InlineData("https://example.com/up", true)]
    [InlineData("http://127.0.0.1:8080/up", true)]
    [InlineData("http://localhost/up", true)]
    [InlineData("http://example.com/up", false)]
    [InlineData("ftp://example.com/up", false)]
    [InlineData("example.com", false)]
    [InlineData("", false)]
    public void OnlyEncryptedOrLoopbackAddressesAreAccepted(string url, bool accepted)
    {
        if (accepted) Assert.NotNull(UploadHelpers.RequireSecure(url, "The address"));
        else Assert.Throws<UploadException>(() => UploadHelpers.RequireSecure(url, "The address"));
    }

    [Theory]
    [InlineData("https://a.b/c", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///c:/x", false)]
    [InlineData(null, false)]
    public void IsWebUrl(string? text, bool expected) => Assert.Equal(expected, UploadHelpers.IsWebUrl(text));
}

public class UploaderTests
{
    private static readonly UploadRequest Request = new([1, 2, 3, 4, 5, 6, 7, 8], "My Screenshot.png", "image/png");

    [Fact]
    public async Task ImgurSendsClientIdAndReturnsLinkAndDeleteLink()
    {
        string? authorization = null, contentType = null;
        int bodyLength = 0;
        using var server = new LoopbackServer((request, body) =>
        {
            authorization = request.Headers["Authorization"];
            contentType = request.ContentType;
            bodyLength = body.Length;
            return (200, "{\"data\":{\"link\":\"https://i.imgur.com/abc.png\",\"deletehash\":\"XyZ\"},\"success\":true}");
        });

        var result = await new ImgurUploader("my-client", server.Url + "/3/image").UploadAsync(Request, null, CancellationToken.None);
        Assert.Equal("https://i.imgur.com/abc.png", result.Url);
        Assert.Equal("https://imgur.com/delete/XyZ", result.DeleteUrl);
        Assert.Equal("Client-ID my-client", authorization);
        Assert.StartsWith("multipart/form-data", contentType, StringComparison.Ordinal);
        Assert.True(bodyLength > Request.Data.Length);
    }

    [Fact]
    public async Task ImgurFailureExplainsTheStatus()
    {
        using var server = new LoopbackServer((_, _) => (403, "{\"data\":{\"error\":\"Invalid client_id\"}}"));
        var ex = await Assert.ThrowsAsync<UploadException>(() => new ImgurUploader("bad", server.Url).UploadAsync(Request, null, CancellationToken.None));
        Assert.Contains("403", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Invalid client_id", ex.Message, StringComparison.Ordinal);
        Assert.Contains("credentials", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ImgurNeedsAClientId() => Assert.Throws<UploadException>(() => new ImgurUploader("  "));

    [Fact]
    public async Task UnreachableServerGivesAReadableError()
    {
        // Nothing listens on port 9 of the loopback address.
        var ex = await Assert.ThrowsAsync<UploadException>(() => new ImgurUploader("id", "http://127.0.0.1:9/x").UploadAsync(Request, null, CancellationToken.None));
        Assert.Contains("Imgur", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task CancellationIsNotReportedAsAFailure()
    {
        using var server = new LoopbackServer((_, _) =>
        {
            Thread.Sleep(3000);
            return (200, "{}");
        });
        using var cts = new CancellationTokenSource(150);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ImgurUploader("id", server.Url).UploadAsync(Request, null, cts.Token));
    }

    [Fact]
    public async Task S3SignsThePutAndUsesAnUnguessableKey()
    {
        var now = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        string? path = null, expected = null, actual = null, method = null;
        byte[] received = [];
        using var server = new LoopbackServer((request, body) =>
        {
            method = request.HttpMethod;
            path = request.Url!.AbsolutePath;
            received = body;
            actual = request.Headers["Authorization"];
            var signed = new Dictionary<string, string>
            {
                ["host"] = request.Headers["Host"]!,
                ["content-type"] = request.ContentType!,
                ["x-amz-content-sha256"] = request.Headers["x-amz-content-sha256"]!,
                ["x-amz-date"] = request.Headers["x-amz-date"]!,
            };
            expected = AwsSigV4.Authorization("PUT", Uri.UnescapeDataString(path), [], signed, AwsSigV4.HexSha256(body), "AK", "SK", "auto", "s3", now);
            return (200, string.Empty);
        });

        var settings = new UploadSettings
        {
            Provider = UploadProvider.S3, S3Endpoint = server.Url, S3Region = "auto", S3Bucket = "shots", S3AccessKey = "AK",
            S3PathPrefix = "/team a/", S3PublicUrlBase = "https://cdn.example.com/", S3PathStyle = true,
        };
        var result = await new S3Uploader(settings, "SK", () => now).UploadAsync(Request, null, CancellationToken.None);

        Assert.Equal("PUT", method);
        Assert.Equal(Request.Data, received);
        Assert.Equal(expected, actual);
        Assert.StartsWith("/shots/team%20a/", path, StringComparison.Ordinal);
        Assert.StartsWith("https://cdn.example.com/team%20a/", result.Url, StringComparison.Ordinal);
        Assert.EndsWith(".png", result.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("Screenshot", result.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task S3FailureShowsTheServerMessage()
    {
        using var server = new LoopbackServer((_, _) => (403, "<Error><Code>SignatureDoesNotMatch</Code><Message>The signature does not match.</Message></Error>"));
        var settings = new UploadSettings { S3Endpoint = server.Url, S3Bucket = "b", S3AccessKey = "AK" };
        var ex = await Assert.ThrowsAsync<UploadException>(() => new S3Uploader(settings, "SK").UploadAsync(Request, null, CancellationToken.None));
        Assert.Contains("The signature does not match.", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("<Code>", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void S3ValidatesItsSettings()
    {
        Assert.Throws<UploadException>(() => new S3Uploader(new UploadSettings { S3AccessKey = "a" }, "s"));
        Assert.Throws<UploadException>(() => new S3Uploader(new UploadSettings { S3Bucket = "b", S3AccessKey = "a" }, string.Empty));
        Assert.Throws<UploadException>(() => new S3Uploader(new UploadSettings { S3Bucket = "b", S3AccessKey = "a", S3Endpoint = "http://minio.example.com" }, "s"));
    }

    [Fact]
    public async Task CustomUploaderSendsHeadersAndReadsJsonPaths()
    {
        string? token = null;
        using var server = new LoopbackServer((request, _) =>
        {
            token = request.Headers["X-Token"];
            return (201, "{\"files\":[{\"url\":\"https://files.example.com/a.png\",\"del\":\"https://files.example.com/del/a\"}]}");
        });
        var settings = new UploadSettings
        {
            CustomUrl = server.Url + "/upload", CustomHeaders = "X-Token: abc123\r\nX-Other: 1",
            CustomResponseUrlPath = "files.0.url", CustomResponseDeletePath = "files.0.del",
        };
        var result = await new CustomHttpUploader(settings).UploadAsync(Request, null, CancellationToken.None);
        Assert.Equal("https://files.example.com/a.png", result.Url);
        Assert.Equal("https://files.example.com/del/a", result.DeleteUrl);
        Assert.Equal("abc123", token);
    }

    [Fact]
    public async Task CustomUploaderAcceptsAPlainTextLink()
    {
        using var server = new LoopbackServer((_, _) => (200, "https://paste.example.com/q1w2\n"));
        var settings = new UploadSettings { CustomUrl = server.Url, CustomResponseUrlPath = string.Empty };
        var result = await new CustomHttpUploader(settings).UploadAsync(Request, null, CancellationToken.None);
        Assert.Equal("https://paste.example.com/q1w2", result.Url);
    }

    [Theory]
    [InlineData("{\"url\":\"javascript:alert(1)\"}")]
    [InlineData("{\"other\":\"https://x\"}")]
    [InlineData("<html>login</html>")]
    public async Task CustomUploaderRejectsRepliesWithoutAWebLink(string reply)
    {
        using var server = new LoopbackServer((_, _) => (200, reply));
        var settings = new UploadSettings { CustomUrl = server.Url };
        await Assert.ThrowsAsync<UploadException>(() => new CustomHttpUploader(settings).UploadAsync(Request, null, CancellationToken.None));
    }

    [Theory]
    [InlineData("no colon here")]
    [InlineData("Bad Name: x")]
    [InlineData(": empty")]
    public void CustomUploaderRejectsMalformedHeaders(string headers) =>
        Assert.Throws<UploadException>(() => new CustomHttpUploader(new UploadSettings { CustomUrl = "https://e.com", CustomHeaders = headers }));

    [Fact]
    public async Task LocalFolderWritesTheFileUnderARandomName()
    {
        string folder = Path.Combine(Path.GetTempPath(), "shotit-upload-" + Guid.NewGuid().ToString("N"), "nested");
        try
        {
            var plain = await new LocalFolderUploader(new UploadSettings { LocalFolder = folder }).UploadAsync(Request, null, CancellationToken.None);
            Assert.True(File.Exists(plain.Url));
            Assert.Equal(Request.Data, await File.ReadAllBytesAsync(plain.Url));

            var linked = await new LocalFolderUploader(new UploadSettings { LocalFolder = folder, LocalPublicUrlBase = "https://my.site/s" })
                .UploadAsync(Request, null, CancellationToken.None);
            Assert.StartsWith("https://my.site/s/", linked.Url, StringComparison.Ordinal);
            Assert.Equal(2, Directory.GetFiles(folder).Length);
            Assert.True(File.Exists(Path.Combine(folder, linked.Url[(linked.Url.LastIndexOf('/') + 1)..])));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, true);
        }
    }

    [Fact]
    public async Task LocalFolderReportsAnUnwritableDestination()
    {
        var uploader = new LocalFolderUploader(new UploadSettings { LocalFolder = "C:\\Windows\\System32\\config\\<bad>|name" });
        await Assert.ThrowsAsync<UploadException>(() => uploader.UploadAsync(Request, null, CancellationToken.None));
    }

    [Fact]
    public void FactoryBuildsTheConfiguredProviderAndExplainsMissingSetup()
    {
        var secrets = new PlainSecrets();
        Assert.Throws<UploadException>(() => UploaderFactory.Create(new UploadSettings(), secrets));
        Assert.IsType<ImgurUploader>(UploaderFactory.Create(new UploadSettings { Provider = UploadProvider.Imgur, ImgurClientId = "x" }, secrets));
        Assert.IsType<LocalFolderUploader>(UploaderFactory.Create(new UploadSettings { Provider = UploadProvider.LocalFolder, LocalFolder = "C:\\x" }, secrets));
        Assert.IsType<FtpUploader>(UploaderFactory.Create(new UploadSettings { Provider = UploadProvider.Ftp, FtpHost = "h" }, secrets));
        Assert.IsType<SftpUploader>(UploaderFactory.Create(new UploadSettings { Provider = UploadProvider.Sftp, FtpHost = "h", FtpUser = "u" }, secrets));
        Assert.Throws<UploadException>(() => UploaderFactory.Create(new UploadSettings { Provider = UploadProvider.Sftp, FtpHost = "h" }, secrets));
        Assert.Throws<UploadException>(() => UploaderFactory.Create(new UploadSettings { Provider = UploadProvider.S3, S3Bucket = "b", S3AccessKey = "a" }, secrets));
    }

    [Fact]
    public void FtpLinksUseThePublicBaseWhenGiven()
    {
        var settings = new UploadSettings { FtpHost = "ftp.example.com", FtpPublicUrlBase = "https://example.com/img/" };
        Assert.Equal("https://example.com/img/a.png", FtpUploader.PublicUrl(settings, "a.png", "/www/a.png", "ftp"));
        settings.FtpPublicUrlBase = string.Empty;
        Assert.Equal("sftp://ftp.example.com/www/a.png", FtpUploader.PublicUrl(settings, "a.png", "/www/a.png", "sftp"));
    }
}
