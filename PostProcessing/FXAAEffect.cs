using System;
using Silk.NET.OpenGL;
using Avalonia3DViewer.Rendering;
using ShaderProgram = Avalonia3DViewer.Rendering.Shader;

namespace Avalonia3DViewer.PostProcessing;

public class FXAAEffect : IDisposable
{
    private readonly GL _gl;
    private ShaderProgram _fxaaShader;

    public float Subpix { get; set; } = 0.75f;
    public float EdgeThreshold { get; set; } = 0.125f;
    public float EdgeThresholdMin { get; set; } = 0.0312f;
    public float SpanMax { get; set; } = 12.0f;
    public float ReduceMul { get; set; } = 1.0f / 8.0f;
    public float ReduceMin { get; set; } = 1.0f / 128.0f;

    public FXAAEffect(GL gl)
    {
        _gl = gl;
        _fxaaShader = new ShaderProgram(_gl, "Shaders/screen_quad.vert", "Shaders/fxaa.frag");
    }

    public void Render(uint sceneTexture, ScreenQuad quad)
    {
        _fxaaShader.Use();
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, sceneTexture);
        _fxaaShader.SetUniform("screenTexture", 0);

        _fxaaShader.SetUniform("fxaaSubpix", Subpix);
        _fxaaShader.SetUniform("fxaaEdgeThreshold", EdgeThreshold);
        _fxaaShader.SetUniform("fxaaEdgeThresholdMin", EdgeThresholdMin);
        _fxaaShader.SetUniform("fxaaSpanMax", SpanMax);
        _fxaaShader.SetUniform("fxaaReduceMul", ReduceMul);
        _fxaaShader.SetUniform("fxaaReduceMin", ReduceMin);
        
        quad.Draw();
    }

    public void Dispose()
    {
        _fxaaShader?.Dispose();
    }
}
