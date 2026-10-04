using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Configuration;
using SIRIAUTOPOST.Application.Interfaces;

namespace SIRIAUTOPOST.Infrastructure.Services;

// Reads files from a Cloudflare R2 bucket through its S3 API. Settings: AppSettings:R2:{Endpoint,BucketName}
// (env AppSettings__R2__*) and R2:{AccessKeyId,SecretAccessKey} (env R2__*); an AppSettings:R2 key also works for all four.
public sealed class R2ObjectStorage : IObjectStorage, IDisposable
{
    private readonly AmazonS3Client? client;
    private readonly string bucket;

    public R2ObjectStorage(IConfiguration config)
    {
        string Get(string name) =>
            config[$"AppSettings:R2:{name}"] is { Length: > 0 } a ? a : config[$"R2:{name}"] ?? "";
        var endpoint = Get("Endpoint");
        bucket = Get("BucketName");
        var key = Get("AccessKeyId");
        var secret = Get("SecretAccessKey");
        if (endpoint == "" || bucket == "" || key == "" || secret == "") return;
        client = new AmazonS3Client(new BasicAWSCredentials(key, secret),
            new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true, AuthenticationRegion = "auto" });
    }

    public bool Enabled => client is not null;

    public async Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default)
    {
        if (client is null) return null;
        try
        {
            var r = await client.GetObjectAsync(bucket, key, ct);
            return r.ResponseStream;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public void Dispose() => client?.Dispose();
}
