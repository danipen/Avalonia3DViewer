using System;
using System.Numerics;
using Silk.NET.OpenGL;
using Avalonia3DViewer.Rendering;
using ShaderProgram = Avalonia3DViewer.Rendering.Shader;

namespace Avalonia3DViewer.PostProcessing;

public class SSAOEffect : IDisposable
{
    private readonly GL _gl;
    private ShaderProgram _ssaoShader;
    private ShaderProgram _blurShader;
    
    private uint _ssaoFBO;
    private uint _ssaoBlurFBO;
    private uint _ssaoColorBuffer;
    private uint _ssaoColorBufferBlur;
    
    private uint _noiseTexture;
    private readonly Vector3[] _ssaoKernel = new Vector3[64];
    
    private int _width;
    private int _height;

    private const int KernelSize = 64;
    private const int NoiseSize = 4;

    public uint SSAOTexture => _ssaoColorBufferBlur;

    public SSAOEffect(GL gl, int width, int height)
    {
        _gl = gl;
        _width = width;
        _height = height;
        
        _ssaoShader = new ShaderProgram(_gl, "Shaders/screen_quad.vert", "Shaders/ssao.frag");
        _blurShader = new ShaderProgram(_gl, "Shaders/screen_quad.vert", "Shaders/ssao_blur.frag");
        
        GenerateSsaoKernel();
        GenerateNoiseTexture();
        CreateFramebuffers();
    }

    private void GenerateSsaoKernel()
    {
        var random = new Random();
        
        for (int i = 0; i < KernelSize; ++i)
        {
            var sample = new Vector3(
                (float)random.NextDouble() * 2.0f - 1.0f,
                (float)random.NextDouble() * 2.0f - 1.0f,
                (float)random.NextDouble()
            );
            sample = Vector3.Normalize(sample) * (float)random.NextDouble();
            
            float scale = (float)i / KernelSize;
            scale = Lerp(0.1f, 1.0f, scale * scale);
            _ssaoKernel[i] = sample * scale;
        }
    }

    private void GenerateNoiseTexture()
    {
        var random = new Random();
        var ssaoNoise = new Vector3[NoiseSize * NoiseSize];
        
        for (int i = 0; i < ssaoNoise.Length; i++)
        {
            ssaoNoise[i] = new Vector3(
                (float)random.NextDouble() * 2.0f - 1.0f,
                (float)random.NextDouble() * 2.0f - 1.0f,
                0.0f
            );
        }
        
        _noiseTexture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _noiseTexture);
        
        unsafe
        {
            fixed (Vector3* data = ssaoNoise)
            {
                _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgb32f, NoiseSize, NoiseSize, 0, 
                    PixelFormat.Rgb, PixelType.Float, data);
            }
        }
        
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat);
    }

    private unsafe void CreateFramebuffers()
    {
        _ssaoFBO = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _ssaoFBO);
        
        _ssaoColorBuffer = CreateSsaoTexture();
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, 
            TextureTarget.Texture2D, _ssaoColorBuffer, 0);
        
        _ssaoBlurFBO = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _ssaoBlurFBO);
        
        _ssaoColorBufferBlur = CreateSsaoTexture();
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, 
            TextureTarget.Texture2D, _ssaoColorBufferBlur, 0);
        
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private unsafe uint CreateSsaoTexture()
    {
        uint texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.R16f, (uint)_width, (uint)_height, 0, 
            PixelFormat.Red, PixelType.Float, (void*)null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        return texture;
    }

    public void Render(uint gPositionTexture, uint gNormalTexture, Matrix4x4 projection, ScreenQuad quad)
    {
        RenderSsaoPass(gPositionTexture, gNormalTexture, projection, quad);
        RenderBlurPass(quad);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private void RenderSsaoPass(uint gPositionTexture, uint gNormalTexture, Matrix4x4 projection, ScreenQuad quad)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _ssaoFBO);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
        
        _ssaoShader.Use();
        _ssaoShader.SetUniform("projection", projection);
        _ssaoShader.SetUniform("noiseScale", new Vector2(_width / (float)NoiseSize, _height / (float)NoiseSize));
        
        for (int i = 0; i < KernelSize; ++i)
            _ssaoShader.SetUniform($"samples[{i}]", _ssaoKernel[i]);
        
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, gPositionTexture);
        _ssaoShader.SetUniform("gPosition", 0);
        
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, gNormalTexture);
        _ssaoShader.SetUniform("gNormal", 1);
        
        _gl.ActiveTexture(TextureUnit.Texture2);
        _gl.BindTexture(TextureTarget.Texture2D, _noiseTexture);
        _ssaoShader.SetUniform("texNoise", 2);
        
        quad.Draw();
    }

    private void RenderBlurPass(ScreenQuad quad)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _ssaoBlurFBO);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
        
        _blurShader.Use();
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _ssaoColorBuffer);
        _blurShader.SetUniform("ssaoInput", 0);
        
        quad.Draw();
    }

    public void Resize(int width, int height)
    {
        _width = width;
        _height = height;
        
        _gl.DeleteFramebuffer(_ssaoFBO);
        _gl.DeleteFramebuffer(_ssaoBlurFBO);
        _gl.DeleteTexture(_ssaoColorBuffer);
        _gl.DeleteTexture(_ssaoColorBufferBlur);
        
        CreateFramebuffers();
    }

    private static float Lerp(float a, float b, float t) => a + t * (b - a);

    public void Dispose()
    {
        _gl.DeleteFramebuffer(_ssaoFBO);
        _gl.DeleteFramebuffer(_ssaoBlurFBO);
        _gl.DeleteTexture(_ssaoColorBuffer);
        _gl.DeleteTexture(_ssaoColorBufferBlur);
        _gl.DeleteTexture(_noiseTexture);
        _ssaoShader?.Dispose();
        _blurShader?.Dispose();
    }
}
