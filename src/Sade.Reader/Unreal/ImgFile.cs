using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Readers;

namespace Sade.Reader.Unreal;

/// <summary>GTA's cooked IMGFile stores native IMGFileEntry structs, rather than tagged UObject properties.</summary>
public sealed class UIMGFile : UObject
{
    public Dictionary<string, byte[]> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);

    public override void Deserialize(FAssetArchive archive, long validPos)
    {
        while (archive.Position < validPos)
        {
            var start = archive.Position;
            var tag = new FPropertyTag(archive, false);
            if (tag.Name.IsNone) break;
            var end = checked(archive.Position + tag.Size);
            if (tag.Size < 0 || end > validPos) throw new InvalidDataException("IMGFile property extends outside its export.");
            if (tag.Name.Text == "allEntries")
            {
                var count = archive.Read<int>();
                if (count is < 0 or > 100_000) throw new InvalidDataException("Invalid IMGFile entry count.");
                if (count > 0)
                {
                    var inner = new FPropertyTag(archive, false);
                    if (inner.TagData?.StructType != "IMGFileEntry")
                        throw new InvalidDataException($"Unexpected IMGFile entry type: {inner.TagData?.StructType}");
                    for (var i = 0; i < count; i++)
                    {
                        var name = archive.ReadFString();
                        var length = archive.Read<int>();
                        if (length < 0 || length > end - archive.Position - 4)
                            throw new InvalidDataException($"Invalid IMGFile payload length for '{name}'.");
                        Entries[name] = archive.ReadBytes(length);
                        _ = archive.Read<uint>(); // Native entry alignment field, observed as 32 in the installed PC edition.
                    }
                }
            }
            else
            {
                archive.Position = start;
                Properties.Add(new FPropertyTag(archive, true));
            }
            archive.Position = end;
        }
        archive.Position = validPos;
    }
}
