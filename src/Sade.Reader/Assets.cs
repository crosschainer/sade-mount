using System.Numerics;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.Texture;
using Sade.Contracts;

namespace Sade.Reader;

public sealed partial class GameReader
{
    public TextureData LoadTexture(string id)
    {
        lock (gate)
        {
            var texture = Provider.LoadPackageObject<UTexture2D>(id);
            var format = texture.Format.ToString() switch
            {
                "PF_DXT1" => "BC1", "PF_DXT3" => "BC2", "PF_DXT5" => "BC3", "PF_BC4" => "BC4", "PF_BC5" => "BC5",
                "PF_BC6H" => "BC6H", "PF_BC7" => "BC7", "PF_B8G8R8A8" => "BGRA8", "PF_R8G8B8A8" => "RGBA8",
                "PF_G8" => "R8", "PF_FloatRGBA" => "RGBA16F",
                _ => throw new NotSupportedException($"Texture {id} uses unsupported {texture.Format}.")
            };
            var mips = new List<TextureMip>();
            for (var i = 0; i < texture.PlatformData.Mips.Length; i++)
            {
                if (texture.GetMip(i) is { BulkData.Data: { Length: > 0 } data } mip)
                {
                    if (mip.SizeZ > 1) throw new NotSupportedException($"Texture {id} is not two-dimensional.");
                    mips.Add(new TextureMip(mip.SizeX, mip.SizeY, data));
                }
            }
            if (mips.Count == 0) throw new InvalidDataException($"Texture {id} has no readable mip data.");
            return new TextureData(id, format, texture.SRGB, mips.ToArray());
        }
    }

    public SoundData LoadSound(string id)
    {
        lock (gate)
        {
            var sound = Provider.LoadPackageObject<USoundWave>(id);
            string format;
            byte[]? bytes;
            if (sound.bStreaming && sound.RunningPlatformData?.Chunks is { } chunks)
            {
                format = sound.RunningPlatformData.AudioFormat.Text;
                using var stream = new MemoryStream();
                foreach (var chunk in chunks) stream.Write(chunk.BulkData.Data ?? throw new InvalidDataException($"Missing streamed audio chunk for {id}."), 0, chunk.AudioDataSize);
                bytes = stream.ToArray();
            }
            else
            {
                var payload = sound.CompressedFormatData?.Formats.FirstOrDefault();
                format = payload?.Key.Text ?? "";
                bytes = payload?.Value.Data ?? sound.RawData?.Data;
            }
            if (bytes is null || bytes.Length == 0) throw new InvalidDataException($"Sound {id} has no readable audio payload.");
            if (bytes.AsSpan().StartsWith("OggS"u8)) format = "OGG";
            else if (bytes.AsSpan().StartsWith("RIFF"u8)) format = "WAV";
            if (format is not ("OGG" or "WAV")) throw new NotSupportedException($"Sound {id} uses unsupported codec {format}.");
            return new SoundData(id, format, bytes, sound.GetOrDefault<int>("SampleRate"), sound.GetOrDefault<int>("NumChannels"), sound.GetOrDefault<bool>("bLooping"));
        }
    }

    public MaterialData LoadMaterial(string id)
    {
        lock (gate)
        {
            var source = Provider.LoadPackageObject<UUnrealMaterial>(id);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return ReadMaterial(source, visited) with { Id = id };
        }
    }

    private MaterialData ReadMaterial(UUnrealMaterial source, HashSet<string> visited)
    {
        var id = ObjectId(source)!;
        if (!visited.Add(id) || visited.Count > 64) throw new InvalidDataException($"Material parent cycle at {id}.");
        var result = new MaterialData { Id = id };
        if (source is UMaterialInstance instance && instance.Parent?.Load<UUnrealMaterial>() is { } parent)
            result = ReadMaterial(parent, visited) with { Id = id };
        else
        {
            var guessed = new CMaterialParams();
            source.GetParams(guessed);
            result = result with { ColorTexture = ObjectId(guessed.Diffuse?.Load()), NormalTexture = ObjectId(guessed.Normal?.Load()),
                OpacityTexture = ObjectId(guessed.Opacity?.Load()), EmissiveTexture = ObjectId(guessed.Emissive?.Load()),
                BlendMode = source.GetOrDefault("BlendMode", EBlendMode.BLEND_Opaque).ToString().Replace("BLEND_", ""),
                TwoSided = source.GetOrDefault<bool>("TwoSided"), AlphaCutoff = source.GetOrDefault("OpacityMaskClipValue", .333f) };
            var defaults = new CMaterialParams2();
            source.GetParams(defaults, EMaterialDepth.AllLayers);
            string? Texture(string name) => defaults.Textures.TryGetValue(name, out var value) ? ObjectId(value.Load()) : null;
            result = result with { ColorTexture = Texture("BaseColor") ?? result.ColorTexture, NormalTexture = Texture("Normal") ?? result.NormalTexture,
                RoughnessTexture = Texture("Roughness"), MetallicTexture = Texture("Metallic"), OpacityTexture = Texture("Opacity") ?? result.OpacityTexture,
                Roughness = defaults.Scalars.GetValueOrDefault("Roughness", result.Roughness), Metallic = defaults.Scalars.GetValueOrDefault("Metallic", result.Metallic),
                Opacity = defaults.Scalars.GetValueOrDefault("OpacityMult", result.Opacity) };
            if (defaults.Colors.TryGetValue("ColorMult", out var tint)) result = result with { Tint = new(tint.R, tint.G, tint.B, 1) };
        }
        if (source is UMaterialInstanceConstant material)
        {
            foreach (var value in material.TextureParameterValues)
            {
                var texture = ObjectId(value.ParameterValue?.Load());
                if (texture is null) continue;
                var name = value.Name.ToLowerInvariant().Replace(" ", "");
                if (name == "packedtexture") report?.Invoke(new Diagnostic(id, "PackedTexture shader channel layout is unavailable in the cooked material graph; PBR uses inherited separate maps and scalar parameters."));
                result = name switch
                {
                    "basecolor" or "diffuse" or "diffusetexture" or "albedo" or "colortexture" => result with { ColorTexture = texture },
                    "normal" or "normalmap" or "normaltexture" => result with { NormalTexture = texture },
                    "roughness" or "roughnesstexture" => result with { RoughnessTexture = texture },
                    "metallic" or "metallictexture" => result with { MetallicTexture = texture },
                    "opacity" or "opacitytexture" or "opacitymask" => result with { OpacityTexture = texture },
                    "emissive" or "emissivetexture" => result with { EmissiveTexture = texture },
                    _ => result
                };
            }
            foreach (var value in material.ScalarParameterValues)
            {
                result = value.Name.ToLowerInvariant() switch
                {
                    "roughness" => result with { Roughness = value.ParameterValue },
                    "metallic" => result with { Metallic = value.ParameterValue },
                    "opacity" or "alpha" or "opacitymult" => result with { Opacity = value.ParameterValue },
                    _ => result
                };
            }
            foreach (var value in material.VectorParameterValues)
                if (value.ParameterValue is { } color && value.Name.ToLowerInvariant() is "colormult" or "basecolor" or "diffusecolor" or "tint")
                    result = result with { Tint = new Vector4(color.R, color.G, color.B, 1) };
            // Overrides are sparse: the struct's default values must not erase inherited properties.
            if (material.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("BasePropertyOverrides") is { } overrides)
            {
                if (overrides.GetOrDefault<bool>("bOverride_BlendMode")) result = result with { BlendMode = overrides.GetOrDefault("BlendMode", EBlendMode.BLEND_Opaque).ToString().Replace("BLEND_", "") };
                if (overrides.GetOrDefault<bool>("bOverride_TwoSided")) result = result with { TwoSided = overrides.GetOrDefault<bool>("TwoSided") };
                if (overrides.GetOrDefault<bool>("bOverride_OpacityMaskClipValue")) result = result with { AlphaCutoff = overrides.GetOrDefault("OpacityMaskClipValue", .333f) };
            }
        }
        return result;
    }
}
