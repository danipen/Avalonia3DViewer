using System;
using Silk.NET.OpenGL;
using Avalonia3DViewer.Rendering;
using ShaderProgram = Avalonia3DViewer.Rendering.Shader;

namespace Avalonia3DViewer.PostProcessing;

public class BloomEffect : IDisposable
{
    private readonly GL _gl;
    private ShaderProgram _extractShader;
    private ShaderProgram _blurShader;
    
    private uint[] _pingpongFBO = new uint[2];
    private uint[] _pingpongBuffer = new uint[2];
    
    private int _width;
    private int _height;

    private const int BlurPasses = 10;

    public uint BloomTexture => _pingpongBuffer[0];

    public BloomEffect(GL gl, int width, int height)
    {
        _gl = gl;
        _width = width;
        _height = height;
        
        _extractShader = new ShaderProgram(_gl, "Shaders/screen_quad.vert", "Shaders/bloom_extract.frag");
        _blurShader = new ShaderProgram(_gl, "Shaders/screen_quad.vert", "Shaders/blur.frag");
        
        CreateFramebuffers();
    }

    private unsafe void CreateFramebuffers()
    {
        _pingpongFBO[0] = _gl.GenFramebuffer();
        _pingpongFBO[1] = _gl.GenFramebuffer();
        
        for (int i = 0; i < 2; i++)
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _pingpongFBO[i]);
            
            _pingpongBuffer[i] = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, _pingpongBuffer[i]);
            _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba16f, 
                (uint)_width, (uint)_height, 0, PixelFormat.Rgba, PixelType.Float, (void*)null);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, 
                TextureTarget.Texture2D, _pingpongBuffer[i], 0);
        }
        
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void Render(uint sceneTexture, float threshold, ScreenQuad quad)
    {
        ExtractBrightAreas(sceneTexture, threshold, quad);
        ApplyBlurPasses(quad);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private void ExtractBrightAreas(uint sceneTexture, float threshold, ScreenQuad quad)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _pingpongFBO[0]);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
        
        _extractShader.Use();
        _extractShader.SetUniform("threshold", threshold);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, sceneTexture);
        _extractShader.SetUniform("scene", 0);
        
        quad.Draw();
    }

    private void ApplyBlurPasses(ScreenQuad quad)
    {
        bool horizontal = true;
        bool firstIteration = true;
        
        _blurShader.Use();
        for (int i = 0; i < BlurPasses; i++)
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _pingpongFBO[horizontal ? 1 : 0]);
            _blurShader.SetUniform("horizontal", horizontal);
            
            _gl.ActiveTexture(TextureUnit.Texture0);
            uint sourceBuffer = firstIteration ? _pingpongBuffer[0] : _pingpongBuffer[horizontal ? 0 : 1];
            _gl.BindTexture(TextureTarget.Texture2D, sourceBuffer);
            _blurShader.SetUniform("image", 0);
            
            quad.Draw();
            
            horizontal = !horizontal;
            firstIteration = false;
        }
    }

    public void Resize(int width, int height)
    {
        _width = width;
        _height = height;
        
        for (int i = 0; i < 2; i++)
        {
            _gl.DeleteFramebuffer(_pingpongFBO[i]);
            _gl.DeleteTexture(_pingpongBuffer[i]);
        }
        
        CreateFramebuffers();
    }

    public void Dispose()
    {
        for (int i = 0; i < 2; i++)
        {
            _gl.DeleteFramebuffer(_pingpongFBO[i]);
            _gl.DeleteTexture(_pingpongBuffer[i]);
        }
        _extractShader?.Dispose();
        _blurShader?.Dispose();
    }
}
