namespace Avalonia3DViewer.Rendering;

/// <summary>
/// Some formats (notably glTF) often pack multiple material channels into one texture:
/// - MetallicRoughness:  G = roughness, B = metallic
/// - ORM (OcclusionRoughnessMetallic): R = AO, G = roughness, B = metallic
/// </summary>
public enum PackedTextureMode
{
    None = 0,
    MetallicRoughness = 1,
    OcclusionRoughnessMetallic = 2
}


