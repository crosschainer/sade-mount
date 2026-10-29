using System.Numerics;
using System.Text;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse_Conversion.Animations;
using Sade.Contracts;
using Sade.Formats;

namespace Sade.Reader;

public sealed partial class GameReader
{
    private PedMetadata? pedestrianMetadata;
    private readonly Dictionary<string, AnimationBankData> loadedBanks = new(StringComparer.OrdinalIgnoreCase);

    private void InitializePedMetadata()
    {
        string? Read(string filename)
        {
            var path = Provider.Files.Keys.FirstOrDefault(p => p.EndsWith("/" + filename, StringComparison.OrdinalIgnoreCase));
            return path is null ? null : Encoding.UTF8.GetString(Provider.SaveAsset(path));
        }
        var peds = Read("peds.ide");
        var groups = Read("animgrp.dat");
        if (peds is null || groups is null) { report?.Invoke(new Diagnostic("pedestrians", "peds.ide or animgrp.dat is missing; automatic locomotion presets are unavailable.")); return; }
        pedestrianMetadata = PedMetadata.Parse(peds, groups, Read("ped.dat") ?? "");
        for (var i = 0; i < entries.Count; i++)
            if (entries[i].Kind == AssetKind.SkeletalMesh && pedestrianMetadata.GetPreset(entries[i].Id) is { } preset)
                entries[i] = entries[i] with { AnimationGroup = preset.Group };
    }

    public PedestrianPreset? GetPedestrianPreset(string modelId)
    {
        lock (gate) return pedestrianMetadata?.GetPreset(modelId);
    }

    public AnimationBankData LoadAnimationBank(string id)
    {
        lock (gate)
        {
            if (loadedBanks.TryGetValue(id, out var bank)) return bank;
            if (!banks.TryGetValue(id, out var source)) throw new KeyNotFoundException($"Animation bank {id} is not in the installed game.");
            var bytes = source.Child is null ? Provider.SaveAsset(source.Source) : GetContainer(source.Source).Entries[source.Child];
            return loadedBanks[id] = IfpParser.Parse(id, bytes, report);
        }
    }

    public AnimationClipData[] LoadAnimations(string id, ModelData model, bool inPlace = false, string[]? clips = null)
    {
        lock (gate)
        {
            if (id.StartsWith("ifp:", StringComparison.OrdinalIgnoreCase))
            {
                var bank = LoadAnimationBank(id);
                if (clips is not null)
                {
                    var available = bank.Clips.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var missing in clips.Where(c => !available.Contains(c))) report?.Invoke(new Diagnostic(model.Id, $"Animation bank {id} has no '{missing}'; the remaining requested clips are available."));
                    clips = clips.Where(available.Contains).ToArray();
                }
                var bound = AnimationBinder.Bind(bank, model, inPlace, clips);
                if (clips is not null) report?.Invoke(new Diagnostic(model.Id, $"Bound {id} at 30 samples/s; source GTA meter translations converted to Unreal centimeters; root speed: {string.Join(", ", bound.Select(c => $"{c.Name}={c.RootSpeed:F1} cm/s"))}."));
                return bound;
            }
            if (model.Bones.Length == 0) throw new ArgumentException("Animation requires a skeletal model.", nameof(model));
            var asset = Provider.LoadPackageObject<UAnimSequence>(id);
            var set = asset.ConvertAnims();
            var skeleton = set.Skeleton;
            var skeletonId = ObjectId(skeleton);
            if (model.SkeletonId is not null && !model.SkeletonId.Equals(skeletonId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Animation {id} targets {skeletonId}, not the model skeleton {model.SkeletonId}.");
            var sourceBones = skeleton.ReferenceSkeleton.FinalRefBoneInfo;
            var boneMap = model.Bones.Select(b => Array.FindIndex(sourceBones, s => s.Name.Text.Equals(b.Name, StringComparison.OrdinalIgnoreCase))).ToArray();
            var selected = clips is null ? null : new HashSet<string>(clips, StringComparer.OrdinalIgnoreCase);
            var output = new List<AnimationClipData>();
            foreach (var sequence in set.Sequences.Where(s => selected is null || selected.Contains(s.Name)))
            {
                var count = Math.Max(1, sequence.NumFrames);
                if ((long)count * model.Bones.Length > 20_000_000) throw new InvalidDataException($"Animation {id} exceeds the pose sampling budget.");
                var frames = new TransformData[count][];
                for (var f = 0; f < count; f++)
                {
                    frames[f] = model.Bones.Select(b => b.LocalBind).ToArray();
                    for (var b = 0; b < model.Bones.Length; b++)
                    {
                        var index = boneMap[b];
                        if (index < 0 || index >= sequence.Tracks.Count) continue;
                        var bind = model.Bones[b].LocalBind;
                        var rotation = new FQuat(bind.Rotation.X, bind.Rotation.Y, bind.Rotation.Z, bind.Rotation.W);
                        var translation = new FVector(bind.Position.X, bind.Position.Y, bind.Position.Z);
                        var scale = new FVector(bind.Scale.X, bind.Scale.Y, bind.Scale.Z);
                        sequence.Tracks[index].GetBoneTransform(f, count, ref rotation, ref translation, ref scale);
                        var q = Quat(rotation);
                        if (f > 0 && Quaternion.Dot(frames[f - 1][b].Rotation, q) < 0) q = -q;
                        frames[f][b] = new TransformData(Vec(translation), q, Vec(scale));
                    }
                }
                var duration = asset.SequenceLength;
                var rate = count > 1 && duration > 0 ? (count - 1) / duration : 30;
                var root = Array.FindIndex(model.Bones, b => b.Parent < 0);
                float speed = 0;
                if (root >= 0 && frames.Length > 1)
                {
                    var start = frames[0][root].Position;
                    var end = frames[^1][root].Position;
                    var delta = new Vector3(end.X - start.X, end.Y - start.Y, 0);
                    speed = duration > 0 ? delta.Length() / duration : 0;
                    if (inPlace) for (var f = 0; f < count; f++) frames[f][root] = frames[f][root] with { Position = new Vector3(start.X, start.Y, frames[f][root].Position.Z) };
                }
                // The upstream converter already accumulates additive sequences onto their reference pose.
                output.Add(new AnimationClipData(sequence.Name, rate, frames, Additive: false, RootSpeed: speed));
            }
            return output.ToArray();
        }
    }
}
