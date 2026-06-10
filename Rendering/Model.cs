using System;
using System.Collections.Generic;
using System.Numerics;
using Assimp;
using Silk.NET.OpenGL;

using Matrix4x4 = System.Numerics.Matrix4x4;

namespace Avalonia3DViewer.Rendering;

public class LoadedMaterialTextures : IDisposable
{
    public Texture? AlbedoMap { get; set; }
    public Texture? NormalMap { get; set; }
    public Texture? MetallicMap { get; set; }
    public Texture? RoughnessMap { get; set; }
    public Texture? AOMap { get; set; }

    // Channel each value is stored in (0=R, 1=G, 2=B, 3=A). See MaterialTexturesData.
    public int MetallicChannel { get; set; }
    public int RoughnessChannel { get; set; }
    public int AoChannel { get; set; }

    // True when the roughness map stores glossiness (roughness = 1 - sample).
    public bool RoughnessInvert { get; set; }

    public void Dispose()
    {
        // Note: packed textures may be shared between slots (and between
        // materials). Texture.Dispose is idempotent, so double-dispose of a
        // shared instance is safe.
        AlbedoMap?.Dispose();
        NormalMap?.Dispose();
        MetallicMap?.Dispose();
        RoughnessMap?.Dispose();
        AOMap?.Dispose();
    }
}

/// <summary>
/// Alpha blending mode - matches glTF specification
/// </summary>
public enum AlphaMode
{
    /// <summary>Alpha is ignored, rendered as fully opaque</summary>
    Opaque = 0,
    /// <summary>Alpha cutout/test - fragments below cutoff are discarded</summary>
    Mask = 1,
    /// <summary>True alpha blending with background</summary>
    Blend = 2,
    /// <summary>No explicit alpha mode specified - use heuristics</summary>
    Unknown = -1
}

public static class MaterialHelper
{
    private const string GltfMetallicFactor = "$mat.gltf.pbrMetallicRoughness.metallicFactor";
    private const string GltfRoughnessFactor = "$mat.gltf.pbrMetallicRoughness.roughnessFactor";
    private const string GltfBaseColorFactor = "$mat.gltf.pbrMetallicRoughness.baseColorFactor";
    private const string GltfAlphaMode = "$mat.gltf.alphaMode";
    private const string GltfAlphaCutoff = "$mat.gltf.alphaCutoff";
    private const string AiMatKeyMetallicFactor = "$mat.metallicFactor";
    private const string AiMatKeyRoughnessFactor = "$mat.roughnessFactor";

    // KHR_materials_pbrSpecularGlossiness (very common in Sketchfab exports).
    // Key names differ between assimp versions, so several are checked.
    private const string GltfSpecularGlossinessFlag = "$mat.gltf.pbrSpecularGlossiness";
    private const string AiMatKeyGlossinessFactor = "$mat.glossinessFactor";
    private const string GltfGlossinessFactor = "$mat.gltf.pbrSpecularGlossiness.glossinessFactor";

    /// <summary>
    /// True when the material uses the glTF specular-glossiness workflow.
    /// These materials are dielectric (metallic = 0) and store glossiness
    /// (inverse roughness) in the alpha of the specularGlossiness texture.
    /// </summary>
    public static bool IsSpecularGlossiness(Assimp.Material mat)
    {
        foreach (var prop in mat.GetAllProperties())
        {
            if (prop.Name == GltfSpecularGlossinessFlag ||
                prop.Name == AiMatKeyGlossinessFactor ||
                prop.Name == GltfGlossinessFactor ||
                prop.Name.Contains("pbrSpecularGlossiness", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public static Vector3 GetAlbedo(Assimp.Material mat)
    {
        foreach (var prop in mat.GetAllProperties())
        {
            if (prop.Name != GltfBaseColorFactor || prop.PropertyType != PropertyType.Float) continue;
            
            var values = prop.GetFloatArrayValue();
            if (values.Length >= 3)
                return new Vector3(values[0], values[1], values[2]);
        }
        
        if (!mat.HasColorDiffuse) return Vector3.One;

        var color = mat.ColorDiffuse;
        var albedo = new Vector3(color.X, color.Y, color.Z);
        
        float brightness = (albedo.X + albedo.Y + albedo.Z) / 3.0f;
        if (brightness is < 0.02f and > 0.001f)
            albedo *= 0.15f / brightness;
        
        return albedo;
    }

    public static float GetMetallic(Assimp.Material mat)
    {
        // IMPORTANT: this check must come FIRST. Assimp's glTF importer writes
        // the metallic-roughness factors even for specular-glossiness materials,
        // using the glTF spec defaults (metallicFactor = 1.0!). Trusting that
        // factor would make every spec-gloss model (most Sketchfab exports)
        // render as full metal. Spec-gloss materials are dielectric by definition.
        if (IsSpecularGlossiness(mat))
            return 0.0f;

        if (TryGetFloatProperty(mat, GltfMetallicFactor, out float gltfMetallic))
            return Math.Clamp(gltfMetallic, 0.0f, 1.0f);

        if (TryGetFloatProperty(mat, AiMatKeyMetallicFactor, out float aiMetallic))
            return Math.Clamp(aiMetallic, 0.0f, 1.0f);

        if (mat.HasReflectivity)
            return Math.Clamp(mat.Reflectivity, 0.0f, 1.0f);

        return 0.0f;
    }

    public static float GetRoughness(Assimp.Material mat)
    {
        // Spec-gloss first: assimp also writes the metallic-roughness factors
        // (glTF defaults: roughnessFactor = 1.0) for spec-gloss materials, so
        // the glossiness conversion must take precedence (see GetMetallic).
        if (IsSpecularGlossiness(mat))
        {
            if (TryGetFloatProperty(mat, AiMatKeyGlossinessFactor, out float gloss) ||
                TryGetFloatProperty(mat, GltfGlossinessFactor, out gloss))
            {
                return Math.Clamp(1.0f - gloss, 0.04f, 1.0f);
            }
            return 0.5f; // No factor: neutral default for spec-gloss
        }

        if (TryGetFloatProperty(mat, GltfRoughnessFactor, out float gltfRoughness))
            return Math.Clamp(gltfRoughness, 0.04f, 1.0f);

        if (TryGetFloatProperty(mat, AiMatKeyRoughnessFactor, out float aiRoughness))
            return Math.Clamp(aiRoughness, 0.04f, 1.0f);

        if (mat.HasShininess)
        {
            float roughness = MathF.Sqrt(2.0f / (mat.Shininess + 2.0f));
            return Math.Clamp(roughness, 0.04f, 1.0f);
        }

        return 0.7f;
    }

    /// <summary>
    /// Multiplier for the sampled metallic texture value. Per the glTF spec,
    /// metallic = metallicFactor × texture.B. Returns 1 when no factor exists
    /// (legacy formats), so standalone metallic maps are unaffected.
    /// </summary>
    public static float GetMetallicTextureScale(Assimp.Material mat)
    {
        if (TryGetFloatProperty(mat, GltfMetallicFactor, out float f) ||
            TryGetFloatProperty(mat, AiMatKeyMetallicFactor, out f))
        {
            return Math.Clamp(f, 0.0f, 1.0f);
        }
        return 1.0f;
    }

    /// <summary>
    /// Multiplier for the sampled roughness/glossiness texture value.
    /// glTF metallic-roughness: roughness = roughnessFactor × texture.G.
    /// glTF specular-glossiness: glossiness = glossinessFactor × texture.A
    /// (the shader inverts after scaling). Returns 1 when no factor exists.
    /// </summary>
    public static float GetRoughnessTextureScale(Assimp.Material mat)
    {
        if (IsSpecularGlossiness(mat))
        {
            if (TryGetFloatProperty(mat, AiMatKeyGlossinessFactor, out float g) ||
                TryGetFloatProperty(mat, GltfGlossinessFactor, out g))
            {
                return Math.Clamp(g, 0.0f, 1.0f);
            }
            return 1.0f;
        }

        if (TryGetFloatProperty(mat, GltfRoughnessFactor, out float r) ||
            TryGetFloatProperty(mat, AiMatKeyRoughnessFactor, out r))
        {
            return Math.Clamp(r, 0.0f, 1.0f);
        }
        return 1.0f;
    }

    public static float GetOpacity(Assimp.Material mat) => mat.HasOpacity ? mat.Opacity : 1.0f;

    public static float GetAO(Assimp.Material mat) => 1.0f;

    /// <summary>
    /// Gets the glTF alpha mode from the material properties.
    /// Returns Unknown if the property is not present (non-glTF formats).
    /// </summary>
    public static AlphaMode GetGltfAlphaMode(Assimp.Material mat)
    {
        foreach (var prop in mat.GetAllProperties())
        {
            if (prop.Name != GltfAlphaMode || prop.PropertyType != PropertyType.String)
                continue;
            
            string mode = prop.GetStringValue();
            return mode?.ToUpperInvariant() switch
            {
                "OPAQUE" => AlphaMode.Opaque,
                "MASK" => AlphaMode.Mask,
                "BLEND" => AlphaMode.Blend,
                _ => AlphaMode.Unknown
            };
        }
        return AlphaMode.Unknown;
    }

    /// <summary>
    /// Gets the glTF alpha cutoff value for MASK mode.
    /// Returns 0.5 (glTF default) if not specified.
    /// </summary>
    public static float GetGltfAlphaCutoff(Assimp.Material mat)
    {
        if (TryGetFloatProperty(mat, GltfAlphaCutoff, out float cutoff))
            return Math.Clamp(cutoff, 0.0f, 1.0f);
        return 0.5f; // glTF default
    }

    private static bool TryGetFloatProperty(Assimp.Material mat, string key, out float value)
    {
        value = 0.0f;
        foreach (var prop in mat.GetAllProperties())
        {
            if (prop.Name != key || prop.PropertyType != PropertyType.Float) continue;
            value = prop.GetFloatValue();
            return true;
        }
        return false;
    }
}

public class Model : IDisposable
{
    public List<Mesh> Meshes { get; } = new();
    public List<Assimp.Material> Materials { get; } = new();
    public List<LoadedMaterialTextures> LoadedTextures { get; } = new();
    
    public Vector3 BoundsMin { get; private set; }
    public Vector3 BoundsMax { get; private set; }
    public Vector3 Center => (BoundsMin + BoundsMax) * 0.5f;
    public float Radius { get; private set; }

    private readonly GL _gl;
    private AssimpContext? _assimpContext;
    private Scene? _assimpScene;
    private bool _disposed;

    public Model(GL gl)
    {
        _gl = gl;
    }

    public static Model CreateFromLoadData(GL gl, ModelLoadData data)
    {
        var model = new Model(gl);

        model._assimpContext = data.AssimpContext;
        model._assimpScene = data.Scene;
        data.AssimpContext = null;
        data.Scene = null;
        
        model.BoundsMin = data.BoundsMin;
        model.BoundsMax = data.BoundsMax;
        model.Radius = data.Radius;
        
        model.Materials.AddRange(data.Materials);
        data.Materials.Clear();

        // Shared decoded textures (packed glTF maps, textures reused across
        // materials) are uploaded to the GPU only once.
        var uploadCache = new Dictionary<TextureData, Texture?>(ReferenceEqualityComparer.Instance);
        foreach (var texData in data.TexturesData)
            model.LoadedTextures.Add(UploadMaterialTextures(gl, texData, uploadCache));

        foreach (var meshData in data.Meshes)
            model.Meshes.Add(new Mesh(gl, meshData.Vertices, meshData.Indices, meshData.MaterialIndex, keepVertices: false));

        return model;
    }

    private static LoadedMaterialTextures UploadMaterialTextures(
        GL gl, MaterialTexturesData texData, Dictionary<TextureData, Texture?> uploadCache)
    {
        var loadedTextures = new LoadedMaterialTextures
        {
            MetallicChannel = texData.MetallicChannel,
            RoughnessChannel = texData.RoughnessChannel,
            AoChannel = texData.AoChannel,
            RoughnessInvert = texData.RoughnessInvert
        };

        if (texData.AlbedoMap != null)
            loadedTextures.AlbedoMap = UploadTexture(gl, texData.AlbedoMap, uploadCache);
        if (texData.NormalMap != null)
            loadedTextures.NormalMap = UploadTexture(gl, texData.NormalMap, uploadCache);
        if (texData.MetallicMap != null)
            loadedTextures.MetallicMap = UploadTexture(gl, texData.MetallicMap, uploadCache);
        if (texData.RoughnessMap != null)
            loadedTextures.RoughnessMap = UploadTexture(gl, texData.RoughnessMap, uploadCache);
        if (texData.AOMap != null)
            loadedTextures.AOMap = UploadTexture(gl, texData.AOMap, uploadCache);

        return loadedTextures;
    }

    private static Texture? UploadTexture(GL gl, TextureData texData, Dictionary<TextureData, Texture?> uploadCache)
    {
        if (uploadCache.TryGetValue(texData, out var cached))
            return cached;

        Texture? texture = null;
        try
        {
            if (texData.PixelData != null && texData.PixelData.Length > 0 && texData.Width > 0 && texData.Height > 0)
                texture = Texture.CreateFromPixelData(gl, texData.PixelData, texData.Width, texData.Height, texData.IsSrgb);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Model] Failed to upload texture: {ex.Message}");
        }

        uploadCache[texData] = texture;
        return texture;
    }

    public static Model LoadFromFile(GL gl, string path)
    {
        // Share the loader with the async path so both behave identically
        // (embedded textures, packed glTF channels, bounds, scaling, ...).
        var data = ModelLoader.Load(path);
        try
        {
            return CreateFromLoadData(gl, data);
        }
        finally
        {
            data.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var mesh in Meshes)
            mesh.Dispose();
        Meshes.Clear();

        foreach (var loadedTex in LoadedTextures)
            loadedTex.Dispose();
        LoadedTextures.Clear();

        Materials.Clear();

        _assimpScene = null;
        _assimpContext?.Dispose();
        _assimpContext = null;
    }
}

