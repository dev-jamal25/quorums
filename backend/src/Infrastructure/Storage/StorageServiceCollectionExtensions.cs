using Backend.Core.Storage;
using Backend.Infrastructure.Configuration.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Minio;

namespace Backend.Infrastructure.Storage;

public static class StorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers the MinIO client (Singleton — it is a connection pool) and the
    /// <see cref="IStorageService"/> seam. The endpoint is host:port only; this seam
    /// owns the SSL decision (dev MinIO is plaintext), never a committed scheme.
    /// </summary>
    public static IServiceCollection AddStorage(this IServiceCollection services)
    {
        services.AddSingleton<IMinioClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MinioOptions>>().Value;
            var (host, port) = ParseEndpoint(options.Endpoint);

            return new MinioClient()
                .WithEndpoint(host, port)
                .WithCredentials(options.AccessKey, options.SecretKey)
                .WithSSL(false)
                .Build();
        });

        services.AddSingleton<IStorageService, MinioStorage>();
        return services;
    }

    private static (string Host, int Port) ParseEndpoint(string endpoint)
    {
        // host:port only (no scheme) per the scaffold-hardening convention.
        var parts = endpoint.Split(':', 2);
        var host = parts[0];
        var port = parts.Length == 2 && int.TryParse(parts[1], out var parsed) ? parsed : 9000;
        return (host, port);
    }
}
