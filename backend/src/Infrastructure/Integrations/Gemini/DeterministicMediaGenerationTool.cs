using System.Text;
using Backend.Core.Integrations;

namespace Backend.Infrastructure.Integrations.Gemini;

/// <summary>
/// Deterministic, network-free <see cref="IMediaGenerationTool"/> (selected by <c>Gemini:Mode=mock</c>):
/// returns a fixed asset per modality — a 1×1 PNG for <c>image</c>, a small structurally-valid mp4 for
/// <c>video</c> (DL-058) — so CI/compose and eval runs (blanked keys, DL-053) render media with zero live
/// Gemini/Veo calls and zero spend (CLAUDE.md: CI on mocks only). The video asset carries the brief's
/// stamped <c>DurationSec</c> so a video <see cref="MediaResult"/> is replayable exactly like an image one.
/// Idempotency comes from the Media node's deterministic asset key, not from this tool.
/// </summary>
public sealed class DeterministicMediaGenerationTool : IMediaGenerationTool
{
    private const string VideoModality = "video";
    private const int DefaultVideoDurationSec = 5;

    // Smallest valid PNG: a 1×1 transparent pixel.
    private static readonly byte[] _onePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    // A small structurally-valid mp4 (ISO BMFF): an 'ftyp' box (so the file is recognizable as mp4 by
    // signature/content-type) followed by a 'free' padding box. Deterministic and offline — the live Veo
    // path produces the genuine playable clip; this stand-in is for replayable CI/eval video runs.
    private static readonly byte[] _tinyMp4 = BuildTinyMp4();

    public Task<MediaResult> GenerateAsync(
        MediaGenerationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.Equals(request.Brief.Modality, VideoModality, StringComparison.OrdinalIgnoreCase))
        {
            var duration = request.Brief.DurationSec ?? DefaultVideoDurationSec;
            return Task.FromResult(new MediaResult(_tinyMp4, "video/mp4", duration));
        }

        return Task.FromResult(new MediaResult(_onePixelPng, "image/png"));
    }

    private static byte[] BuildTinyMp4()
    {
        var ftypPayload = new List<byte>();
        ftypPayload.AddRange("isom"u8.ToArray());        // major_brand
        ftypPayload.AddRange([0x00, 0x00, 0x02, 0x00]);  // minor_version 0x200
        ftypPayload.AddRange("isom"u8.ToArray());        // compatible_brands…
        ftypPayload.AddRange("iso2"u8.ToArray());
        ftypPayload.AddRange("mp41"u8.ToArray());

        return [.. Box("ftyp", [.. ftypPayload]), .. Box("free", new byte[64])];
    }

    private static byte[] Box(string type, byte[] payload)
    {
        var size = 8 + payload.Length;
        var box = new byte[size];
        box[0] = (byte)(size >> 24);
        box[1] = (byte)(size >> 16);
        box[2] = (byte)(size >> 8);
        box[3] = (byte)size;
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        payload.CopyTo(box, 8);
        return box;
    }
}
