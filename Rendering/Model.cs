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

    public void Dispose()
    {
        AlbedoMap?.Dispose();
        NormalMap?.Dispose();
        MetallicMap?.Dispose();
        RoughnessMap?.Dispose();
        AOMap?.Dispose();
    }
}

public static class MaterialHelper
{
    private const string GltfMetallicFactor = "$mat.gltf.pbrMetallicRoughness.metallicFactor";
    private const string GltfRoughnessFactor = "$mat.gltf.pbrMetallicRoughness.roughnessFactor";
    private const string GltfBaseColorFactor = "$mat.gltf.pbrMetallicRoughness.baseColorFactor";
    private const string AiMatKeyMetallicFactor = "$mat.metallicFactor";
    private const string AiMatKeyRoughnessFactor = "$mat.roughnessFactor";

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
        var albedo = new Vector3(color.R, color.G, color.B);
        
        float brightness = (albedo.X + albedo.Y + albedo.Z) / 3.0f;
        if (brightness is < 0.02f and > 0.001f)
            albedo *= 0.15f / brightness;
        
        return albedo;
    }

    public static float GetMetallic(Assimp.Material mat)
    {
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

    public static float GetOpacity(Assimp.Material mat) => mat.HasOpacity ? mat.Opacity : 1.0f;

    public static float GetAO(Assimp.Material mat) => 1.0f;

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
        
        foreach (var texData in data.TexturesData)
            model.LoadedTextures.Add(UploadMaterialTextures(gl, texData));
        
        foreach (var meshData in data.Meshes)
            model.Meshes.Add(new Mesh(gl, meshData.Vertices, meshData.Indices, meshData.MaterialIndex, keepVertices: false));
        
        return model;
    }

    private static LoadedMaterialTextures UploadMaterialTextures(GL gl, MaterialTexturesData texData)
    {
        var loadedTextures = new LoadedMaterialTextures();
        
        if (texData.AlbedoMap != null)
            loadedTextures.AlbedoMap = UploadTexture(gl, texData.AlbedoMap);
        if (texData.NormalMap != null)
            loadedTextures.NormalMap = UploadTexture(gl, texData.NormalMap);
        if (texData.MetallicMap != null)
            loadedTextures.MetallicMap = UploadTexture(gl, texData.MetallicMap);
        if (texData.RoughnessMap != null)
            loadedTextures.RoughnessMap = UploadTexture(gl, texData.RoughnessMap);
        if (texData.AOMap != null)
            loadedTextures.AOMap = UploadTexture(gl, texData.AOMap);
        
        return loadedTextures;
    }
    
    private static Texture? UploadTexture(GL gl, TextureData texData)
    {
        try
        {
            if (texData.PixelData.Length > 0 && texData.Width > 0 && texData.Height > 0)
                return Texture.CreateFromPixelData(gl, texData.PixelData, texData.Width, texData.Height, texData.IsSrgb);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Model] Failed to upload texture: {ex.Message}");
        }
        return null;
    }

    public static Model LoadFromFile(GL gl, string path)
    {
        var model = new Model(gl);
        try
        {
            model._assimpContext = new AssimpContext();
            model._assimpScene = model._assimpContext.ImportFile(path,
                PostProcessSteps.Triangulate |
                PostProcessSteps.GenerateNormals |
                PostProcessSteps.CalculateTangentSpace |
                PostProcessSteps.FlipUVs);

            var scene = model._assimpScene;
            if (scene == null || scene.SceneFlags.HasFlag(SceneFlags.Incomplete) || scene.RootNode == null)
                throw new Exception($"Failed to load model: {path}");

            string directory = System.IO.Path.GetDirectoryName(path) ?? "";

            foreach (var mat in scene.Materials)
            {
                model.Materials.Add(mat);
                model.LoadedTextures.Add(LoadMaterialTextures(gl, mat, scene, directory));
            }

            model.ProcessNode(scene.RootNode, scene);
            model.CalculateBounds();
            model.ClearMeshCpuData();

            return model;
        }
        catch
        {
            model.Dispose();
            throw;
        }
    }

    private static LoadedMaterialTextures LoadMaterialTextures(GL gl, Assimp.Material mat, Scene scene, string directory)
    {
        var loadedTextures = new LoadedMaterialTextures();

        if (mat.HasTextureDiffuse)
            loadedTextures.AlbedoMap = LoadTexture(gl, mat.TextureDiffuse, scene, directory, srgb: true);

        if (mat.HasTextureNormal)
            loadedTextures.NormalMap = LoadTexture(gl, mat.TextureNormal.FilePath, directory);
        else if (mat.HasTextureHeight)
            loadedTextures.NormalMap = LoadTexture(gl, mat.TextureHeight.FilePath, directory);

        if (mat.HasTextureSpecular)
            loadedTextures.MetallicMap = LoadTexture(gl, mat.TextureSpecular.FilePath, directory);

        if (mat.GetMaterialTextureCount(TextureType.Shininess) > 0)
        {
            mat.GetMaterialTexture(TextureType.Shininess, 0, out TextureSlot tex);
            loadedTextures.RoughnessMap = LoadTexture(gl, tex.FilePath, directory);
        }

        if (mat.GetMaterialTextureCount(TextureType.Ambient) > 0)
        {
            mat.GetMaterialTexture(TextureType.Ambient, 0, out TextureSlot tex);
            loadedTextures.AOMap = LoadTexture(gl, tex.FilePath, directory);
        }

        return loadedTextures;
    }

    private static Texture? LoadTexture(GL gl, TextureSlot texSlot, Scene scene, string directory, bool srgb)
    {
        if (texSlot.FilePath.StartsWith("*"))
            return LoadEmbeddedTexture(gl, texSlot.FilePath, scene, srgb);
        
        return LoadTexture(gl, texSlot.FilePath, directory, srgb);
    }

    private static Texture? LoadEmbeddedTexture(GL gl, string filePath, Scene scene, bool srgb)
    {
        if (!int.TryParse(filePath.AsSpan(1), out int texIndex) || texIndex >= scene.TextureCount)
            return null;

        var embeddedTex = scene.Textures[texIndex];
        if (!embeddedTex.HasCompressedData) return null;

        try
        {
            return Texture.LoadFromMemory(gl, embeddedTex.CompressedData, srgb);
        }
        catch
        {
            return null;
        }
    }

    private static Texture? LoadTexture(GL gl, string filePath, string directory, bool srgb = false)
    {
        string texPath = System.IO.Path.Combine(directory, filePath);
        return System.IO.File.Exists(texPath) ? Texture.LoadFromFile(gl, texPath, srgb) : null;
    }

    private void ProcessNode(Node node, Scene scene, Matrix4x4 parentTransform = default)
    {
        var nodeTransform = parentTransform == default ? Matrix4x4.Identity : parentTransform;
        
        var assimpMatrix = node.Transform;
        var transform = new Matrix4x4(
            assimpMatrix.A1, assimpMatrix.B1, assimpMatrix.C1, assimpMatrix.D1,
            assimpMatrix.A2, assimpMatrix.B2, assimpMatrix.C2, assimpMatrix.D2,
            assimpMatrix.A3, assimpMatrix.B3, assimpMatrix.C3, assimpMatrix.D3,
            assimpMatrix.A4, assimpMatrix.B4, assimpMatrix.C4, assimpMatrix.D4
        );
        
        nodeTransform = transform * nodeTransform;
        
        foreach (var meshIndex in node.MeshIndices)
        {
            var assimpMesh = scene.Meshes[meshIndex];
            Meshes.Add(ProcessMesh(assimpMesh, nodeTransform, assimpMesh.MaterialIndex));
        }

        foreach (var child in node.Children)
            ProcessNode(child, scene, nodeTransform);
    }

    private Mesh ProcessMesh(Assimp.Mesh assimpMesh, Matrix4x4 transform, int materialIndex)
    {
        try
        {
            Matrix4x4.Invert(transform, out var invTransform);
            var normalMatrix = Matrix4x4.Transpose(invTransform);

            var vertices = new Vertex[assimpMesh.VertexCount];
            for (int i = 0; i < assimpMesh.VertexCount; i++)
                vertices[i] = CreateVertex(assimpMesh, i, transform, normalMatrix);

            var indices = new List<uint>();
            foreach (var face in assimpMesh.Faces)
            {
                foreach (var index in face.Indices)
                    indices.Add((uint)index);
            }

            return new Mesh(_gl, vertices, indices.ToArray(), materialIndex, keepVertices: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Model] ERROR processing mesh: {ex.Message}");
            throw;
        }
    }

    private static Vertex CreateVertex(Assimp.Mesh mesh, int index, Matrix4x4 transform, Matrix4x4 normalMatrix)
    {
        var pos = new Vector3(mesh.Vertices[index].X, mesh.Vertices[index].Y, mesh.Vertices[index].Z);
        
        var normal = mesh.HasNormals
            ? new Vector3(mesh.Normals[index].X, mesh.Normals[index].Y, mesh.Normals[index].Z)
            : Vector3.UnitY;
        
        var tangent = mesh.HasTangentBasis
            ? new Vector3(mesh.Tangents[index].X, mesh.Tangents[index].Y, mesh.Tangents[index].Z)
            : Vector3.UnitX;
        
        var bitangent = mesh.HasTangentBasis
            ? new Vector3(mesh.BiTangents[index].X, mesh.BiTangents[index].Y, mesh.BiTangents[index].Z)
            : Vector3.UnitZ;

        return new Vertex
        {
            Position = Vector3.Transform(pos, transform),
            Normal = Vector3.Normalize(Vector3.TransformNormal(normal, normalMatrix)),
            TexCoord = mesh.HasTextureCoords(0)
                ? new Vector2(mesh.TextureCoordinateChannels[0][index].X, mesh.TextureCoordinateChannels[0][index].Y)
                : Vector2.Zero,
            Tangent = Vector3.Normalize(Vector3.TransformNormal(tangent, normalMatrix)),
            Bitangent = Vector3.Normalize(Vector3.TransformNormal(bitangent, normalMatrix))
        };
    }

    private void CalculateBounds()
    {
        if (Meshes.Count == 0)
        {
            BoundsMin = BoundsMax = Vector3.Zero;
            Radius = 1.0f;
            return;
        }

        BoundsMin = new Vector3(float.MaxValue);
        BoundsMax = new Vector3(float.MinValue);

        foreach (var mesh in Meshes)
        {
            var meshBounds = mesh.GetBounds();
            BoundsMin = Vector3.Min(BoundsMin, meshBounds.Min);
            BoundsMax = Vector3.Max(BoundsMax, meshBounds.Max);
        }

        Radius = Vector3.Distance(BoundsMin, BoundsMax) * 0.5f;
        
        ScaleUpIfTooSmall();
    }

    private void ScaleUpIfTooSmall()
    {
        if (Radius >= 0.1f) return;

        const float scale = 100.0f;
        BoundsMin *= scale;
        BoundsMax *= scale;
        Radius *= scale;
        
        foreach (var mesh in Meshes)
            mesh.Scale(scale);
    }

    public void ClearMeshCpuData()
    {
        foreach (var mesh in Meshes)
            mesh.ClearCpuVertexData();
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

