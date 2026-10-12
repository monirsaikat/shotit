namespace ShotIt.Core.Upload;

public sealed record UploadRequest(byte[] Data, string FileName, string ContentType);

public sealed record UploadResult(string Url, string? DeleteUrl = null);

/// <summary>Thrown for any upload failure with a message that is safe and useful to show the user.</summary>
public sealed class UploadException : Exception
{
    public UploadException()
    {
    }

    public UploadException(string message)
        : base(message)
    {
    }

    public UploadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public interface IUploader
{
    string Name { get; }

    /// <summary>Uploads the image. Implementations throw <see cref="UploadException"/> on every failure.</summary>
    Task<UploadResult> UploadAsync(UploadRequest request, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>Encrypts secrets at rest. The Windows implementation uses DPAPI scoped to the current user.</summary>
public interface ISecretProtector
{
    string Protect(string plainText);

    /// <summary>Returns an empty string when the value cannot be decrypted (other user, other machine, corrupted).</summary>
    string Unprotect(string protectedText);
}
