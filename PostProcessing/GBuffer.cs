using System;
using System.Numerics;
using Silk.NET.OpenGL;
using Avalonia3DViewer.Rendering;
using ShaderProgram = Avalonia3DViewer.Rendering.Shader;

namespace Avalonia3DViewer.PostProcessing;

public class GBuffer : IDisposable
{
    private readonly GL _gl;
    private ShaderProgram _geometryShader;
    
    private uint _gBuffer;
    private uint _gPosition;
    private uint _gNormal;
    private uint _gAlbedo;
    private uint _rboDepth;
    
    private int _width;
    private int _height;

    public uint PositionTexture => _gPosition;
    public uint NormalTexture => _gNormal;
    public uint AlbedoTexture => _gAlbedo;
    public uint Framebuffer => _gBuffer;

    public GBuffer(GL gl, int width, int height)
    {
        _gl = gl;
        _width = width;
        _height = height;
        
        _geometryShader = new ShaderProgram(_gl, "Shaders/geometry.vert", "Shaders/geometry.frag");
        CreateFramebuffer();
    }

    private unsafe void CreateFramebuffer()
    {
        _gBuffer = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _gBuffer);
        
        _gPosition = CreateViewSpaceTexture();
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, 
            TextureTarget.Texture2D, _gPosition, 0);
        
        _gNormal = CreateViewSpaceTexture();
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1, 
            TextureTarget.Texture2D, _gNormal, 0);
        
        _gAlbedo = CreateAlbedoTexture();
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment2, 
            TextureTarget.Texture2D, _gAlbedo, 0);
        
        SetDrawBuffers();
        CreateDepthRenderbuffer();
        ValidateFramebuffer();
        
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private unsafe uint CreateViewSpaceTexture()
    {
        uint texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba16f, 
            (uint)_width, (uint)_height, 0, PixelFormat.Rgba, PixelType.Float, (void*)null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        return texture;
    }

    private unsafe uint CreateAlbedoTexture()
    {
        uint texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba, 
            (uint)_width, (uint)_height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, (void*)null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        return texture;
    }

    private void SetDrawBuffers()
    {
        DrawBufferMode[] attachments = { 
            DrawBufferMode.ColorAttachment0, 
            DrawBufferMode.ColorAttachment1, 
            DrawBufferMode.ColorAttachment2 
        };
        _gl.DrawBuffers(3, attachments);
    }

    private void CreateDepthRenderbuffer()
    {
        _rboDepth = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _rboDepth);
        _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent, 
            (uint)_width, (uint)_height);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, 
            RenderbufferTarget.Renderbuffer, _rboDepth);
    }

    private void ValidateFramebuffer()
    {
        if (_gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
            Console.WriteLine("[GBuffer] ERROR: Framebuffer is not complete!");
    }

    public void BeginRender()
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _gBuffer);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
    }

    public void EndRender()
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void UseShader()
    {
        _geometryShader.Use();
    }

    public void SetUniforms(Matrix4x4 model, Matrix4x4 view, Matrix4x4 projection)
    {
        _geometryShader.SetUniform("model", model);
        _geometryShader.SetUniform("view", view);
        _geometryShader.SetUniform("projection", projection);
    }

    public void Resize(int width, int height)
    {
        _width = width;
        _height = height;
        
        _gl.DeleteFramebuffer(_gBuffer);
        _gl.DeleteTexture(_gPosition);
        _gl.DeleteTexture(_gNormal);
        _gl.DeleteTexture(_gAlbedo);
        _gl.DeleteRenderbuffer(_rboDepth);
        
        CreateFramebuffer();
    }

    public void Dispose()
    {
        _gl.DeleteFramebuffer(_gBuffer);
        _gl.DeleteTexture(_gPosition);
        _gl.DeleteTexture(_gNormal);
        _gl.DeleteTexture(_gAlbedo);
        _gl.DeleteRenderbuffer(_rboDepth);
        _geometryShader?.Dispose();
    }
}
