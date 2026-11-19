using System.Text;
using ShotIt.Core.Settings;
using ShotIt.Core.Upload;
using ShotIt.Upload;

namespace ShotIt.Upload.Tests;

public class FreeLinkTests
{
    private const string Unreachable = "http://127.0.0.1:9/x";
    private const string UguuReply = "{\"success\":true,\"files\":[{\"url\":\"https:\\/\\/h.uguu.se\\/abc.png\"}]}";
    private static readonly UploadRequest Request = new([1, 2, 3, 4, 5, 6, 7, 8], "Secret Plan.png", "image/png");

    [Fact]
    public async Task ExpiringLinkSendsTheTimeAndReturnsTheLink()
    {
        string body = string.Empty;
        using var server = new LoopbackServer((_, data) =>
        {
            body = Encoding.UTF8.GetString(data);
            return (200, "https://litter.catbox.moe/abc123.png\n");
        });

        var result = await new FreeLinkUploader("24h", server.Url + "/api.php", Unreachable).UploadAsync(Request, null, CancellationToken.None);
        Assert.Equal("https://litter.catbox.moe/abc123.png", result.Url);
        Assert.Null(result.Note);
        Assert.Contains("name=\"reqtype\"", body, StringComparison.Ordinal);
        Assert.Contains("fileupload", body, StringComparison.Ordinal);
        Assert.Contains("name=\"time\"", body, StringComparison.Ordinal);
        Assert.Contains("24h", body, StringComparison.Ordinal);
        Assert.Contains("name=\"fileToUpload\"", body, StringComparison.Ordinal);
        // The original file name must not leave the computer.
        Assert.DoesNotContain("Secret", body, StringComparison.Ordinal);
        // The host's form parser does not understand the extended filename* parameter.
        Assert.DoesNotContain("filename*", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PermanentLinkSendsNoTime()
    {
        string body = string.Empty;
        using var server = new LoopbackServer((_, data) =>
        {
            body = Encoding.UTF8.GetString(data);
            return (200, "https://files.catbox.moe/xyz.png");
        });

        var uploader = new FreeLinkUploader("never", server.Url, Unreachable);
        var result = await uploader.UploadAsync(Request, null, CancellationToken.None);
        Assert.Equal("https://files.catbox.moe/xyz.png", result.Url);
        Assert.DoesNotContain("name=\"time\"", body, StringComparison.Ordinal);
        Assert.Contains("permanent", uploader.Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThreeHoursGoesStraightToUguu()
    {
        string body = string.Empty;
        using var uguu = new LoopbackServer((_, data) =>
        {
            body = Encoding.UTF8.GetString(data);
            return (200, UguuReply);
        });

        var result = await new FreeLinkUploader("3h", Unreachable, uguu.Url).UploadAsync(Request, null, CancellationToken.None);
        Assert.Equal("https://h.uguu.se/abc.png", result.Url);
        Assert.Null(result.Note);
        Assert.Contains("name=\"files[]\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABlockedCatboxFallsBackToUguuAndSaysSo()
    {
        using var blocked = new LoopbackServer((_, _) => (403, "<!DOCTYPE html><html><title>403 | Forbidden</title></html>"));
        using var uguu = new LoopbackServer((_, _) => (200, UguuReply));

        var result = await new FreeLinkUploader("72h", blocked.Url, uguu.Url).UploadAsync(Request, null, CancellationToken.None);
        Assert.Equal("https://h.uguu.se/abc.png", result.Url);
        Assert.Contains("3 hours", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenBothHostsFailTheMessageSaysSoWithoutQuotingTheFirewallPage()
    {
        using var blocked = new LoopbackServer((_, _) => (403, "<html><body>BunkerWeb</body></html>"));
        var ex = await Assert.ThrowsAsync<UploadException>(() => new FreeLinkUploader("1h", blocked.Url, Unreachable).UploadAsync(Request, null, CancellationToken.None));
        Assert.Contains("403", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Uguu", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("BunkerWeb", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusalOfTheFileIsReportedWithoutTryingAnotherHost()
    {
        int uguuCalls = 0;
        using var catbox = new LoopbackServer((_, _) => (200, "File type not allowed."));
        using var uguu = new LoopbackServer((_, _) =>
        {
            uguuCalls++;
            return (200, UguuReply);
        });
        var ex = await Assert.ThrowsAsync<UploadException>(() => new FreeLinkUploader("1h", catbox.Url, uguu.Url).UploadAsync(Request, null, CancellationToken.None));
        Assert.Contains("File type not allowed", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, uguuCalls);
    }

    [Theory]
    [InlineData("72h", "Litterbox", "3 days")]
    [InlineData("1H", "Litterbox", "1 hour")]
    [InlineData("3h", "Uguu", "3 hours")]
    [InlineData("forever", "Litterbox", "3 days")]
    [InlineData(null, "Litterbox", "3 days")]
    public void ExpiryChoosesTheHost(string? setting, string host, string label)
    {
        var uploader = new FreeLinkUploader(setting);
        Assert.StartsWith(host, uploader.Name, StringComparison.Ordinal);
        Assert.Contains(label, uploader.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void FactoryBuildsTheFreeLinkUploader()
    {
        var settings = new UploadSettings { Provider = UploadProvider.FreeLink, FreeLinkExpiry = "never" };
        Assert.IsType<FreeLinkUploader>(UploaderFactory.Create(settings, new NoSecrets()));
    }

    /// <summary>
    /// Talks to the real hosts. Off unless SHOTIT_LIVE_UPLOAD=1, because it publishes a (blank) picture.
    /// </summary>
    [Theory]
    [InlineData("1h")]
    [InlineData("3h")]
    public async Task LiveUploadReturnsALink(string expiry)
    {
        if (Environment.GetEnvironmentVariable("SHOTIT_LIVE_UPLOAD") != "1") return;
        // A valid 1x1 transparent PNG.
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        var result = await new FreeLinkUploader(expiry).UploadAsync(new UploadRequest(png, "test.png", "image/png"), null, CancellationToken.None);
        Assert.StartsWith("https://", result.Url, StringComparison.Ordinal);
        Assert.EndsWith(".png", result.Url, StringComparison.Ordinal);
    }

    private sealed class NoSecrets : ISecretProtector
    {
        public string Protect(string plainText) => plainText;

        public string Unprotect(string protectedText) => protectedText;
    }
}
