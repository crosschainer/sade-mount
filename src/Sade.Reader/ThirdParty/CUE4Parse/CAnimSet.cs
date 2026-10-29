// Source: FabianFG/CUE4Parse, commit 1e3293d4b3ecaf6ef3d73641b9ffe47fa4cf1562
// Apache-2.0. See LICENSE and ORIGIN.md in this folder.
// Modified: preserve upstream nullable warning context when compiled in this nullable-enabled project.
#nullable disable warnings
using System.Collections.Generic;
using CUE4Parse.UE4.Assets.Exports.Animation;

namespace CUE4Parse_Conversion.Writers.ActorX.Structs.Animations;

/// <summary>
/// TODO: refactor
/// </summary>
public class CAnimSet
{
    public readonly USkeleton Skeleton;
    public readonly List<CAnimSequence> Sequences = [];

    public float TotalAnimTime;

    public CAnimSet(USkeleton skeleton)
    {
        Skeleton = skeleton;
    }
}

