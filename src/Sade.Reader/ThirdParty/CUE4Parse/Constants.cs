// Source: FabianFG/CUE4Parse, commit 1e3293d4b3ecaf6ef3d73641b9ffe47fa4cf1562
// Apache-2.0. See LICENSE and ORIGIN.md in this folder.
// Modified: preserve upstream nullable warning context when compiled in this nullable-enabled project.
#nullable disable warnings
namespace CUE4Parse_Conversion;

public class Constants
{
    public const int MAX_MESHBONES = 1024 * 3;
    public const int NUM_INFLUENCES_UE4 = 4;

    public const int PSK_VERSION = 20220723;
    public const int PSA_VERSION = 20100422;

    public const int MAX_MESH_UV_SETS = 8;
    public const int MESH_HASH_SIZE = 16384;

    public const int MAX_ANIM_LINEAR_KEYS = 4;
    public const int ANIM_INFO_SIZE = 2 * 64 + 10 * 4;
    public const int VJointPosPsk_SIZE = 4 * 4 + 3 * 4 + 4 + 3 * 4;
    public const int FNamedBoneBinary_SIZE = 64 + 3 * 4 + VJointPosPsk_SIZE;
    public const int VQuatAnimKey_SIZE = 3 * 4 + 4 * 4 + 4;
    public const int VScaleAnimKey_SIZE = 3 * 4 + 4;
    public const int VMorphData_SIZE = 3 * 4 + 3 * 4 + 4;
    public const int VSocket_SIZE = 64 + 64 + 3 * 4 + 3 * 4 + 3 * 4;

    public const int DXT_BITS_PER_PIXEL = 4;

    public const float Byte_Bone_Scale = 1.0f / byte.MaxValue;
    public const float UShort_Bone_Scale = 1.0f / ushort.MaxValue;
}

