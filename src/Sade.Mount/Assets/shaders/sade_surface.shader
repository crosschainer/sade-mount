HEADER
{
    Description = "GTA San Andreas Definitive Edition mounted materials";
}
FEATURES
{
    #include "common/features.hlsl"
    Feature(F_ALPHA_TEST, 0..1, "Transparency");
    Feature(F_TRANSLUCENT, 0..1, "Transparency");
    FeatureRule(Requires1(F_ADDITIVE_BLEND, F_TRANSLUCENT), "Additive requires translucency");
}
MODES
{
    Forward();
    Depth(S_MODE_DEPTH);
}
COMMON
{
    StaticCombo(S_RENDER_BACKFACES, F_RENDER_BACKFACES, Sys(ALL));
    #include "common/shared.hlsl"
}
struct VertexInput
{
    #include "common/vertexinput.hlsl"
    float4 vSadeColor : COLOR0 < Semantic(Color); >;
};
struct PixelInput
{
    #include "common/pixelinput.hlsl"
    float4 vSadeColor : COLOR0;
};
VS
{
    #include "common/vertex.hlsl"
    PixelInput MainVs(VertexInput v)
    {
        PixelInput i = ProcessVertex(v);
        i.vSadeColor = v.vSadeColor;
        return FinalizeVertex(i);
    }
}
PS
{
    StaticCombo(S_ALPHA_TEST, F_ALPHA_TEST, Sys(ALL));
    StaticCombo(S_TRANSLUCENT, F_TRANSLUCENT, Sys(ALL));
    StaticCombo(S_ADDITIVE_BLEND, F_ADDITIVE_BLEND, Sys(ALL));
    CreateInputTexture2D(TextureColor, Srgb, 8, "", "_color", "Material", Default4(1,1,1,1));
    CreateInputTexture2D(TextureNormal, Linear, 8, "", "_normal", "Material", Default4(0.5,0.5,1,1));
    CreateInputTexture2D(TextureRoughness, Linear, 8, "", "_roughness", "Material", Default(1));
    CreateInputTexture2D(TextureMetalness, Linear, 8, "", "_metalness", "Material", Default(1));
    CreateInputTexture2D(TexturePacked, Linear, 8, "", "_packed", "Material", Default3(1,1,1));
    CreateInputTexture2D(TextureOpacity, Linear, 8, "", "_opacity", "Material", Default(1));
    CreateInputTexture2D(TextureEmissive, Srgb, 8, "", "_emissive", "Material", Default3(0,0,0));
    Texture2D g_tColor < Channel(RGBA, Box(TextureColor), Srgb); OutputFormat(BC7); SrgbRead(true); >;
    Texture2D g_tNormal < Channel(RGBA, Box(TextureNormal), Linear); OutputFormat(BC7); SrgbRead(false); >;
    Texture2D g_tRoughness < Channel(R, Box(TextureRoughness), Linear); OutputFormat(BC7); SrgbRead(false); >;
    Texture2D g_tMetalness < Channel(R, Box(TextureMetalness), Linear); OutputFormat(BC7); SrgbRead(false); >;
    Texture2D g_tPacked < Channel(RGB, Box(TexturePacked), Linear); OutputFormat(BC7); SrgbRead(false); >;
    Texture2D g_tOpacity < Channel(R, Box(TextureOpacity), Linear); OutputFormat(BC7); SrgbRead(false); >;
    Texture2D g_tEmissive < Channel(RGB, Box(TextureEmissive), Srgb); OutputFormat(BC7); SrgbRead(true); >;
    SamplerState TextureFiltering < Filter(ANISOTROPIC); MaxAniso(8); AddressU(WRAP); AddressV(WRAP); >;
    float4 g_vTint < Default4(1,1,1,1); >;
    float g_flRoughness < Default(0.7); >;
    float g_flMetalness < Default(0); >;
    float g_flPackedPbr < Default(0); >;
    float g_flOpacity < Default(1); >;
    #define MATERIAL_ALPHA_TEXTURE g_tColor
    TextureAttribute(RepresentativeTexture, g_tColor);
    TextureAttribute(LightSim_DiffuseAlbedoTexture, g_tColor);
    BoolAttribute(renderbackfaces, F_RENDER_BACKFACES ? true : false);
    #include "common/pixel.hlsl"
    float4 MainPs(PixelInput i) : SV_Target0
    {
        Material m = Material::Init(i);
        float2 uv = i.vTextureCoords.xy;
        float4 color = g_tColor.Sample(TextureFiltering, uv);
        float2 normalXY = g_tNormal.Sample(TextureFiltering, uv).rg * 2.0 - 1.0;
        float3 normalTs = float3(normalXY, sqrt(saturate(1.0 - dot(normalXY, normalXY))));
        // DE packs vertex AO/masks in color channels; character G/B are zero, not a color tint.
        m.Albedo = color.rgb * g_vTint.rgb * i.vVertexColor.rgb;
        m.AmbientOcclusion = saturate(i.vSadeColor.r);
        m.Opacity = color.a * g_vTint.a * g_flOpacity * g_tOpacity.Sample(TextureFiltering, uv).r;
        // Imported Unreal tangents already include their authored V sign (and the Y reflection).
        // TransformNormal applies an additional Source1 normal-map Y flip; bypass it here so
        // the cooked BC5 normal reconstructs the same surface normal as the original game.
        m.Normal = normalize(normalTs.x * normalize(i.vTangentUWs)
                           + normalTs.y * normalize(i.vTangentVWs)
                           + normalTs.z * normalize(i.vNormalWs));
        // DE character packed maps: R = metallic mask, G = roughness.
        // B is a skin/hair scattering mask, not ambient occlusion.
        float2 packed = g_tPacked.Sample(TextureFiltering, uv).rg;
        m.Roughness = saturate(g_flRoughness * lerp(g_tRoughness.Sample(TextureFiltering, uv).r, packed.g, g_flPackedPbr));
        m.Metalness = saturate(g_flMetalness * lerp(g_tMetalness.Sample(TextureFiltering, uv).r, packed.r, g_flPackedPbr));
        m.Emission = g_tEmissive.Sample(TextureFiltering, uv).rgb;
        return ShadingModelStandard::Shade(m);
    }
}
