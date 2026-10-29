using Sandbox;
using Sandbox.Mounting;
using Sade.Contracts;

namespace Sade.Mount;

internal sealed class TextureLoader(string id) : ResourceLoader<SadeMount>
{
    protected override object Load()
    {
        var data = Host.Reader.LoadTexture(id);
        if (data.Mips.Length == 0) throw new InvalidDataException($"{id}: no texture mip data.");
        var first = data.Mips[0];
        if (first.Width is < 1 or > short.MaxValue || first.Height is < 1 or > short.MaxValue) throw new InvalidDataException($"{id}: invalid texture dimensions.");
        var format = data.Format.Replace("PF_", "").ToUpperInvariant() switch
        {
            "DXT1" or "BC1" => ImageFormat.DXT1,
            "DXT3" or "BC2" => ImageFormat.DXT3,
            "DXT5" or "BC3" => ImageFormat.DXT5,
            "BC4" => ImageFormat.ATI1N,
            "BC5" => ImageFormat.ATI2N,
            "BC6H" => ImageFormat.BC6H,
            "BC7" => ImageFormat.BC7,
            "B8G8R8A8" or "BGRA8888" or "BGRA8" => data.Srgb ? ImageFormat.BGRA8888 : ImageFormat.BGRA8888_LINEAR,
            "R8G8B8A8" or "RGBA8888" or "RGBA8" => data.Srgb ? ImageFormat.RGBA8888 : ImageFormat.RGBA8888_LINEAR,
            "G8" or "R8" => ImageFormat.I8,
            "FLOATRGBA" or "RGBA16F" => ImageFormat.RGBA16161616F,
            _ => throw new NotSupportedException($"{id}: texture format {data.Format}.")
        };
        var bytes = TextureMipData.Pack(data);
        return new Texture2DBuilder().WithName(Path).WithSize(first.Width, first.Height).WithFormat(format).WithMips(data.Mips.Length).WithData(bytes).WithStaticUsage().Finish();
    }
}

internal sealed class MaterialLoader(string id) : ResourceLoader<SadeMount>
{
    protected override object Load()
    {
        var data = Host.Reader.LoadMaterial(id);
        var material = Material.Create(Path, "shaders/sade_surface.shader", false);
        material.Set("g_tColor", TextureFor(data.ColorTexture, Texture.White));
        material.Set("g_tNormal", TextureFor(data.NormalTexture, FlatNormal));
        material.Set("g_tRoughness", TextureFor(data.RoughnessTexture, Texture.White));
        material.Set("g_tMetalness", TextureFor(data.MetallicTexture, Texture.White));
        material.Set("g_tOpacity", TextureFor(data.OpacityTexture, Texture.White));
        material.Set("g_tEmissive", TextureFor(data.EmissiveTexture, Texture.Black));
        material.Set("g_vTint", new Vector4(data.Tint.X, data.Tint.Y, data.Tint.Z, data.Tint.W));
        material.Set("g_flRoughness", data.Roughness);
        material.Set("g_flMetalness", data.Metallic);
        material.Set("g_flOpacity", data.Opacity);
        material.Set("g_flAlphaTestReference", data.AlphaCutoff);
        material.SetFeature("F_ALPHA_TEST", data.BlendMode.Contains("Masked", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
        material.SetFeature("F_TRANSLUCENT", data.BlendMode.Contains("Translucent", StringComparison.OrdinalIgnoreCase) || data.BlendMode.Contains("Additive", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
        material.SetFeature("F_ADDITIVE_BLEND", data.BlendMode.Contains("Additive", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
        material.SetFeature("F_RENDER_BACKFACES", data.TwoSided ? 1 : 0);
        return material;
    }
    private Texture TextureFor(string? textureId, Texture fallback)
    {
        var path = Host.PathFor(AssetKind.Texture, textureId);
        if (path is null) return fallback;
        var texture = Texture.Load(path);
        return texture is null || texture.IsError ? fallback : texture;
    }
    private static Texture? flatNormal;
    private static Texture FlatNormal => flatNormal is { IsValid: true } ? flatNormal : flatNormal = Texture.Create(1, 1).WithData(new byte[] { 128, 128, 255, 255 }).Finish();
}

internal sealed class SoundLoader(string id) : ResourceLoader<SadeMount>
{
    protected override object Load()
    {
        var sound = Host.Reader.LoadSound(id);
        var options = new SoundFile.LoadOptions { Loop = sound.Looping };
        return sound.Format.ToUpperInvariant() switch
        {
            "OGG" or "VORBIS" => SoundFile.FromOgg(Path, sound.Data, options),
            "WAV" or "WAVE" => SoundFile.FromWav(Path, sound.Data, options),
            "MP3" => SoundFile.FromMp3(Path, sound.Data, options),
            "PCM" or "PCM16" => SoundFile.FromPcm(Path, sound.Data, new SoundFile.PcmOptions { Channels = sound.Channels, Rate = (uint)sound.SampleRate, Bits = 16, Loop = sound.Looping }),
            _ => throw new NotSupportedException($"{id}: audio codec {sound.Format} is not supported by this mount.")
        };
    }
}
