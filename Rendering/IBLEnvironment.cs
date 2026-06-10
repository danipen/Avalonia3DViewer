using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace Avalonia3DViewer.Rendering;

public class IBLEnvironment : IDisposable
{
    private readonly GL _gl;
    
    public uint EnvironmentMap { get; private set; }
    public uint IrradianceMap { get; private set; }
    public uint PrefilterMap { get; private set; }
    public uint BrdfLUT { get; private set; }

    private Shader? _equirectToCubemapShader;
    private Shader? _irradianceShader;
    private Shader? _prefilterShader;
    private Shader? _brdfShader;
    
    private Mesh? _cube;
    private uint _captureFBO;
    private uint _captureRBO;
    private uint _quadVAO;
    private uint _quadVBO;

    private const uint EnvCubemapSize = 1024;
    private const uint IrradianceSize = 64;
    private const uint PrefilterSize = 256;
    private const uint PrefilterMipLevels = 8;
    private const uint BrdfLutSize = 512;

    private static readonly Matrix4x4[] CaptureViews =
    {
        Matrix4x4.CreateLookAt(Vector3.Zero, new Vector3(1, 0, 0), new Vector3(0, -1, 0)),
        Matrix4x4.CreateLookAt(Vector3.Zero, new Vector3(-1, 0, 0), new Vector3(0, -1, 0)),
        Matrix4x4.CreateLookAt(Vector3.Zero, new Vector3(0, 1, 0), new Vector3(0, 0, 1)),
        Matrix4x4.CreateLookAt(Vector3.Zero, new Vector3(0, -1, 0), new Vector3(0, 0, -1)),
        Matrix4x4.CreateLookAt(Vector3.Zero, new Vector3(0, 0, 1), new Vector3(0, -1, 0)),
        Matrix4x4.CreateLookAt(Vector3.Zero, new Vector3(0, 0, -1), new Vector3(0, -1, 0))
    };

    public IBLEnvironment(GL gl)
    {
        _gl = gl;
        InitializeFramebuffers();
        CreateDummyTextures();

        // The BRDF LUT does not depend on the environment map, so bake the real
        // one immediately. Using a dummy (white) LUT here badly over-brightens
        // IBL specular: brdf.y would add a constant 1.0 to the Fresnel term.
        _brdfShader = new Shader(_gl, "Shaders/brdf.vert", "Shaders/brdf.frag");
        BrdfLUT = GenerateBrdfLut(BrdfLutSize);
    }

    private void InitializeFramebuffers()
    {
        _captureFBO = _gl.GenFramebuffer();
        _captureRBO = _gl.GenRenderbuffer();

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _captureFBO);
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _captureRBO);
        _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, 512, 512);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, 
            RenderbufferTarget.Renderbuffer, _captureRBO);
    }

    private unsafe void CreateDummyTextures()
    {
        // NOTE: On ANGLE/GLES3 (D3D11 backend), RGB16F is frequently unsupported while RGBA16F is supported.
        // Using RGBA16F keeps it portable; shaders sample .rgb and ignore alpha.
        float[] blackPixel = { 0.0f, 0.0f, 0.0f, 1.0f };

        IrradianceMap = CreateDummyCubemap(blackPixel);
        PrefilterMap = CreateDummyCubemap(blackPixel);
    }

    private unsafe uint CreateDummyCubemap(float[] pixel)
    {
        uint cubemap = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.TextureCubeMap, cubemap);
        
        fixed (float* ptr = pixel)
        {
            for (uint i = 0; i < 6; i++)
            {
                _gl.TexImage2D(TextureTarget.TextureCubeMapPositiveX + (int)i, 0, (int)InternalFormat.Rgba16f,
                    1, 1, 0, PixelFormat.Rgba, PixelType.Float, ptr);
            }
        }

        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);

        return cubemap;
    }

    public void LoadEnvironment(string hdriPath)
    {
        DisposeEnvironmentResources();

        _equirectToCubemapShader = new Shader(_gl, "Shaders/cubemap.vert", "Shaders/equirect_to_cubemap.frag");
        _irradianceShader = new Shader(_gl, "Shaders/cubemap.vert", "Shaders/irradiance_convolution.frag");
        _prefilterShader = new Shader(_gl, "Shaders/cubemap.vert", "Shaders/prefilter.frag");

        _cube = Mesh.CreateCube(_gl);

        using var hdrTexture = Texture.LoadHDR(_gl, hdriPath);

        // The capture cube is rendered from the inside, so back-face culling
        // (enabled by the viewport) would discard everything.
        bool cullWasEnabled = _gl.IsEnabled(EnableCap.CullFace);
        _gl.Disable(EnableCap.CullFace);

        try
        {
            EnvironmentMap = CreateCubemap(EnvCubemapSize, mipmap: true);
            ConvertEquirectangularToCubemap(hdrTexture.Handle, EnvironmentMap, EnvCubemapSize);

            _gl.BindTexture(TextureTarget.TextureCubeMap, EnvironmentMap);
            _gl.GenerateMipmap(TextureTarget.TextureCubeMap);

            IrradianceMap = CreateCubemap(IrradianceSize);
            GenerateIrradianceMap(EnvironmentMap, IrradianceMap, IrradianceSize);

            PrefilterMap = CreateCubemap(PrefilterSize, mipmap: true);
            // Only mips 0..(PrefilterMipLevels-1) are rendered; clamp sampling so the
            // auto-generated (uninitialized) tail mips are never read.
            _gl.BindTexture(TextureTarget.TextureCubeMap, PrefilterMap);
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMaxLevel, (int)PrefilterMipLevels - 1);
            GeneratePrefilterMap(EnvironmentMap, PrefilterMap, PrefilterSize);
        }
        finally
        {
            if (cullWasEnabled)
                _gl.Enable(EnableCap.CullFace);
        }
    }

    private void DisposeEnvironmentResources()
    {
        if (EnvironmentMap != 0) { _gl.DeleteTexture(EnvironmentMap); EnvironmentMap = 0; }
        if (IrradianceMap != 0) { _gl.DeleteTexture(IrradianceMap); IrradianceMap = 0; }
        if (PrefilterMap != 0) { _gl.DeleteTexture(PrefilterMap); PrefilterMap = 0; }

        _equirectToCubemapShader?.Dispose(); _equirectToCubemapShader = null;
        _irradianceShader?.Dispose(); _irradianceShader = null;
        _prefilterShader?.Dispose(); _prefilterShader = null;

        _cube?.Dispose(); _cube = null;
    }

    private unsafe uint CreateCubemap(uint resolution, bool mipmap = false)
    {
        uint cubemap = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.TextureCubeMap, cubemap);

        for (uint i = 0; i < 6; i++)
        {
            _gl.TexImage2D(TextureTarget.TextureCubeMapPositiveX + (int)i, 0, (int)InternalFormat.Rgba16f,
                resolution, resolution, 0, PixelFormat.Rgba, PixelType.Float, null);
        }

        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)GLEnum.ClampToEdge);

        if (mipmap)
        {
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
            _gl.GenerateMipmap(TextureTarget.TextureCubeMap);
        }
        else
        {
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        }

        return cubemap;
    }

    private void ConvertEquirectangularToCubemap(uint equirectangularMap, uint cubemap, uint resolution)
    {
        _equirectToCubemapShader!.Use();
        _equirectToCubemapShader.SetUniform("equirectangularMap", 0);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, equirectangularMap);

        var captureProjection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2.0f, 1.0f, 0.1f, 10.0f);
        _equirectToCubemapShader.SetUniform("projection", captureProjection);

        RenderToCubemapFaces(cubemap, resolution, view => _equirectToCubemapShader.SetUniform("view", view));
    }

    private void GenerateIrradianceMap(uint environmentMap, uint irradianceMap, uint resolution)
    {
        _irradianceShader!.Use();
        _irradianceShader.SetUniform("environmentMap", 0);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.TextureCubeMap, environmentMap);

        var captureProjection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2.0f, 1.0f, 0.1f, 10.0f);
        _irradianceShader.SetUniform("projection", captureProjection);

        // RenderToCubemapFaces resizes the shared depth renderbuffer itself.
        RenderToCubemapFaces(irradianceMap, resolution, view => _irradianceShader.SetUniform("view", view));
    }

    private void GeneratePrefilterMap(uint environmentMap, uint prefilterMap, uint resolution)
    {
        _prefilterShader!.Use();
        _prefilterShader.SetUniform("environmentMap", 0);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.TextureCubeMap, environmentMap);

        var captureProjection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2.0f, 1.0f, 0.1f, 10.0f);
        _prefilterShader.SetUniform("projection", captureProjection);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _captureFBO);

        for (uint mip = 0; mip < PrefilterMipLevels; mip++)
        {
            uint mipWidth = Math.Max(1, (uint)(resolution * MathF.Pow(0.5f, mip)));
            uint mipHeight = Math.Max(1, (uint)(resolution * MathF.Pow(0.5f, mip)));

            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _captureRBO);
            _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, mipWidth, mipHeight);
            _gl.Viewport(0, 0, mipWidth, mipHeight);

            float roughness = (float)mip / (PrefilterMipLevels - 1);
            _prefilterShader.SetUniform("roughness", roughness);
            _prefilterShader.SetUniform("resolution", (float)EnvCubemapSize);

            for (int face = 0; face < 6; face++)
            {
                _prefilterShader.SetUniform("view", CaptureViews[face]);
                _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                    TextureTarget.TextureCubeMapPositiveX + face, prefilterMap, (int)mip);

                _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                _cube!.Draw();
            }
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private void RenderToCubemapFaces(uint cubemap, uint resolution, Action<Matrix4x4> setView)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _captureFBO);

        // Make sure the shared depth renderbuffer matches the target resolution
        // (it may be left at a different size by a previous capture pass).
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _captureRBO);
        _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, resolution, resolution);

        _gl.Viewport(0, 0, resolution, resolution);

        for (int i = 0; i < 6; i++)
        {
            setView(CaptureViews[i]);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.TextureCubeMapPositiveX + i, cubemap, 0);

            _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            _cube!.Draw();
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private unsafe uint GenerateBrdfLut(uint resolution)
    {
        uint brdfLUT = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, brdfLUT);

        // RGBA16F: portable across desktop GL and ANGLE/GLES3 (RG16F is not
        // reliably color-renderable on ES). The shader writes (A, B, 0, 1).
        _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba16f, resolution, resolution, 0,
            PixelFormat.Rgba, PixelType.Float, null);

        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _captureFBO);
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _captureRBO);
        _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, resolution, resolution);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, 
            TextureTarget.Texture2D, brdfLUT, 0);

        _gl.Viewport(0, 0, resolution, resolution);
        _brdfShader!.Use();
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        RenderQuad();

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        return brdfLUT;
    }

    private unsafe void RenderQuad()
    {
        if (_quadVAO == 0)
        {
            float[] quadVertices = {
                // pos        // uv
                -1.0f,  1.0f, 0.0f, 1.0f,
                -1.0f, -1.0f, 0.0f, 0.0f,
                 1.0f,  1.0f, 1.0f, 1.0f,
                 1.0f, -1.0f, 1.0f, 0.0f,
            };

            _quadVAO = _gl.GenVertexArray();
            _quadVBO = _gl.GenBuffer();
            _gl.BindVertexArray(_quadVAO);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _quadVBO);

            fixed (float* v = quadVertices)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quadVertices.Length * sizeof(float)), v, BufferUsageARB.StaticDraw);
            }

            // Attribute conventions match Shader.cs bindings: 0 = aPos, 2 = aTexCoord.
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)0);
            _gl.EnableVertexAttribArray(2);
            _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)(2 * sizeof(float)));
        }

        _gl.BindVertexArray(_quadVAO);
        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        _gl.BindVertexArray(0);
    }

    public void Dispose()
    {
        DisposeEnvironmentResources();

        if (BrdfLUT != 0) { _gl.DeleteTexture(BrdfLUT); BrdfLUT = 0; }
        _brdfShader?.Dispose(); _brdfShader = null;

        if (_captureFBO != 0) { _gl.DeleteFramebuffer(_captureFBO); _captureFBO = 0; }
        if (_captureRBO != 0) { _gl.DeleteRenderbuffer(_captureRBO); _captureRBO = 0; }
        if (_quadVAO != 0) { _gl.DeleteVertexArray(_quadVAO); _quadVAO = 0; }
        if (_quadVBO != 0) { _gl.DeleteBuffer(_quadVBO); _quadVBO = 0; }
    }
}
