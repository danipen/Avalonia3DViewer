using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Assimp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Matrix4x4 = System.Numerics.Matrix4x4;

namespace Avalonia3DViewer.Rendering;

/// <summary>
/// Holds raw vertex data before GPU upload
/// </summary>
public class MeshData
{
    public Vertex[] Vertices { get; set; } = Array.Empty<Vertex>();
    public uint[] Indices { get; set; } = Array.Empty<uint>();
    public int MaterialIndex { get; set; }
    
    /// <summary>
    /// Clears the vertex and index data to free memory after GPU upload.
    /// Sets arrays to null to allow GC to collect the large arrays.
    /// </summary>
    public void ClearData()
    {
        Vertices = null!;
        Indices = null!;
    }
}

/// <summary>
/// Holds texture data loaded and decoded from disk, ready for GPU upload
/// </summary>
public class TextureData
{
    public byte[] PixelData { get; set; } = Array.Empty<byte>();
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsSrgb { get; set; }
    
    /// <summary>
    /// Clears the pixel data to free memory after GPU upload.
    /// Sets array to null to allow GC to collect the large pixel data.
    /// </summary>
    public void ClearData()
    {
        PixelData = null!;
        Width = 0;
        Height = 0;
    }
}

/// <summary>
/// Holds material textures data before GPU upload
/// </summary>
public class MaterialTexturesData
{
    public TextureData? AlbedoMap { get; set; }
    public TextureData? NormalMap { get; set; }
    public TextureData? MetallicMap { get; set; }
    public TextureData? RoughnessMap { get; set; }
    public TextureData? AOMap { get; set; }
    
    /// <summary>
    /// Clears all texture data to free memory after GPU upload.
    /// </summary>
    public void ClearData()
    {
        AlbedoMap?.ClearData();
        NormalMap?.ClearData();
        MetallicMap?.ClearData();
        RoughnessMap?.ClearData();
        AOMap?.ClearData();
        AlbedoMap = null;
        NormalMap = null;
        MetallicMap = null;
        RoughnessMap = null;
        AOMap = null;
    }
}

/// <summary>
/// Holds all model data parsed on background thread, ready for GPU upload
/// </summary>
public class ModelLoadData : IDisposable
{
    public List<MeshData> Meshes { get; } = new();
    public List<Assimp.Material> Materials { get; } = new();
    public List<MaterialTexturesData> TexturesData { get; } = new();
    public Vector3 BoundsMin { get; set; }
    public Vector3 BoundsMax { get; set; }
    public float Radius { get; set; }
    public Vector3 Center => (BoundsMin + BoundsMax) * 0.5f;

    /// <summary>
    /// Holds the Assimp context/scene that own the unmanaged material data.
    /// These are transferred to <see cref="Model"/> on GPU upload, and disposed
    /// here only if the load data is abandoned (e.g. canceled/replaced).
    /// </summary>
    internal AssimpContext? AssimpContext { get; set; }
    internal Scene? Scene { get; set; }
    
    /// <summary>
    /// Clears all loaded data to free memory after GPU upload.
    /// Call this after the model has been uploaded to the GPU.
    /// </summary>
    public void Dispose()
    {
        // If the Assimp objects haven't been transferred to a Model, dispose them here.
        // This prevents leaks when a load is canceled or replaced before upload.
        // Note: In this Assimp.Net version, Scene itself isn't IDisposable; disposing the context is what releases unmanaged data.
        Scene = null;
        AssimpContext?.Dispose();
        AssimpContext = null;

        // Clear all mesh vertex/index data
        foreach (var mesh in Meshes)
        {
            mesh.ClearData();
        }
        Meshes.Clear();
        
        // Clear all texture pixel data
        foreach (var texData in TexturesData)
        {
            texData.ClearData();
        }
        TexturesData.Clear();
        
        // Clear materials list (Assimp materials are managed by the scene)
        Materials.Clear();
    }
}

/// <summary>
/// Progress reporting for model loading
/// </summary>
public class ModelLoadProgress
{
    public string Stage { get; set; } = "";
    public int Current { get; set; }
    public int Total { get; set; }
}

/// <summary>
/// Async model loader - parses model on background thread
/// </summary>
public static class ModelLoader
{
    /// <summary>
    /// Maximum texture dimension (width or height). Textures larger than this will be downscaled.
    /// This helps prevent memory bloat from 4K/8K textures.
    /// </summary>
    public const int MaxTextureSize = 2048;
    
    private sealed class TextureLoadState
    {
        public int Index;
    }

    public static async Task<ModelLoadData> LoadAsync(
        string path,
        IProgress<ModelLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() => LoadInternal(path, progress, cancellationToken), cancellationToken);
    }

    private static ModelLoadData LoadInternal(
        string path,
        IProgress<ModelLoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var data = new ModelLoadData();
        try
        {
            // Indeterminate: opening/parsing can take an unknown amount of time depending on file IO and format.
            progress?.Report(new ModelLoadProgress { Stage = "Opening file...", Current = 0, Total = 0 });

            // Do NOT dispose AssimpContext/Scene here: the unmanaged material data is referenced by Assimp.Material
            // objects and must stay alive until the Model is disposed (ownership is transferred on upload).
            var context = new AssimpContext();
            var scene = context.ImportFile(path,
                PostProcessSteps.Triangulate |
                PostProcessSteps.GenerateNormals |
                PostProcessSteps.CalculateTangentSpace |
                PostProcessSteps.FlipUVs);

            if (scene == null || scene.SceneFlags.HasFlag(SceneFlags.Incomplete) || scene.RootNode == null)
            {
                context.Dispose();
                throw new Exception($"Failed to load model: {path}");
            }

            data.AssimpContext = context;
            data.Scene = scene;

            cancellationToken.ThrowIfCancellationRequested();

            string directory = System.IO.Path.GetDirectoryName(path) ?? "";
            var textureCache = new Dictionary<string, TextureData?>(StringComparer.OrdinalIgnoreCase);

            // Load materials and textures
            int totalTextureLoads = CountPlannedTextureLoads(scene);
            if (totalTextureLoads <= 0)
            {
                progress?.Report(new ModelLoadProgress { Stage = "No textures to load", Current = 1, Total = 1 });
                totalTextureLoads = 1;
            }
            else
            {
                progress?.Report(new ModelLoadProgress { Stage = "Loading textures...", Current = 0, Total = totalTextureLoads });
            }

            var textureLoadState = new TextureLoadState { Index = 0 };
            foreach (var mat in scene.Materials)
            {
                cancellationToken.ThrowIfCancellationRequested();

                data.Materials.Add(mat);
                var texturesData = LoadMaterialTextures(mat, scene, directory, textureCache, progress, textureLoadState, totalTextureLoads, cancellationToken);
                data.TexturesData.Add(texturesData);
            }
            
            // Clear texture cache to free memory - textures are now in TexturesData
            textureCache.Clear();

            // Process meshes
            var allMeshes = new List<MeshData>();
            int meshCount = Math.Max(1, scene.MeshCount);
            int meshIndex = 0;
            ProcessNode(scene.RootNode, scene, Matrix4x4.Identity, allMeshes, ref meshIndex, meshCount, progress, cancellationToken);

            foreach (var mesh in allMeshes)
            {
                data.Meshes.Add(mesh);
            }

            // Calculate bounds
            progress?.Report(new ModelLoadProgress { Stage = "Finalizing...", Current = 0, Total = 1 });
            CalculateBounds(data);

            progress?.Report(new ModelLoadProgress { Stage = "Preparing for GPU...", Current = 1, Total = 1 });

            return data;
        }
        catch
        {
            data.Dispose();
            throw;
        }
    }

    private static MaterialTexturesData LoadMaterialTextures(
        Assimp.Material mat,
        Scene scene,
        string directory,
        Dictionary<string, TextureData?> textureCache,
        IProgress<ModelLoadProgress>? progress,
        TextureLoadState textureLoadState,
        int totalTextureLoads,
        CancellationToken cancellationToken)
    {
        var data = new MaterialTexturesData();

        TextureData? LoadOne(string path, bool isSrgb)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Report *before* the slow work starts so the UI updates even if decode takes a long time.
            int nextIndex = Math.Min(textureLoadState.Index + 1, totalTextureLoads);
            var progressData = new ModelLoadProgress
            {
                Stage = $"Loading texture ({nextIndex}/{totalTextureLoads})",
                // Use 1-based progress so "Loading texture N/N" maps to 100% even while the last texture is decoding.
                Current = nextIndex,
                Total = totalTextureLoads
            };
            progress?.Report(progressData);

            TextureData? tex = null;
            try
            {
                tex = LoadTextureData(path, scene, directory, isSrgb, textureCache, cancellationToken);
                return tex;
            }
            finally
            {
                // Always advance, even if load fails, to keep progress moving.
                textureLoadState.Index = Math.Min(textureLoadState.Index + 1, totalTextureLoads);
                // Keep progress consistent with the stage text (1..Total).
                progressData.Current = Math.Min(textureLoadState.Index, totalTextureLoads);
                progress?.Report(progressData);
            }
        }

        // Albedo/Diffuse texture
        if (mat.HasTextureDiffuse)
        {
            var texSlot = mat.TextureDiffuse;
            data.AlbedoMap = LoadOne(texSlot.FilePath, isSrgb: true);
        }

        // Normal map
        if (mat.HasTextureNormal)
        {
            data.NormalMap = LoadOne(mat.TextureNormal.FilePath, isSrgb: false);
        }
        else if (mat.HasTextureHeight)
        {
            data.NormalMap = LoadOne(mat.TextureHeight.FilePath, isSrgb: false);
        }

        // Metallic/Specular
        if (mat.HasTextureSpecular)
        {
            data.MetallicMap = LoadOne(mat.TextureSpecular.FilePath, isSrgb: false);
        }

        // Roughness/Shininess
        if (mat.GetMaterialTextureCount(TextureType.Shininess) > 0)
        {
            mat.GetMaterialTexture(TextureType.Shininess, 0, out TextureSlot tex);
            data.RoughnessMap = LoadOne(tex.FilePath, isSrgb: false);
        }

        // AO
        if (mat.GetMaterialTextureCount(TextureType.Ambient) > 0)
        {
            mat.GetMaterialTexture(TextureType.Ambient, 0, out TextureSlot tex);
            data.AOMap = LoadOne(tex.FilePath, isSrgb: false);
        }

        return data;
    }

    private static TextureData? LoadTextureData(
        string filePath,
        Scene scene,
        string directory,
        bool isSrgb,
        Dictionary<string, TextureData?> textureCache,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            string cacheKey;
            
            // Check if it's an embedded texture
            if (filePath.StartsWith("*"))
            {
                cacheKey = $"embedded:{filePath}|srgb:{isSrgb}";
                if (textureCache.TryGetValue(cacheKey, out var cachedEmbedded))
                    return cachedEmbedded;

                if (int.TryParse(filePath.Substring(1), out int texIndex) && texIndex < scene.TextureCount)
                {
                    var embeddedTex = scene.Textures[texIndex];
                    if (embeddedTex.HasCompressedData)
                    {
                        // Decode embedded compressed bytes (usually png/jpg) without copying into a new array.
                        var imageBytes = embeddedTex.CompressedData;

                        cancellationToken.ThrowIfCancellationRequested();

                        using var stream = new MemoryStream(imageBytes, writable: false);
                        using var img = Image.Load<Rgba32>(stream);
                        
                        // Downscale if texture exceeds max size
                        DownscaleIfNeeded(img);

                        var pixels = new byte[img.Width * img.Height * 4];
                        img.CopyPixelDataTo(pixels);

                        cancellationToken.ThrowIfCancellationRequested();

                        var result = new TextureData
                        {
                            PixelData = pixels,
                            Width = img.Width,
                            Height = img.Height,
                            IsSrgb = isSrgb
                        };

                        textureCache[cacheKey] = result;
                        return result;
                    }
                    else
                    {
                        textureCache[cacheKey] = null;
                        return null;
                    }
                }
                else
                {
                    textureCache[cacheKey] = null;
                    return null;
                }
            }
            else
            {
                string texPath = Path.Combine(directory, filePath);
                // Prefer a stable absolute-ish key to maximize cache hits.
                string fullTexPath;
                try
                {
                    fullTexPath = Path.GetFullPath(texPath);
                }
                catch
                {
                    fullTexPath = texPath;
                }

                cacheKey = $"{fullTexPath}|srgb:{isSrgb}";
                if (textureCache.TryGetValue(cacheKey, out var cachedFile))
                    return cachedFile;

                if (!File.Exists(fullTexPath))
                {
                    textureCache[cacheKey] = null;
                    return null;
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Decode directly from file stream to avoid ReadAllBytes + MemoryStream allocations.
                using var fs = File.OpenRead(fullTexPath);
                using var img = Image.Load<Rgba32>(fs);
                
                // Downscale if texture exceeds max size
                DownscaleIfNeeded(img);

                var pixels = new byte[img.Width * img.Height * 4];
                img.CopyPixelDataTo(pixels);

                cancellationToken.ThrowIfCancellationRequested();

                var result = new TextureData
                {
                    PixelData = pixels,
                    Width = img.Width,
                    Height = img.Height,
                    IsSrgb = isSrgb
                };

                textureCache[cacheKey] = result;
                return result;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModelLoader] Failed to load texture {filePath}: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Downscales the image if it exceeds MaxTextureSize while preserving aspect ratio.
    /// </summary>
    private static void DownscaleIfNeeded(Image<Rgba32> img)
    {
        if (img.Width <= MaxTextureSize && img.Height <= MaxTextureSize)
            return;
            
        int originalWidth = img.Width;
        int originalHeight = img.Height;
        
        // Calculate new size maintaining aspect ratio
        float scale = Math.Min((float)MaxTextureSize / img.Width, (float)MaxTextureSize / img.Height);
        int newWidth = Math.Max(1, (int)(img.Width * scale));
        int newHeight = Math.Max(1, (int)(img.Height * scale));
        
        img.Mutate(x => x.Resize(newWidth, newHeight));
        
        Console.WriteLine($"[ModelLoader] Downscaled texture from {originalWidth}x{originalHeight} to {newWidth}x{newHeight}");
    }

    private static int CountPlannedTextureLoads(Scene scene)
    {
        int count = 0;
        foreach (var mat in scene.Materials)
        {
            // At most one per slot, matching what LoadMaterialTextures actually loads.
            if (mat.HasTextureDiffuse) count++;
            if (mat.HasTextureNormal || mat.HasTextureHeight) count++;
            if (mat.HasTextureSpecular) count++;
            if (mat.GetMaterialTextureCount(TextureType.Shininess) > 0) count++;
            if (mat.GetMaterialTextureCount(TextureType.Ambient) > 0) count++;
        }
        return count;
    }

    private static void ProcessNode(
        Node node, 
        Scene scene, 
        Matrix4x4 parentTransform,
        List<MeshData> meshes,
        ref int meshIndex,
        int totalMeshCount,
        IProgress<ModelLoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Get node transformation
        // Assimp/AssimpNetter node transforms are effectively transposed vs how System.Numerics
        // expects them when using Vector3.Transform (row-vector convention).
        // We keep the previous behavior (from AssimpNet) by transposing here.
        var transform = Matrix4x4.Transpose(node.Transform);
        
        var nodeTransform = transform * parentTransform;
        
        foreach (var mi in node.MeshIndices)
        {
            var assimpMesh = scene.Meshes[mi];
            meshes.Add(ProcessMesh(assimpMesh, nodeTransform, assimpMesh.MaterialIndex));
            
            meshIndex++;
            progress?.Report(new ModelLoadProgress
            {
                Stage = $"Processing meshes ({meshIndex}/{totalMeshCount})",
                Current = meshIndex,
                Total = totalMeshCount
            });
        }

        foreach (var child in node.Children)
        {
            ProcessNode(child, scene, nodeTransform, meshes, ref meshIndex, totalMeshCount, progress, cancellationToken);
        }
    }

    private static MeshData ProcessMesh(Assimp.Mesh assimpMesh, Matrix4x4 transform, int materialIndex)
    {
        var vertices = new List<Vertex>();
        var indices = new List<uint>();

        // Calculate normal matrix
        Matrix4x4.Invert(transform, out var invTransform);
        var normalMatrix = Matrix4x4.Transpose(invTransform);

        // Process vertices
        for (int i = 0; i < assimpMesh.VertexCount; i++)
        {
            var pos = new Vector3(
                assimpMesh.Vertices[i].X,
                assimpMesh.Vertices[i].Y,
                assimpMesh.Vertices[i].Z);
            
            var normal = assimpMesh.HasNormals
                ? new Vector3(assimpMesh.Normals[i].X, assimpMesh.Normals[i].Y, assimpMesh.Normals[i].Z)
                : Vector3.UnitY;
            
            var tangent = assimpMesh.HasTangentBasis
                ? new Vector3(assimpMesh.Tangents[i].X, assimpMesh.Tangents[i].Y, assimpMesh.Tangents[i].Z)
                : Vector3.UnitX;
            
            var bitangent = assimpMesh.HasTangentBasis
                ? new Vector3(assimpMesh.BiTangents[i].X, assimpMesh.BiTangents[i].Y, assimpMesh.BiTangents[i].Z)
                : Vector3.UnitZ;
            
            // Apply transformation
            var transformedPos = Vector3.Transform(pos, transform);
            var transformedNormal = Vector3.Normalize(Vector3.TransformNormal(normal, normalMatrix));
            var transformedTangent = Vector3.Normalize(Vector3.TransformNormal(tangent, normalMatrix));
            var transformedBitangent = Vector3.Normalize(Vector3.TransformNormal(bitangent, normalMatrix));
            
            var vertex = new Vertex
            {
                Position = transformedPos,
                Normal = transformedNormal,
                TexCoord = assimpMesh.HasTextureCoords(0)
                    ? new Vector2(assimpMesh.TextureCoordinateChannels[0][i].X, assimpMesh.TextureCoordinateChannels[0][i].Y)
                    : Vector2.Zero,
                Tangent = transformedTangent,
                Bitangent = transformedBitangent
            };

            vertices.Add(vertex);
        }

        // Process indices
        foreach (var face in assimpMesh.Faces)
        {
            foreach (var index in face.Indices)
            {
                indices.Add((uint)index);
            }
        }

        return new MeshData
        {
            Vertices = vertices.ToArray(),
            Indices = indices.ToArray(),
            MaterialIndex = materialIndex
        };
    }

    private static void CalculateBounds(ModelLoadData data)
    {
        if (data.Meshes.Count == 0)
        {
            data.BoundsMin = data.BoundsMax = Vector3.Zero;
            data.Radius = 1.0f;
            return;
        }

        var boundsMin = new Vector3(float.MaxValue);
        var boundsMax = new Vector3(float.MinValue);

        foreach (var mesh in data.Meshes)
        {
            foreach (var vertex in mesh.Vertices)
            {
                boundsMin = Vector3.Min(boundsMin, vertex.Position);
                boundsMax = Vector3.Max(boundsMax, vertex.Position);
            }
        }

        float radius = Vector3.Distance(boundsMin, boundsMax) * 0.5f;
        
        // If model is extremely small, scale it up
        if (radius < 0.1f && radius > 0.0001f)
        {
            float scale = 100.0f;
            boundsMin *= scale;
            boundsMax *= scale;
            radius *= scale;
            
            // Scale all vertices
            foreach (var mesh in data.Meshes)
            {
                for (int i = 0; i < mesh.Vertices.Length; i++)
                {
                    mesh.Vertices[i].Position *= scale;
                }
            }
        }

        data.BoundsMin = boundsMin;
        data.BoundsMax = boundsMax;
        data.Radius = radius;
    }
}
