using System.Numerics;
using Sade.Contracts;

namespace Sade.Formats;

public static class AnimationBinder
{
    public const float SampleRate = 30;
    private static readonly Dictionary<int, string> PedBoneNames = new()
    {
        [0] = "Root", [1] = "Pelvis", [2] = "Spine", [3] = "Spine1", [4] = "Neck", [5] = "Head",
        [6] = "L_Brow", [7] = "R_Brow", [8] = "Jaw", [21] = "Bip01_R_Clavicle", [22] = "R_UpperArm",
        [23] = "R_ForeArm", [24] = "R_Hand", [25] = "R_Finger", [26] = "R_Finger01",
        [31] = "Bip01_L_Clavicle", [32] = "L_UpperArm", [33] = "L_ForeArm", [34] = "L_Hand",
        [35] = "L_Finger", [36] = "L_Finger01", [41] = "L_Thigh", [42] = "L_Calf", [43] = "L_Foot",
        [44] = "L_Toe0", [51] = "R_Thigh", [52] = "R_Calf", [53] = "R_Foot", [54] = "R_Toe0",
        [201] = "Belly", [301] = "R_breast", [302] = "L_breast"
    };

    /// <summary>Samples local skeletal poses. Missing channels retain the target reference pose.</summary>
    public static AnimationClipData[] Bind(AnimationBankData bank, ModelData model, bool inPlace = false, string[]? clips = null)
    {
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentNullException.ThrowIfNull(model);
        if (model.Bones.Length == 0) throw new ArgumentException("Animation binding requires a skeletal model.", nameof(model));
        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < model.Bones.Length; i++)
        {
            var bone = model.Bones[i];
            if (bone.Parent < -1 || bone.Parent >= i) throw new InvalidDataException($"Invalid parent for bone '{bone.Name}'.");
            if (!names.TryAdd(NormalizeBoneName(bone.Name), i)) throw new InvalidDataException($"Ambiguous skeleton bone name '{bone.Name}'.");
        }
        var selected = clips is null ? null : new HashSet<string>(clips, StringComparer.OrdinalIgnoreCase);
        if (selected is not null)
        {
            var available = bank.Clips.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = selected.Where(c => !available.Contains(c)).ToArray();
            if (missing.Length > 0) throw new KeyNotFoundException($"Animation bank '{bank.Id}' has no clips: {string.Join(", ", missing)}.");
        }
        return bank.Clips.Where(c => selected is null || selected.Contains(c.Name)).Select(clip => BindClip(clip, model, names, inPlace)).ToArray();
    }

    public static string NormalizeBoneName(string name) => string.Join('_', name.Trim().Split([' ', '\t', '_'], StringSplitOptions.RemoveEmptyEntries));

    private static AnimationClipData BindClip(RawAnimationClip clip, ModelData model, Dictionary<string, int> names, bool inPlace)
    {
        if (!float.IsFinite(clip.Duration) || clip.Duration < 0 || clip.Duration > 3600) throw new InvalidDataException($"Invalid duration for '{clip.Name}'.");
        var assignments = new Dictionary<int, AnimationTrack>();
        foreach (var track in clip.Tracks)
        {
            if (track.Keys.Length == 0) continue;
            var canonical = NormalizeBoneName(track.Name);
            var rootAlias = track.BoneTag == 0 || canonical.Equals("Normal", StringComparison.OrdinalIgnoreCase) || canonical.Equals("Root", StringComparison.OrdinalIgnoreCase);
            int boneIndex;
            if (rootAlias)
            {
                if (!names.TryGetValue("Root", out boneIndex)) boneIndex = Array.FindIndex(model.Bones, b => b.Parent == -1);
            }
            else if (!names.TryGetValue(canonical, out boneIndex))
            {
                if (track.BoneTag is not { } tag || !PedBoneNames.TryGetValue(tag, out var alias) || !names.TryGetValue(alias, out boneIndex)) continue;
            }
            if (!assignments.TryAdd(boneIndex, track)) throw new InvalidDataException($"Clip '{clip.Name}' maps multiple tracks to '{model.Bones[boneIndex].Name}'.");
        }
        if (assignments.Count == 0) throw new InvalidDataException($"Clip '{clip.Name}' has no bones compatible with '{model.Id}'.");

        var intervals = clip.Duration == 0 ? 0 : Math.Max(1, (int)MathF.Ceiling(clip.Duration * SampleRate));
        // Use an exact duration: uniformly spaced samples include both source endpoints.
        var rate = clip.Duration > 0 ? intervals / clip.Duration : SampleRate;
        if ((long)(intervals + 1) * model.Bones.Length > 20_000_000) throw new InvalidDataException("Animation exceeds the pose sampling budget.");
        var frames = new TransformData[intervals + 1][];
        var rootIndex = Array.FindIndex(model.Bones, b => b.Parent == -1);
        // Classic pedestrian IFPs use +Y forward and carry a quarter-turn in their root pose.
        // DE's pedestrian reference meshes face +X. Convert the entire root trajectory together,
        // retaining heading changes inside the animation rather than flattening every root yaw.
        var rootBasis = names.ContainsKey("Root") && names.ContainsKey("Pelvis")
            ? Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2) : Quaternion.Identity;
        var rootSpeed = 0f;
        Vector3? rootStart = null;
        if (assignments.TryGetValue(rootIndex, out var rootTrack))
        {
            var first = Sample(rootTrack.Keys, 0);
            var last = Sample(rootTrack.Keys, clip.Duration);
            if (first.Position is { } from && last.Position is { } to)
            {
                rootStart = Vector3.Transform(GtaCoordinates.ToUnreal(from), rootBasis);
                var delta = GtaCoordinates.ToUnreal(to - from);
                rootSpeed = clip.Duration > 0 ? new Vector2(delta.X, delta.Y).Length() / clip.Duration : 0;
            }
        }
        for (var f = 0; f <= intervals; f++)
        {
            var pose = frames[f] = model.Bones.Select(b => b.LocalBind).ToArray();
            var time = f == intervals ? clip.Duration : f / rate;
            foreach (var (boneIndex, track) in assignments)
            {
                var key = Sample(track.Keys, time);
                var bind = model.Bones[boneIndex].LocalBind;
                var rotation = GtaCoordinates.ToUnreal(key.Rotation);
                var translation = key.Position is { } source ? GtaCoordinates.ToUnreal(source) : Vector3.Zero;
                if (boneIndex == rootIndex)
                {
                    rotation = Quaternion.Normalize(rootBasis * rotation);
                    translation = Vector3.Transform(translation, rootBasis);
                }
                var position = bind.Position + translation;
                if (inPlace && boneIndex == rootIndex && rootStart is { } start)
                    position = new Vector3(bind.Position.X + start.X, bind.Position.Y + start.Y, position.Z);
                if (f > 0 && Quaternion.Dot(frames[f - 1][boneIndex].Rotation, rotation) < 0) rotation = Quaternion.Negate(rotation);
                pose[boneIndex] = new TransformData(position, rotation, key.Scale ?? bind.Scale);
            }
        }
        return new AnimationClipData(clip.Name, rate, frames, RootSpeed: rootSpeed);
    }

    internal static AnimationKey Sample(AnimationKey[] keys, float time)
    {
        if (time <= keys[0].Time) return keys[0];
        if (time >= keys[^1].Time) return keys[^1];
        var low = 0;
        var high = keys.Length - 1;
        while (high - low > 1)
        {
            var mid = low + (high - low) / 2;
            if (keys[mid].Time <= time) low = mid; else high = mid;
        }
        var a = keys[low];
        var b = keys[high];
        var t = (time - a.Time) / (b.Time - a.Time);
        var q = Quaternion.Slerp(a.Rotation, b.Rotation, t);
        Vector3? p = a.Position is { } p0 && b.Position is { } p1 ? Vector3.Lerp(p0, p1, t) : a.Position ?? b.Position;
        Vector3? s = a.Scale is { } s0 && b.Scale is { } s1 ? Vector3.Lerp(s0, s1, t) : a.Scale ?? b.Scale;
        return new AnimationKey(time, Quaternion.Normalize(q), p, s);
    }
}
