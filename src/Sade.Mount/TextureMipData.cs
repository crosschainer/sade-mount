using Sade.Contracts;

namespace Sade.Mount;

/// <summary>Validates Unreal's largest-first mip chain and packs the smallest-first runtime upload used by s&box.</summary>
internal static class TextureMipData
{
    internal static byte[] Pack(TextureData texture)
    {
        if (texture.Mips.Length == 0) throw Invalid(texture, "no mip data");
        var width = texture.Mips[0].Width;
        var height = texture.Mips[0].Height;
        if (width is < 1 or > short.MaxValue || height is < 1 or > short.MaxValue)
            throw Invalid(texture, "invalid texture dimensions");
        var maximumMips = 1;
        for (var size = Math.Max(width, height); size > 1; size >>= 1) maximumMips++;
        if (texture.Mips.Length > maximumMips) throw Invalid(texture, "too many mip levels");

        int length = 0;
        var blockCompressed = texture.Format.Replace("PF_", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant()
            is "DXT1" or "DXT3" or "DXT5" or "BC1" or "BC2" or "BC3" or "BC4" or "BC5" or "BC6H" or "BC7";
        foreach (var mip in texture.Mips)
        {
            // Cooked Unreal BC tails can report a physical 4x4 block for logical 2x2/1x1 levels.
            var logicalDimensions = mip.Width == width && mip.Height == height;
            var paddedDimensions = blockCompressed && mip.Width == Math.Max(4, width) && mip.Height == Math.Max(4, height);
            if (!logicalDimensions && !paddedDimensions)
                throw Invalid(texture, $"expected mip dimensions {width}x{height}, received {mip.Width}x{mip.Height}");
            var expected = ByteCount(texture.Format, width, height);
            if (mip.Data is null || mip.Data.Length != expected)
                throw Invalid(texture, $"mip {width}x{height} requires {expected} bytes, received {mip.Data?.Length ?? 0}");
            length = checked(length + expected);
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }

        var packed = new byte[length];
        var offset = 0;
        // Texture2DBuilder uses Source's smallest-to-largest mip layout, unlike Unreal.
        // See the engine TextureMipUploadTest.BuildData and Source1 Texture.Pixels upload.
        for (var i = texture.Mips.Length - 1; i >= 0; i--)
        {
            var data = texture.Mips[i].Data;
            data.CopyTo(packed, offset);
            offset += data.Length;
        }
        return packed;
    }

    private static int ByteCount(string format, int width, int height)
    {
        var normalized = format.Replace("PF_", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
        return normalized switch
        {
            "DXT1" or "BC1" or "BC4" => checked(((width + 3) / 4) * ((height + 3) / 4) * 8),
            "DXT3" or "BC2" or "DXT5" or "BC3" or "BC5" or "BC6H" or "BC7" => checked(((width + 3) / 4) * ((height + 3) / 4) * 16),
            "B8G8R8A8" or "BGRA8888" or "BGRA8" or "R8G8B8A8" or "RGBA8888" or "RGBA8" => checked(width * height * 4),
            "G8" or "R8" => checked(width * height),
            "FLOATRGBA" or "RGBA16F" => checked(width * height * 8),
            _ => throw new NotSupportedException($"Texture format {format} has no upload byte layout.")
        };
    }

    private static InvalidDataException Invalid(TextureData texture, string reason) => new($"{texture.Id}: {reason}.");
}
