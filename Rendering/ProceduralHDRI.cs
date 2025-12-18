using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace Avalonia3DViewer.Rendering;

public class ProceduralHDRI : IDisposable
{
    private readonly GL _gl;
    private uint _envCubemap;
    private uint _irradianceMap;
    private uint _prefilterMap;
    
    private const int EnvSize = 512;
    private const int IrradianceSize = 32;
    private const int PrefilterSize = 128;

    public uint EnvironmentMap => _envCubemap;
    public uint IrradianceMap => _irradianceMap;
    public uint PrefilterMap => _prefilterMap;

    public ProceduralHDRI(GL gl)
    {
        _gl = gl;
        GenerateProceduralSky();
    }

    private void GenerateProceduralSky()
    {
        _envCubemap = CreateCubemapWithData(EnvSize, face => GenerateSkyGradient(face));
        _irradianceMap = CreateCubemapWithData(IrradianceSize, GenerateIrradiance);
        _prefilterMap = CreateCubemapWithMipmaps(PrefilterSize, face => GenerateSkyGradient(face, 0.5f));
    }

    private uint CreateCubemapWithData(int size, Func<int, Vector3[]> generateFaceData)
    {
        uint cubemap = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.TextureCubeMap, cubemap);
        
        for (int face = 0; face < 6; face++)
        {
            Vector3[] colors = generateFaceData(face);
            UploadCubemapFace(face, size, colors);
        }
        
        SetCubemapParameters(mipmap: false);
        return cubemap;
    }

    private uint CreateCubemapWithMipmaps(int size, Func<int, Vector3[]> generateFaceData)
    {
        uint cubemap = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.TextureCubeMap, cubemap);
        
        for (int face = 0; face < 6; face++)
        {
            Vector3[] colors = generateFaceData(face);
            UploadCubemapFace(face, size, colors);
        }
        
        SetCubemapParameters(mipmap: true);
        _gl.GenerateMipmap(TextureTarget.TextureCubeMap);
        return cubemap;
    }

    private unsafe void UploadCubemapFace(int face, int size, Vector3[] colors)
    {
        // ANGLE/GLES3 commonly supports RGBA16F but not RGB16F. Upload as RGBA16F with alpha=1.
        var rgba = new Vector4[colors.Length];
        for (int i = 0; i < colors.Length; i++)
            rgba[i] = new Vector4(colors[i], 1.0f);

        fixed (Vector4* ptr = rgba)
        {
            _gl.TexImage2D(TextureTarget.TextureCubeMapPositiveX + face, 0, (int)InternalFormat.Rgba16f,
                (uint)size, (uint)size, 0, PixelFormat.Rgba, PixelType.Float, ptr);
        }
    }

    private void SetCubemapParameters(bool mipmap)
    {
        int minFilter = mipmap ? (int)GLEnum.LinearMipmapLinear : (int)GLEnum.Linear;
        
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, minFilter);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)GLEnum.ClampToEdge);
    }

    private Vector3[] GenerateSkyGradient(int face, float intensity = 1.0f)
    {
        int size = intensity < 1.0f ? PrefilterSize : EnvSize;
        var colors = new Vector3[size * size];
        
        var skyTop = new Vector3(0.4f, 0.45f, 0.55f) * intensity;
        var skyHorizon = new Vector3(0.7f, 0.7f, 0.72f) * intensity;
        var skyBottom = new Vector3(0.15f, 0.15f, 0.18f) * intensity;
        var sunDir = Vector3.Normalize(new Vector3(-0.3f, -0.7f, -0.4f));
        
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size;
                float v = (y + 0.5f) / size;
                
                Vector3 dir = GetCubemapDirection(face, u, v);
                float t = (dir.Y + 1.0f) * 0.5f;
                
                Vector3 color = t > 0.5f
                    ? Vector3.Lerp(skyHorizon, skyTop, (t - 0.5f) * 2.0f)
                    : Vector3.Lerp(skyBottom, skyHorizon, t * 2.0f);
                
                color = AddSunGlow(color, dir, sunDir, intensity);
                colors[y * size + x] = color;
            }
        }
        
        return colors;
    }

    private static Vector3 AddSunGlow(Vector3 color, Vector3 dir, Vector3 sunDir, float intensity)
    {
        float sunDot = Math.Max(0, Vector3.Dot(dir, -sunDir));
        float sunGlow = MathF.Pow(sunDot, 64.0f) * 8.0f * intensity;
        float sunHalo = MathF.Pow(sunDot, 8.0f) * 1.5f * intensity;
        
        color += new Vector3(1.0f, 0.95f, 0.85f) * sunGlow;
        color += new Vector3(1.0f, 0.98f, 0.92f) * sunHalo;
        return color;
    }

    private Vector3[] GenerateIrradiance(int face)
    {
        var colors = new Vector3[IrradianceSize * IrradianceSize];
        
        var skyColor = new Vector3(0.5f, 0.52f, 0.55f);
        var groundColor = new Vector3(0.15f, 0.14f, 0.13f);
        var horizonColor = new Vector3(0.4f, 0.4f, 0.42f);
        
        for (int y = 0; y < IrradianceSize; y++)
        {
            for (int x = 0; x < IrradianceSize; x++)
            {
                float u = (x + 0.5f) / IrradianceSize;
                float v = (y + 0.5f) / IrradianceSize;
                
                Vector3 dir = GetCubemapDirection(face, u, v);
                float t = dir.Y;
                
                colors[y * IrradianceSize + x] = t > 0
                    ? Vector3.Lerp(horizonColor, skyColor, t)
                    : Vector3.Lerp(horizonColor, groundColor, -t);
            }
        }
        
        return colors;
    }

    private static Vector3 GetCubemapDirection(int face, float u, float v)
    {
        float s = u * 2.0f - 1.0f;
        float t = v * 2.0f - 1.0f;
        
        Vector3 dir = face switch
        {
            0 => new Vector3(1, -t, -s),
            1 => new Vector3(-1, -t, s),
            2 => new Vector3(s, 1, t),
            3 => new Vector3(s, -1, -t),
            4 => new Vector3(s, -t, 1),
            5 => new Vector3(-s, -t, -1),
            _ => new Vector3(1, 0, 0)
        };
        
        return Vector3.Normalize(dir);
    }

    public void Dispose()
    {
        _gl.DeleteTexture(_envCubemap);
        _gl.DeleteTexture(_irradianceMap);
        _gl.DeleteTexture(_prefilterMap);
    }
}
