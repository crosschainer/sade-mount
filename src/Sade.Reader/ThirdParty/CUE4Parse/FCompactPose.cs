// Source: FabianFG/CUE4Parse, commit 1e3293d4b3ecaf6ef3d73641b9ffe47fa4cf1562
// Apache-2.0. See LICENSE and ORIGIN.md in this folder.
// Modified: preserve upstream nullable warning context when compiled in this nullable-enabled project.
#nullable disable warnings
using System.Diagnostics;
using CUE4Parse.UE4.Objects.Core.Math;

namespace CUE4Parse_Conversion.Writers.ActorX.Structs.Animations
{
    public class FCompactPose : ICloneable
    {
        public readonly FPoseBone[] Bones;

        public FCompactPose(int boneLength)
        {
            Bones = new FPoseBone[boneLength];
        }

        public void NormalizeRotations()
        {
            foreach (var bone in Bones)
                bone.Transform.Rotation.Normalize();
        }

        public void PushTransformAtFrame(List<CAnimTrack> dstTracks, int frame)
        {
            Debug.Assert(dstTracks.Count == Bones.Length);

            for (int index = 0; index < Bones.Length; ++index)
            {
                if (!Bones[index].IsValidKey) continue;

                FTransform transform = Bones[index].Transform;
                dstTracks[index].KeyQuat[frame] = transform.Rotation;
                dstTracks[index].KeyPos[frame] = transform.Translation;
                dstTracks[index].KeyScale[frame] = transform.Scale3D;
            }
        }

        public object Clone()
        {
            var pose = new FCompactPose(Bones.Length);
            for (int i = 0; i < pose.Bones.Length; i++)
            {
                pose.Bones[i] = (FPoseBone)Bones[i].Clone();
            }
            return pose;
        }
    }
}

