using Sade.Contracts;
using Sade.Mount;
using Sade.Reader;

static TextureMip Mip(int w, int h, int size, byte value) => new(w, h, Enumerable.Repeat(value, size).ToArray());
static TextureData Texture(string format, params TextureMip[] mips) => new("synthetic", format, false, mips);
static void Equal(byte[] expected, byte[] actual)
{
    if (!actual.AsSpan().SequenceEqual(expected)) throw new Exception("Mip upload bytes differ from the expected smallest-first order.");
}
static void Reject(TextureData texture)
{
    try { TextureMipData.Pack(texture); }
    catch (InvalidDataException) { return; }
    throw new Exception("Accepted invalid mip chain.");
}

// Distinct authored mip colors catch the corruption even when the total byte count is correct.
Equal([3, 3, 3, 3, .. Enumerable.Repeat((byte)2, 16), .. Enumerable.Repeat((byte)1, 64)],
    TextureMipData.Pack(Texture("RGBA8", Mip(4, 4, 64, 1), Mip(2, 2, 16, 2), Mip(1, 1, 4, 3))));
Equal([.. Enumerable.Repeat((byte)4, 8), .. Enumerable.Repeat((byte)3, 8), .. Enumerable.Repeat((byte)2, 8), .. Enumerable.Repeat((byte)1, 16)],
    TextureMipData.Pack(Texture("BC1", Mip(8, 4, 16, 1), Mip(4, 2, 8, 2), Mip(2, 1, 8, 3), Mip(1, 1, 8, 4))));
Equal([2, 2, 2, .. Enumerable.Repeat((byte)1, 21)], TextureMipData.Pack(Texture("R8", Mip(3, 7, 21, 1), Mip(1, 3, 3, 2))));
Equal([.. Enumerable.Repeat((byte)3, 8), .. Enumerable.Repeat((byte)2, 8), .. Enumerable.Repeat((byte)1, 8)],
    TextureMipData.Pack(Texture("BC1", Mip(4, 4, 8, 1), Mip(4, 4, 8, 2), Mip(4, 4, 8, 3))));
foreach (var format in new[] { "BC2", "BC3", "BC5", "BC6H", "BC7" })
    Equal(Enumerable.Repeat((byte)5, 16).ToArray(), TextureMipData.Pack(Texture(format, Mip(1, 1, 16, 5))));
Equal(Enumerable.Repeat((byte)6, 8).ToArray(), TextureMipData.Pack(Texture("RGBA16F", Mip(1, 1, 8, 6))));
Reject(Texture("BC1"));
Reject(Texture("BC1", Mip(8, 8, 32, 0), Mip(2, 2, 8, 0)));
Reject(Texture("BC1", Mip(4, 4, 7, 0)));
Reject(Texture("RGBA8", Mip(1, 1, 4, 0), Mip(1, 1, 4, 0)));
Reject(Texture("R8", Mip(0, 1, 0, 0)));
Console.WriteLine("PASS: authored mip ordering, block-compressed tails, rectangular sizes and malformed-chain rejection.");

if (args.Contains("--installed"))
{
    using var reader = new GameReader();
    var game = Environment.GetEnvironmentVariable("SADE_GAME_DIR") ?? @"C:\Program Files (x86)\Steam\steamapps\common\GTA San Andreas - The Definitive Edition";
    reader.Initialize(new(game, Path.Combine(Path.GetTempPath(), "sade-reader-smoke")));
    var textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var modelId in new[] { "/Game/SanAndreas/Characters/Peds/SK_smoke", "/Game/SanAndreas/Characters/Peds/SK_ballas1", "/Game/SanAndreas/Characters/Peds/SK_fam1" })
    {
        var model = reader.LoadModel(modelId);
        foreach (var part in model.Parts.Where(p => p.Lod == 0))
        {
            Console.WriteLine($"SECTION {modelId} {part.Name}: triangles={part.Indices.Length / 3}, UV=({part.Vertices.Min(v => v.UV.X)},{part.Vertices.Min(v => v.UV.Y)})..({part.Vertices.Max(v => v.UV.X)},{part.Vertices.Max(v => v.UV.Y)}), material={part.MaterialId}");
            if (part.MaterialId is null) continue;
            var material = reader.LoadMaterial(part.MaterialId);
            foreach (var id in new[] { material.ColorTexture, material.NormalTexture, material.RoughnessTexture, material.MetallicTexture, material.OpacityTexture, material.EmissiveTexture }.OfType<string>())
            {
                if (!textures.Add(id)) continue;
                var texture = reader.LoadTexture(id);
                var packed = TextureMipData.Pack(texture);
                Console.WriteLine($"TEXTURE {id}: {texture.Format} {texture.Mips[0].Width}x{texture.Mips[0].Height}, {texture.Mips.Length} valid mips, {packed.Length} bytes");
            }
        }
    }
    Console.WriteLine($"PASS: {textures.Count} real installed character texture chains, including color/normal maps and tiny shared textures.");
}
