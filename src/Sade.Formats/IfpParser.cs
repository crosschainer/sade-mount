using System.Numerics;
using Sade.Contracts;

namespace Sade.Formats;

/// <summary>Reads RenderWare ANP3/ANP2 and chunked ANPK animation banks without engine dependencies.</summary>
public static class IfpParser
{
    public static AnimationBankData Parse(string id, ReadOnlySpan<byte> bytes, Action<Diagnostic>? report = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var input = new BinaryCursor(bytes);
        var magic = input.Text(4);
        var length = input.UInt32();
        if (length > int.MaxValue || length > input.Remaining) throw new InvalidDataException("IFP bank size exceeds its payload.");
        var bank = new BinaryCursor(input.Take((int)length));
        var clips = magic switch
        {
            "ANP3" => ReadPacked(ref bank, true),
            "ANP2" => ReadPacked(ref bank, false),
            "ANPK" => ReadChunked(ref bank, id, report),
            _ => throw new NotSupportedException($"IFP format '{magic}' is not supported.")
        };
        bank.RequireZeroTail();
        input.RequireZeroTail(); // IMG entries may contain zero sector padding.
        if (clips.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != clips.Length)
            throw new InvalidDataException("IFP bank contains duplicate clip names.");
        return new AnimationBankData(id, clips);
    }

    private static RawAnimationClip[] ReadPacked(ref BinaryCursor reader, bool anp3)
    {
        reader.Text(24);
        var clipCount = reader.Count(Math.Min(100_000, reader.Remaining / (anp3 ? 36 : 28)), "clip");
        var clips = new RawAnimationClip[clipCount];
        for (var c = 0; c < clips.Length; c++)
        {
            var name = RequireName(reader.Text(24), "clip");
            var trackCount = reader.Count(Math.Min(4096, reader.Remaining / 36), "track");
            uint declaredFrameBytes = 0;
            if (anp3)
            {
                declaredFrameBytes = reader.UInt32();
                reader.UInt32(); // Compression/allocation flags. Frame type describes each track.
            }
            long frameBytes = 0;
            var tracks = new AnimationTrack[trackCount];
            for (var t = 0; t < tracks.Length; t++)
            {
                var trackName = RequireName(reader.Text(24), "track");
                var kind = reader.Int32();
                var stride = kind switch { 1 => 20, 2 => 32, 3 => 10, 4 => 16, _ => throw new InvalidDataException($"Unknown IFP frame type {kind}.") };
                var count = reader.Count(Math.Min(1_000_000, reader.Remaining / stride), "key");
                var tag = reader.Int32();
                var hasPosition = kind is 2 or 4;
                var compressed = kind is 3 or 4;
                var keys = new AnimationKey[count];
                frameBytes += (long)count * stride;
                for (var k = 0; k < count; k++)
                {
                    var rotation = compressed
                        ? Normalize(new Quaternion(reader.Int16() / 4096f, reader.Int16() / 4096f, reader.Int16() / 4096f, reader.Int16() / 4096f))
                        : ReadQuaternion(ref reader, false);
                    // Native KeyFrameCompressed uses 60 ticks/second, not the old Unity importer's 64.
                    var time = compressed ? reader.UInt16() / 60f : reader.Single();
                    Vector3? position = hasPosition
                        ? compressed ? new Vector3(reader.Int16(), reader.Int16(), reader.Int16()) / 1024f : ReadVector(ref reader)
                        : null;
                    keys[k] = new AnimationKey(time, rotation, position);
                }
                ValidateKeys(keys, name, trackName);
                tracks[t] = new AnimationTrack(trackName, tag < 0 ? null : tag, keys);
            }
            if (anp3 && frameBytes != declaredFrameBytes)
                throw new InvalidDataException($"Clip '{name}' declares {declaredFrameBytes} key bytes but contains {frameBytes}.");
            clips[c] = MakeClip(name, tracks);
        }
        return clips;
    }

    private static RawAnimationClip[] ReadChunked(ref BinaryCursor reader, string id, Action<Diagnostic>? report)
    {
        var info = reader.Chunk("INFO");
        var count = info.Count(Math.Min(100_000, reader.Remaining / 16), "clip");
        info.Text(info.Remaining); // Bank name.
        var clips = new RawAnimationClip[count];
        for (var c = 0; c < count; c++)
        {
            var nameChunk = reader.Chunk("NAME");
            var name = RequireName(nameChunk.Text(nameChunk.Remaining), "clip");
            var animation = reader.Chunk("DGAN");
            var animationInfo = animation.Chunk("INFO");
            var tracksCount = animationInfo.Count(Math.Min(4096, animation.Remaining / 16), "track");
            var tracks = new AnimationTrack[tracksCount];
            for (var t = 0; t < tracks.Length; t++)
            {
                var objectData = animation.Chunk("CPAN");
                var objectInfo = objectData.Chunk("ANIM");
                if (objectInfo.Data.Length is not (40 or 44)) throw new InvalidDataException($"Unexpected ANPK ANIM size {objectInfo.Data.Length}.");
                var trackName = RequireName(objectInfo.Text(28), "track");
                var frames = objectInfo.Count(Math.Min(1_000_000, objectData.Remaining / 20), "key");
                objectInfo.Skip(8); // Sibling links.
                int? tag = objectInfo.Remaining >= 4 ? objectInfo.Int32() : null;
                if (tag < 0) tag = null;
                var keys = new AnimationKey[frames];
                var invalidLeadingPose = false;
                if (frames > 0)
                {
                    if (objectData.Remaining < 8) throw new InvalidDataException("Missing ANPK key chunk.");
                    var format = System.Text.Encoding.ASCII.GetString(objectData.Data.Slice(objectData.Position, 4));
                    var hasPosition = format is "KRT0" or "KRTS";
                    var hasScale = format == "KRTS";
                    if (format is not ("KR00" or "KRT0" or "KRTS")) throw new InvalidDataException($"Unknown ANPK key format '{format}'.");
                    var keyData = objectData.Chunk(format);
                    var stride = hasScale ? 44 : hasPosition ? 32 : 20;
                    if ((long)frames * stride != keyData.Data.Length) throw new InvalidDataException("ANPK key count does not match chunk size.");
                    for (var k = 0; k < frames; k++)
                    {
                        var rawRotation = new Quaternion(keyData.RawSingle(), keyData.RawSingle(), keyData.RawSingle(), keyData.RawSingle());
                        Vector3? position = hasPosition ? ReadRawVector(ref keyData) : null;
                        Vector3? scale = hasScale ? ReadRawVector(ref keyData) : null;
                        var time = keyData.Single();
                        var rotationLength = rawRotation.LengthSquared();
                        var invalidPose = !float.IsFinite(rotationLength) || rotationLength < 1e-12f || rotationLength > 4 || !IsFinite(position) || !IsFinite(scale);
                        if (invalidPose && k == 0 && frames > 1)
                        {
                            // Inspect the following key before deciding whether this is a shipped
                            // cutscene guard-frame anomaly. Never normalize the invalid quaternion.
                            invalidLeadingPose = true;
                            keys[k] = new AnimationKey(time, Quaternion.Identity, null);
                        }
                        else if (invalidPose && k > 0 && k == frames - 1 && !(invalidLeadingPose && k == 1))
                        {
                            // Shipped prolog1.ifp contains uninitialized terminal poses. Keep the last valid pose,
                            // including translation, instead of sending nonsensical values into skinning.
                            keys[k] = keys[k - 1] with { Time = time };
                            report?.Invoke(new Diagnostic(id, $"ANPK '{name}'/'{trackName}': held the previous pose for an invalid terminal pose."));
                        }
                        else if (invalidPose) throw new InvalidDataException($"ANPK '{name}'/'{trackName}': invalid pose at key {k}.");
                        else keys[k] = new AnimationKey(time, Quaternion.Conjugate(Normalize(rawRotation)), position, scale);
                    }
                    if (invalidLeadingPose)
                    {
                        // crash3a and synd_7 contain an uninitialized first pose precisely 20 ms
                        // before a valid key. Hold that neighbor only within the observed guard interval.
                        var interval = keys[1].Time - keys[0].Time;
                        if (interval < 0 || interval > 0.0201f)
                            throw new InvalidDataException($"ANPK '{name}'/'{trackName}': invalid leading pose outside the 20 ms guard interval.");
                        keys[0] = keys[1] with { Time = keys[0].Time };
                        report?.Invoke(new Diagnostic(id, $"ANPK '{name}'/'{trackName}': held the next pose for an invalid leading guard pose."));
                    }
                }
                objectData.RequireZeroTail();
                if (keys.Zip(keys.Skip(1), (a, b) => b.Time < a.Time).Any(backward => backward))
                {
                    // Real cutscene banks contain out-of-order float timestamps; sort whole keys, preserving
                    // authored poses and the order of equal timestamps, before binary-search sampling.
                    keys = keys.OrderBy(key => key.Time).ToArray();
                    report?.Invoke(new Diagnostic(id, $"ANPK '{name}'/'{trackName}': ordered source keys by timestamp."));
                }
                if (keys.Length == 2 && keys[0].Time < 0 && keys[1].Time >= 0 && (keys[0] with { Time = keys[1].Time }) == keys[1])
                {
                    // Some cutscene exports retain a negative pre-roll key for constant tracks.
                    // Identical poses prove trimming to zero cannot change the sampled animation.
                    keys[0] = keys[0] with { Time = 0 };
                    report?.Invoke(new Diagnostic(id, $"ANPK '{name}'/'{trackName}': trimmed constant pre-roll to time zero."));
                }
                ValidateKeys(keys, name, trackName);
                tracks[t] = new AnimationTrack(trackName, tag, keys);
            }
            animation.RequireZeroTail();
            clips[c] = MakeClip(name, tracks);
        }
        return clips;
    }

    private static RawAnimationClip MakeClip(string name, AnimationTrack[] tracks) => new(name, tracks,
        tracks.SelectMany(t => t.Keys).Select(k => k.Time).DefaultIfEmpty().Max());

    private static void ValidateKeys(AnimationKey[] keys, string clip, string track)
    {
        var previousTime = -1f;
        for (var i = 0; i < keys.Length; i++)
        {
            if (keys[i].Time < 0 || keys[i].Time < previousTime) throw new InvalidDataException($"Nonmonotonic time in '{clip}' track '{track}'.");
            previousTime = keys[i].Time;
            if (i > 0 && Quaternion.Dot(keys[i - 1].Rotation, keys[i].Rotation) < 0)
                keys[i] = keys[i] with { Rotation = Quaternion.Negate(keys[i].Rotation) };
        }
    }

    private static string RequireName(string name, string kind) => !string.IsNullOrWhiteSpace(name) ? name : throw new InvalidDataException($"Empty IFP {kind} name.");
    internal static Vector3 ReadVector(ref BinaryCursor reader) => new(reader.Single(), reader.Single(), reader.Single());
    private static Vector3 ReadRawVector(ref BinaryCursor reader) => new(reader.RawSingle(), reader.RawSingle(), reader.RawSingle());
    private static bool IsFinite(Vector3? v) => v is not { } value || (float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z));
    private static Quaternion ReadQuaternion(ref BinaryCursor reader, bool conjugate)
    {
        var q = Normalize(new Quaternion(reader.Single(), reader.Single(), reader.Single(), reader.Single()));
        return conjugate ? Quaternion.Conjugate(q) : q;
    }
    internal static Quaternion Normalize(Quaternion rotation)
    {
        var length = rotation.LengthSquared();
        if (!float.IsFinite(length) || length < 1e-12f) throw new InvalidDataException("Invalid zero or nonfinite animation quaternion.");
        return Quaternion.Normalize(rotation);
    }
}
