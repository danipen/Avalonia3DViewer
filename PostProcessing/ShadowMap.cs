using System;
using System.Numerics;
using Silk.NET.OpenGL;
using Avalonia3DViewer.Rendering;
using ShaderProgram = Avalonia3DViewer.Rendering.Shader;

namespace Avalonia3DViewer.PostProcessing;

public class ShadowMap : IDisposable
{
    private readonly GL _gl;
    private ShaderProgram _shadowShader;
    
    private uint _depthMapFBO;
    private uint _depthMap;
    
    private const int ShadowWidth = 2048;
    private const int ShadowHeight = 2048;

    public uint DepthMapTexture => _depthMap;
    public Matrix4x4 LightSpaceMatrix { get; private set; }

    public ShadowMap(GL gl)
    {
        _gl = gl;
        _shadowShader = new ShaderProgram(_gl, "Shaders/shadow.vert", "Shaders/shadow.frag");
        CreateFramebuffer();
    }

    private unsafe void CreateFramebuffer()
    {
        _depthMapFBO = _gl.GenFramebuffer();
        
        _depthMap = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _depthMap);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.DepthComponent24, 
            ShadowWidth, ShadowHeight, 0, PixelFormat.DepthComponent, PixelType.UnsignedInt, (void*)null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToBorder);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToBorder);
        
        float[] borderColor = { 1.0f, 1.0f, 1.0f, 1.0f };
        fixed (float* ptr = borderColor)
        {
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBorderColor, ptr);
        }
        
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _depthMapFBO);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, 
            TextureTarget.Texture2D, _depthMap, 0);
        _gl.DrawBuffer(GLEnum.None);
        _gl.ReadBuffer(GLEnum.None);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void BeginRender(Vector3 lightDir, Vector3 sceneCenter, float sceneRadius)
    {
        LightSpaceMatrix = CalculateLightSpaceMatrix(lightDir, sceneCenter, sceneRadius);
        
        _gl.Viewport(0, 0, ShadowWidth, ShadowHeight);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _depthMapFBO);
        _gl.Clear(ClearBufferMask.DepthBufferBit);
        
        _shadowShader.Use();
        _shadowShader.SetUniform("lightSpaceMatrix", LightSpaceMatrix);
    }

    private Matrix4x4 CalculateLightSpaceMatrix(Vector3 lightDir, Vector3 sceneCenter, float sceneRadius)
    {
        Vector3 lightPos = sceneCenter - lightDir * sceneRadius * 5.0f;
        
        Vector3 upVector = MathF.Abs(Vector3.Dot(lightDir, Vector3.UnitY)) > 0.9f 
            ? Vector3.UnitZ 
            : Vector3.UnitY;
        
        Matrix4x4 lightView = Matrix4x4.CreateLookAt(lightPos, sceneCenter, upVector);
        
        float orthoSize = sceneRadius * 4.0f;
        float nearPlane = sceneRadius * 0.5f;
        float farPlane = sceneRadius * 12.0f;
        Matrix4x4 lightProjection = Matrix4x4.CreateOrthographic(orthoSize, orthoSize, nearPlane, farPlane);
        
        return lightView * lightProjection;
    }

    public void RenderMesh(Matrix4x4 modelMatrix)
    {
        _shadowShader.SetUniform("model", modelMatrix);
    }

    public void EndRender(int viewportWidth, int viewportHeight)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)viewportWidth, (uint)viewportHeight);
    }

    public void Use()
    {
        _shadowShader.Use();
    }

    public void Dispose()
    {
        _gl.DeleteFramebuffer(_depthMapFBO);
        _gl.DeleteTexture(_depthMap);
        _shadowShader?.Dispose();
    }
}
