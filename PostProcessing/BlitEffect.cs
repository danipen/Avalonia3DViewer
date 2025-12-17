using System;
using Silk.NET.OpenGL;
using Avalonia3DViewer.Rendering;
using ShaderProgram = Avalonia3DViewer.Rendering.Shader;

namespace Avalonia3DViewer.PostProcessing;

public sealed class BlitEffect : IDisposable
{
    private readonly GL _gl;
    private readonly ShaderProgram _blitShader;

    public BlitEffect(GL gl)
    {
        _gl = gl;
        _blitShader = new ShaderProgram(_gl, "Shaders/screen_quad.vert", "Shaders/blit.frag");
    }

    public void Render(uint texture, ScreenQuad quad)
    {
        _blitShader.Use();
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        _blitShader.SetUniform("screenTexture", 0);
        quad.Draw();
    }

    public void Dispose()
    {
        _blitShader.Dispose();
    }
}
