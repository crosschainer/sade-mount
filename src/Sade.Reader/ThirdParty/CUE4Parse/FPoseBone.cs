// Source: FabianFG/CUE4Parse, commit 1e3293d4b3ecaf6ef3d73641b9ffe47fa4cf1562
// Apache-2.0. See LICENSE and ORIGIN.md in this folder.
// Modified: preserve upstream nullable warning context when compiled in this nullable-enabled project.
#nullable disable warnings
using System.Diagnostics;
using CUE4Parse.UE4.Objects.Core.Math;

namespace CUE4Parse_Conversion.Writers.ActorX.Structs.Animations
{
    public class FPoseBone : ICloneable
    {
        public FTransform Transform;
        public int ParentIndex;
        public string Name;
        public bool IsValidKey;
        public bool Accumulated;

        public FPoseBone()
        {
            Transform = FTransform.Identity;
            ParentIndex = -1;
            Name = "";
            IsValidKey = false;
            Accumulated = false;
        }

        public void AccumulateWithAdditiveScale(FTransform atom, float weight)
        {
            Debug.Assert(!Accumulated);
            Transform.Rotation = atom.Rotation * weight * Transform.Rotation;
            Transform.Translation += atom.Translation * weight;
            Transform.Scale3D *= FVector.OneVector + atom.Scale3D * weight;
            Accumulated = true;
        }

        public object Clone()
        {
            return new FPoseBone
            {
                Transform = (FTransform) Transform.Clone(),
                ParentIndex = ParentIndex,
                Name = Name,
                IsValidKey = IsValidKey,
                Accumulated = Accumulated
            };
        }
    }
}

