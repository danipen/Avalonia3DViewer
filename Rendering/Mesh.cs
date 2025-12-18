using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace Avalonia3DViewer.Rendering;

public struct Vertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 TexCoord;
    public Vector3 Tangent;
    public Vector3 Bitangent;
}

public class Mesh : IDisposable
{
    private readonly GL _gl;
    private uint _vao;
    private uint _vbo;
    private uint _ebo;
    private readonly int _indexCount;
    private readonly DrawElementsType _indexType;
    private Vertex[]? _vertices;
    private bool _disposed;
    
    private Vector3 _boundsMin;
    private Vector3 _boundsMax;
    private bool _boundsCalculated;
    
    public int IndexCount => _indexCount;
    public int MaterialIndex { get; set; }

    public Mesh(GL gl, Vertex[] vertices, uint[] indices, int materialIndex = 0, bool keepVertices = false)
    {
        _gl = gl;
        _indexCount = indices.Length;
        MaterialIndex = materialIndex;

        CalculateAndCacheBounds(vertices);
        _vertices = keepVertices ? vertices : null;

        // Prefer 16-bit indices when possible (more compatible on GLES2 and smaller).
        // We decide index type up-front so DrawElements uses the correct type.
        _indexType = ChooseIndexType(gl, indices, out var indicesU16);

        try
        {
            CreateBuffers(vertices, indices, indicesU16);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Mesh] ERROR creating mesh: {ex.Message}");
            throw;
        }
    }

    private void CreateBuffers(Vertex[] vertices, uint[] indicesU32, ushort[]? indicesU16)
    {
        DrainGlErrors("[Mesh] Pre-existing OpenGL error before mesh creation");

        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();
        _ebo = _gl.GenBuffer();

        _gl.BindVertexArray(_vao);

        UploadVertexData(vertices);
        if (_indexType == DrawElementsType.UnsignedShort && indicesU16 != null)
            UploadIndexData(indicesU16);
        else
            UploadIndexData(indicesU32);
        SetupVertexAttributes();

        _gl.BindVertexArray(0);
        
        DrainGlErrors("[Mesh] OpenGL error after mesh creation");
    }

    private void UploadVertexData(Vertex[] vertices)
    {
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        unsafe
        {
            fixed (Vertex* v = vertices)
            {
                var size = (nuint)(vertices.Length * sizeof(Vertex));
                _gl.BufferData(BufferTargetARB.ArrayBuffer, size, v, BufferUsageARB.StaticDraw);
            }
        }
    }

    private void UploadIndexData(uint[] indices)
    {
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        unsafe
        {
            fixed (uint* i = indices)
            {
                var size = (nuint)(indices.Length * sizeof(uint));
                _gl.BufferData(BufferTargetARB.ElementArrayBuffer, size, i, BufferUsageARB.StaticDraw);
            }
        }
    }

    private void UploadIndexData(ushort[] indices)
    {
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        unsafe
        {
            fixed (ushort* i = indices)
            {
                var size = (nuint)(indices.Length * sizeof(ushort));
                _gl.BufferData(BufferTargetARB.ElementArrayBuffer, size, i, BufferUsageARB.StaticDraw);
            }
        }
    }

    private unsafe void SetupVertexAttributes()
    {
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, (uint)sizeof(Vertex), (void*)0);

        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, (uint)sizeof(Vertex), (void*)sizeof(Vector3));

        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, (uint)sizeof(Vertex), (void*)(sizeof(Vector3) * 2));

        _gl.EnableVertexAttribArray(3);
        _gl.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, (uint)sizeof(Vertex), (void*)(sizeof(Vector3) * 2 + sizeof(Vector2)));

        _gl.EnableVertexAttribArray(4);
        _gl.VertexAttribPointer(4, 3, VertexAttribPointerType.Float, false, (uint)sizeof(Vertex), (void*)(sizeof(Vector3) * 3 + sizeof(Vector2)));
    }

    private void DrainGlErrors(string label)
    {
        // Important: glGetError returns and clears one error at a time.
        // Drain to avoid stale errors being reported much later (which can be misleading).
        int drained = 0;
        while (drained < 32)
        {
            var error = _gl.GetError();
            if (error == GLEnum.NoError)
                return;
            drained++;
            Console.WriteLine($"{label}: {error}");
        }
    }

    public void Draw()
    {
        _gl.BindVertexArray(_vao);
        unsafe
        {
            _gl.DrawElements(PrimitiveType.Triangles, (uint)_indexCount, _indexType, (void*)0);
        }
        _gl.BindVertexArray(0);
    }

    public void Scale(float scale)
    {
        if (_vertices == null)
        {
            Console.WriteLine("[Mesh] WARNING: Scale() called but vertices were not kept in memory.");
            return;
        }
        
        for (int i = 0; i < _vertices.Length; i++)
            _vertices[i].Position *= scale;
        
        _boundsMin *= scale;
        _boundsMax *= scale;
        
        UploadVertexData(_vertices);
    }

    public (Vector3 Min, Vector3 Max) GetBounds()
    {
        if (_boundsCalculated)
            return (_boundsMin, _boundsMax);
            
        if (_vertices == null || _vertices.Length == 0)
            return (Vector3.Zero, Vector3.Zero);

        CalculateAndCacheBounds(_vertices);
        return (_boundsMin, _boundsMax);
    }
    
    private void CalculateAndCacheBounds(Vertex[] vertices)
    {
        if (vertices == null || vertices.Length == 0)
        {
            _boundsMin = Vector3.Zero;
            _boundsMax = Vector3.Zero;
            _boundsCalculated = true;
            return;
        }

        _boundsMin = new Vector3(float.MaxValue);
        _boundsMax = new Vector3(float.MinValue);

        foreach (var vertex in vertices)
        {
            _boundsMin = Vector3.Min(_boundsMin, vertex.Position);
            _boundsMax = Vector3.Max(_boundsMax, vertex.Position);
        }
        
        _boundsCalculated = true;
    }

    public void ClearCpuVertexData() => _vertices = null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_vao != 0) _gl.DeleteVertexArray(_vao);
        if (_vbo != 0) _gl.DeleteBuffer(_vbo);
        if (_ebo != 0) _gl.DeleteBuffer(_ebo);
        _vao = 0;
        _vbo = 0;
        _ebo = 0;
        _vertices = null;
    }

    public static Mesh CreateCube(GL gl)
    {
        var vertices = new Vertex[]
        {
            new() { Position = new(-1, -1, -1), Normal = new(0, 0, -1), TexCoord = new(0, 0) },
            new() { Position = new(1, -1, -1), Normal = new(0, 0, -1), TexCoord = new(1, 0) },
            new() { Position = new(1, 1, -1), Normal = new(0, 0, -1), TexCoord = new(1, 1) },
            new() { Position = new(-1, 1, -1), Normal = new(0, 0, -1), TexCoord = new(0, 1) },
            
            new() { Position = new(-1, -1, 1), Normal = new(0, 0, 1), TexCoord = new(0, 0) },
            new() { Position = new(1, -1, 1), Normal = new(0, 0, 1), TexCoord = new(1, 0) },
            new() { Position = new(1, 1, 1), Normal = new(0, 0, 1), TexCoord = new(1, 1) },
            new() { Position = new(-1, 1, 1), Normal = new(0, 0, 1), TexCoord = new(0, 1) },
            
            new() { Position = new(-1, -1, -1), Normal = new(-1, 0, 0), TexCoord = new(0, 0) },
            new() { Position = new(-1, -1, 1), Normal = new(-1, 0, 0), TexCoord = new(1, 0) },
            new() { Position = new(-1, 1, 1), Normal = new(-1, 0, 0), TexCoord = new(1, 1) },
            new() { Position = new(-1, 1, -1), Normal = new(-1, 0, 0), TexCoord = new(0, 1) },
            
            new() { Position = new(1, -1, -1), Normal = new(1, 0, 0), TexCoord = new(0, 0) },
            new() { Position = new(1, -1, 1), Normal = new(1, 0, 0), TexCoord = new(1, 0) },
            new() { Position = new(1, 1, 1), Normal = new(1, 0, 0), TexCoord = new(1, 1) },
            new() { Position = new(1, 1, -1), Normal = new(1, 0, 0), TexCoord = new(0, 1) },
            
            new() { Position = new(-1, -1, -1), Normal = new(0, -1, 0), TexCoord = new(0, 0) },
            new() { Position = new(1, -1, -1), Normal = new(0, -1, 0), TexCoord = new(1, 0) },
            new() { Position = new(1, -1, 1), Normal = new(0, -1, 0), TexCoord = new(1, 1) },
            new() { Position = new(-1, -1, 1), Normal = new(0, -1, 0), TexCoord = new(0, 1) },
            
            new() { Position = new(-1, 1, -1), Normal = new(0, 1, 0), TexCoord = new(0, 0) },
            new() { Position = new(1, 1, -1), Normal = new(0, 1, 0), TexCoord = new(1, 0) },
            new() { Position = new(1, 1, 1), Normal = new(0, 1, 0), TexCoord = new(1, 1) },
            new() { Position = new(-1, 1, 1), Normal = new(0, 1, 0), TexCoord = new(0, 1) },
        };

        var indices = new uint[]
        {
            0, 1, 2, 2, 3, 0,
            4, 5, 6, 6, 7, 4,
            8, 9, 10, 10, 11, 8,
            12, 13, 14, 14, 15, 12,
            16, 17, 18, 18, 19, 16,
            20, 21, 22, 22, 23, 20
        };

        return new Mesh(gl, vertices, indices);
    }

    private static DrawElementsType ChooseIndexType(GL gl, uint[] indices, out ushort[]? indicesU16)
    {
        indicesU16 = null;

        uint max = 0;
        for (int i = 0; i < indices.Length; i++)
            if (indices[i] > max) max = indices[i];

        if (max <= ushort.MaxValue)
        {
            indicesU16 = new ushort[indices.Length];
            for (int i = 0; i < indices.Length; i++)
                indicesU16[i] = (ushort)indices[i];
            return DrawElementsType.UnsignedShort;
        }

        // If indices don't fit 16-bit, fall back to uint indices.
        // Note: On GLES2 this requires GL_OES_element_index_uint; on GLES3 it's core.
        // We don't hard-fail here because some runtimes provide the extension even on ES2.
        return DrawElementsType.UnsignedInt;
    }
}
