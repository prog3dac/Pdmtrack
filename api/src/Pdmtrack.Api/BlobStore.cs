using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Pdmtrack.Api;

/// <summary>
/// Contenuto dei file, indirizzato per SHA-256 (nome oggetto = hash esadecimale minuscolo).
/// Stessa convenzione di file_versions.storage_path, quindi il passaggio tra storage non tocca il database.
/// </summary>
public interface IBlobStore
{
    Task<bool> ExistsAsync(string sha256, CancellationToken ct);
    /// <summary>Il contenuto e' gia' stato verificato contro l'hash.</summary>
    Task PutAsync(string sha256, Stream content, long length, CancellationToken ct);
    Task<Stream?> OpenReadAsync(string sha256, CancellationToken ct);
}

public static class BlobStoreSetup
{
    /// <summary>Pdm:Storage:Kind = "FileSystem" (Pdm:Storage:Path) oppure "S3" (MinIO, Supabase Storage, AWS).</summary>
    public static IServiceCollection AddBlobStore(this IServiceCollection services, IConfiguration config)
    {
        var s = config.GetSection("Pdm:Storage");
        if (string.Equals(s["Kind"], "S3", StringComparison.OrdinalIgnoreCase))
        {
            var client = new AmazonS3Client(new BasicAWSCredentials(s["S3:AccessKey"], s["S3:SecretKey"]),
                new AmazonS3Config { ServiceURL = s["S3:ServiceUrl"], ForcePathStyle = true });
            services.AddSingleton<IBlobStore>(new S3BlobStore(client, s["S3:Bucket"] ?? "vault"));
        }
        else
        {
            services.AddSingleton<IBlobStore>(new FileSystemBlobStore(s["Path"] ?? Path.Combine(AppContext.BaseDirectory, "vault")));
        }
        return services;
    }
}

public sealed class FileSystemBlobStore(string root) : IBlobStore
{
    private string PathOf(string sha) => Path.Combine(root, sha[..2], sha[2..4], sha);

    public Task<bool> ExistsAsync(string sha256, CancellationToken ct) => Task.FromResult(File.Exists(PathOf(sha256)));

    public async Task PutAsync(string sha256, Stream content, long length, CancellationToken ct)
    {
        var target = PathOf(sha256);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var f = File.Create(tmp)) await content.CopyToAsync(f, ct);
        File.Move(tmp, target, overwrite: true); // atomico: un lettore non vede mai un file a meta'
    }

    public Task<Stream?> OpenReadAsync(string sha256, CancellationToken ct)
    {
        var p = PathOf(sha256);
        return Task.FromResult<Stream?>(File.Exists(p) ? File.OpenRead(p) : null);
    }
}

public sealed class S3BlobStore(IAmazonS3 s3, string bucket) : IBlobStore
{
    public async Task<bool> ExistsAsync(string sha256, CancellationToken ct)
    {
        try { await s3.GetObjectMetadataAsync(bucket, sha256, ct); return true; }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return false; }
    }

    public Task PutAsync(string sha256, Stream content, long length, CancellationToken ct) =>
        s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = sha256, InputStream = content, AutoCloseStream = false }, ct);

    public async Task<Stream?> OpenReadAsync(string sha256, CancellationToken ct)
    {
        try { return (await s3.GetObjectAsync(bucket, sha256, ct)).ResponseStream; }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }
}
