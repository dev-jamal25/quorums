using System.Security.Cryptography;
using System.Text;

namespace Backend.Core.Common;

/// <summary>
/// Derives a stable <see cref="Guid"/> from a seed guid plus a purpose label. Used to
/// make side effects idempotent under Hangfire retry (DL-022): the same run always
/// yields the same asset id (so a re-run overwrites one MinIO key) and the same
/// publish key (so a re-run does not double-publish). Not a security primitive — the
/// hash is used purely as a deterministic way to fill 16 bytes.
/// </summary>
public static class DeterministicGuid
{
    public static Guid From(Guid seed, string purpose)
    {
        var input = Encoding.UTF8.GetBytes($"{seed:N}:{purpose}");
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        return new Guid(hash[..16]);
    }
}
