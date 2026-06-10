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
    // 128 -> 1 px: 8 mip levels (0..7), matching MAX_REFLECTION_LOD = 7.0 in pbr.frag.
    private const int PrefilterMipLevels = 8;

    public uint EnvironmentMap => _envCubemap;
    public uint IrradianceMap => _irradianceMap;
    public uint PrefilterMap => _prefilterMap;

    private static readonly Matrix4x4[] CaptureViews =
    {
        Matrix4x4.CreateLookAt(Vector3.Zero, new Vector3(1, 0, 0), new Vector3(0, -1, 0)),
        Matrix4x4.CreateLookAt(Vector3.Zero, new Vector3(-1, 0, 0), new Vector3(0, -1, 0)),
        Matrix4x4.CreateLookAt(Vector3.Zero, new Vector3(0, 1, 0), new Vector3(0, 0, 1)),
        Matrix4x4.CreateLookAt(Vector3.Zero, new Vector3(0, -1, 0), new Vector3(0, 0, -1)),
        Matrix4x4.CreateLookAt(Vector3.Zero, new Vector3(0, 0, 1), new Vector3(0, -1, 0)),
        Matrix4x4.CreateLookAt(Vector3.Zero, new Vector3(0, 0, -1), new Vector3(0, -1, 0))
    };

    public ProceduralHDRI(GL gl)
    {
        _gl = gl;
        GenerateProceduralSky();
    }

    private void GenerateProceduralSky()
    {
        // Analytic sky -> environment cubemap. Mipmaps are required because the
        // prefilter pass importance-samples it via textureLod to avoid fireflies.
        _envCubemap = CreateCubemapWithMipmaps(EnvSize, face => GenerateSkyGradient(face, EnvSize));

        // Convolve the sky with the SAME GPU passes used for loaded HDRIs.
        //
        // IMPORTANT: auto-generated box mipmaps are NOT a substitute for GGX
        // prefiltering. They blur far less than the GGX lobe at equivalent mip
        // levels, so the sun hotspot stays concentrated and mid-roughness
        // materials pick up 2-3x too much specular energy (everything looks
        // glossy/oily). Likewise, a hand-painted gradient is not a substitute
        // for the cosine-convolved irradiance: it misses the sun's diffuse
        // energy entirely (~1.5x too dark), further inflating the
        // specular-to-diffuse ratio.
        BakeIblMaps();
    }

    /// <summary>
    /// Runs the irradiance cosine convolution and the GGX specular prefilter
    /// over the procedural environment cubemap, mirroring IBLEnvironment.
    /// Baking resources are created and destroyed here: the procedural sky is
    /// static, so this runs exactly once.
    /// </summary>
    private void BakeIblMaps()
    {
        Shader? irradianceShader = null;
        Shader? prefilterShader = null;
        Mesh? cube = null;
        uint fbo = 0;
        uint rbo = 0;

        // The capture cube is rendered from the inside; back-face culling
        // (enabled by the viewport) would discard everything.
        bool cullWasEnabled = _gl.IsEnabled(EnableCap.CullFace);
        _gl.Disable(EnableCap.CullFace);

        try
        {
            irradianceShader = new Shader(_gl, "Shaders/cubemap.vert", "Shaders/irradiance_convolution.frag");
            prefilterShader = new Shader(_gl, "Shaders/cubemap.vert", "Shaders/prefilter.frag");
            cube = Mesh.CreateCube(_gl);

            fbo = _gl.GenFramebuffer();
            rbo = _gl.GenRenderbuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, rbo);
            _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                RenderbufferTarget.Renderbuffer, rbo);

            var captureProjection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2.0f, 1.0f, 0.1f, 10.0f);

            // --- Diffuse irradiance (cosine convolution of the actual sky + sun) ---
            _irradianceMap = CreateEmptyCubemap(IrradianceSize, mipmap: false);

            irradianceShader.Use();
            irradianceShader.SetUniform("environmentMap", 0);
            irradianceShader.SetUniform("projection", captureProjection);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.TextureCubeMap, _envCubemap);

            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, rbo);
            _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24,
                (uint)IrradianceSize, (uint)IrradianceSize);
            _gl.Viewport(0, 0, (uint)IrradianceSize, (uint)IrradianceSize);

            for (int face = 0; face < 6; face++)
            {
                irradianceShader.SetUniform("view", CaptureViews[face]);
                _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                    TextureTarget.TextureCubeMapPositiveX + face, _irradianceMap, 0);

                _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                cube.Draw();
            }

            // --- Specular prefilter (GGX convolution, one roughness per mip) ---
            _prefilterMap = CreateEmptyCubemap(PrefilterSize, mipmap: true);
            // 128px base -> full chain is exactly mips 0..7, all rendered below.
            // The MaxLevel clamp just makes that contract explicit (and keeps the
            // code symmetric with IBLEnvironment, where the clamp is load-bearing).
            _gl.BindTexture(TextureTarget.TextureCubeMap, _prefilterMap);
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMaxLevel, PrefilterMipLevels - 1);

            prefilterShader.Use();
            prefilterShader.SetUniform("environmentMap", 0);
            prefilterShader.SetUniform("projection", captureProjection);
            prefilterShader.SetUniform("resolution", (float)EnvSize);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.TextureCubeMap, _envCubemap);

            for (int mip = 0; mip < PrefilterMipLevels; mip++)
            {
                uint mipSize = Math.Max(1u, (uint)(PrefilterSize >> mip));

                _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, rbo);
                _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24,
                    mipSize, mipSize);
                _gl.Viewport(0, 0, mipSize, mipSize);

                float roughness = (float)mip / (PrefilterMipLevels - 1);
                prefilterShader.SetUniform("roughness", roughness);

                for (int face = 0; face < 6; face++)
                {
                    prefilterShader.SetUniform("view", CaptureViews[face]);
                    _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                        TextureTarget.TextureCubeMapPositiveX + face, _prefilterMap, mip);

                    _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                    cube.Draw();
                }
            }
        }
        finally
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            if (fbo != 0) _gl.DeleteFramebuffer(fbo);
            if (rbo != 0) _gl.DeleteRenderbuffer(rbo);
            cube?.Dispose();
            prefilterShader?.Dispose();
            irradianceShader?.Dispose();

            if (cullWasEnabled)
                _gl.Enable(EnableCap.CullFace);
        }
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

    /// <summary>
    /// Allocates an RGBA16F cubemap render target. When mipmap is true the full
    /// mip chain is allocated (via GenerateMipmap) so individual mips can be
    /// attached as framebuffer color targets.
    /// </summary>
    private unsafe uint CreateEmptyCubemap(int size, bool mipmap)
    {
        uint cubemap = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.TextureCubeMap, cubemap);

        for (int face = 0; face < 6; face++)
        {
            _gl.TexImage2D(TextureTarget.TextureCubeMapPositiveX + face, 0, (int)InternalFormat.Rgba16f,
                (uint)size, (uint)size, 0, PixelFormat.Rgba, PixelType.Float, null);
        }

        SetCubemapParameters(mipmap);
        if (mipmap)
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

    private Vector3[] GenerateSkyGradient(int face, int size)
    {
        var colors = new Vector3[size * size];

        var skyTop = new Vector3(0.4f, 0.45f, 0.55f);
        var skyHorizon = new Vector3(0.7f, 0.7f, 0.72f);
        var skyBottom = new Vector3(0.15f, 0.15f, 0.18f);
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

                color = AddSunGlow(color, dir, sunDir, 1.0f);
                colors[y * size + x] = color;
            }
        }

        return colors;
    }

    private static Vector3 AddSunGlow(Vector3 color, Vector3 dir, Vector3 sunDir, float intensity)
    {
        // Keep the sun hotspot moderate: very glossy materials (low roughness)
        // mirror this directly, and an 8x white blob reads as ugly bright
        // splotches on smooth surfaces. Direct lights provide the crisp
        // highlights instead.
        float sunDot = Math.Max(0, Vector3.Dot(dir, -sunDir));
        float sunGlow = MathF.Pow(sunDot, 64.0f) * 3.0f * intensity;
        float sunHalo = MathF.Pow(sunDot, 8.0f) * 0.8f * intensity;

        color += new Vector3(1.0f, 0.95f, 0.85f) * sunGlow;
        color += new Vector3(1.0f, 0.98f, 0.92f) * sunHalo;
        return color;
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
