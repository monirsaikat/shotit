using System.Text;
using ShotIt.Core.Settings;
using ShotIt.Core.Upload;
using ShotIt.Upload;

namespace ShotIt.Upload.Tests;

public class FreeLinkTests
{
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

        var result = await new FreeLinkUploader("24h", server.Url + "/api.php").UploadAsync(Request, null, CancellationToken.None);
        Assert.Equal("https://litter.catbox.moe/abc123.png", result.Url);
        Assert.Contains("name=reqtype", body, StringComparison.Ordinal);
        Assert.Contains("fileupload", body, StringComparison.Ordinal);
        Assert.Contains("name=time", body, StringComparison.Ordinal);
        Assert.Contains("24h", body, StringComparison.Ordinal);
        Assert.Contains("name=fileToUpload", body, StringComparison.Ordinal);
        // The original file name must not leave the computer.
        Assert.DoesNotContain("Secret", body, StringComparison.Ordinal);
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

        var uploader = new FreeLinkUploader("never", server.Url);
        var result = await uploader.UploadAsync(Request, null, CancellationToken.None);
        Assert.Equal("https://files.catbox.moe/xyz.png", result.Url);
        Assert.DoesNotContain("name=time", body, StringComparison.Ordinal);
        Assert.Contains("permanent", uploader.Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusalInPlainTextIsReported()
    {
        using var server = new LoopbackServer((_, _) => (200, "File type not allowed."));
        var ex = await Assert.ThrowsAsync<UploadException>(() => new FreeLinkUploader("1h", server.Url).UploadAsync(Request, null, CancellationToken.None));
        Assert.Contains("File type not allowed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreachableHostMentionsBlockedNetworks()
    {
        var ex = await Assert.ThrowsAsync<UploadException>(() => new FreeLinkUploader("1h", "http://127.0.0.1:9/x").UploadAsync(Request, null, CancellationToken.None));
        Assert.Contains("Some networks block", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("72h", "72h")]
    [InlineData("1H", "1h")]
    [InlineData("forever", "72h")]
    [InlineData(null, "72h")]
    public void UnknownExpiryFallsBackToThreeDays(string? setting, string expected)
    {
        var uploader = new FreeLinkUploader(setting);
        string label = FreeLinkUploader.Expiries.First(e => e.Value == expected).Label;
        Assert.Contains(label, uploader.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void FactoryBuildsTheFreeLinkUploader()
    {
        var settings = new UploadSettings { Provider = UploadProvider.FreeLink, FreeLinkExpiry = "never" };
        Assert.IsType<FreeLinkUploader>(UploaderFactory.Create(settings, new NoSecrets()));
    }

    private sealed class NoSecrets : ISecretProtector
    {
        public string Protect(string plainText) => plainText;

        public string Unprotect(string protectedText) => protectedText;
    }
}
