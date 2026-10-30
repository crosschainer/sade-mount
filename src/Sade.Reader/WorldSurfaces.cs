using System.Text;
using Sade.Contracts;
using Sade.Formats;

namespace Sade.Reader;

public sealed partial class GameReader
{
    private SurfaceAudioTable? surfaceAudio;
    private WorldPropCatalog? worldProps;

    public ModelData LoadWorldModel(string id)
    {
        lock (gate)
        {
            var source = LoadModel(id);
            worldProps ??= WorldPropCatalog.Parse(ReadOriginalData("object.dat"));
            if (worldProps.Get(source) is { } prop)
            {
                var movable = WorldPropCatalog.MovableCollision(source);
                if (!movable.IsEmpty)
                {
                    var col = FindLegacyCollision(id);
                    surfaceAudio ??= SurfaceAudioTable.Parse(ReadOriginalData("surfaud.dat"));
                    var materials = col is null ? [] : col.MaterialIds.Concat(col.Boxes.Select(b => b.MaterialId)).Concat(col.Spheres.Select(s => s.MaterialId));
                    var audio = materials.Select(material => surfaceAudio[material]).GroupBy(s => s).MaxBy(g => g.Count())?.Key ?? SurfaceAudio.Unknown;
                    return source with { Collision = movable, WorldProp = prop with { Surface = audio } };
                }
            }
            var model = BakedMeshCollision.ForStaticWorld(source);
            if (!ReferenceEquals(source, model)) report?.Invoke(new(id, model.Collision.Source));
            if (model.Collision.Indices.Length == 0 || FindLegacyCollision(id) is not { MaterialIds.Length: > 0 } legacy) return model;
            surfaceAudio ??= SurfaceAudioTable.Parse(Encoding.Latin1.GetString(Provider.SaveAsset("Gameface/Content/OriginalData/GTASA/data/surfaud.dat")));
            var collision = CollisionSurfaceTransfer.Apply(model.Collision, legacy, surfaceAudio, out var fallback, message => report?.Invoke(new(id, message)));
            if (fallback > 0) report?.Invoke(new(id, $"{fallback}/{collision.Indices.Length / 3} surface labels use the COL's dominant audio group where the source geometry could not be matched confidently."));
            return model with { Collision = collision };
        }
    }
}
