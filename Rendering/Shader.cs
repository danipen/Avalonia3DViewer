using System;
using System.IO;
using Silk.NET.OpenGL;

namespace Avalonia3DViewer.Rendering;

public class Shader : IDisposable
{
    private readonly GL _gl;
    private uint _handle;
    private bool _disposed;

    public Shader(GL gl, string vertexPath, string fragmentPath)
    {
        _gl = gl;

        uint vertex = LoadShader(ShaderType.VertexShader, vertexPath);
        uint fragment = LoadShader(ShaderType.FragmentShader, fragmentPath);

        _handle = _gl.CreateProgram();
        _gl.AttachShader(_handle, vertex);
        _gl.AttachShader(_handle, fragment);
        _gl.LinkProgram(_handle);

        ValidateLinkStatus();

        _gl.DetachShader(_handle, vertex);
        _gl.DetachShader(_handle, fragment);
        _gl.DeleteShader(vertex);
        _gl.DeleteShader(fragment);
    }

    private void ValidateLinkStatus()
    {
        _gl.GetProgram(_handle, GLEnum.LinkStatus, out var status);
        if (status == 0)
        {
            string info = _gl.GetProgramInfoLog(_handle);
            throw new Exception($"Program failed to link: {info}");
        }
    }

    public void Use() => _gl.UseProgram(_handle);

    public void SetUniform(string name, int value)
    {
        int location = _gl.GetUniformLocation(_handle, name);
        if (location != -1)
            _gl.Uniform1(location, value);
    }

    public void SetUniform(string name, float value)
    {
        int location = _gl.GetUniformLocation(_handle, name);
        if (location != -1)
            _gl.Uniform1(location, value);
    }

    public void SetUniform(string name, System.Numerics.Vector3 value)
    {
        int location = _gl.GetUniformLocation(_handle, name);
        if (location != -1)
            _gl.Uniform3(location, value.X, value.Y, value.Z);
    }

    public void SetUniform(string name, System.Numerics.Vector2 value)
    {
        int location = _gl.GetUniformLocation(_handle, name);
        if (location != -1)
            _gl.Uniform2(location, value.X, value.Y);
    }

    public void SetUniform(string name, System.Numerics.Matrix4x4 value)
    {
        int location = _gl.GetUniformLocation(_handle, name);
        if (location == -1) return;
        
        unsafe
        {
            _gl.UniformMatrix4(location, 1, false, (float*)&value);
        }
    }

    public void SetUniform(string name, bool value)
    {
        int location = _gl.GetUniformLocation(_handle, name);
        if (location != -1)
            _gl.Uniform1(location, value ? 1 : 0);
    }

    private uint LoadShader(ShaderType type, string path)
    {
        string src = File.ReadAllText(path);
        uint handle = _gl.CreateShader(type);
        _gl.ShaderSource(handle, src);
        _gl.CompileShader(handle);

        string infoLog = _gl.GetShaderInfoLog(handle);
        if (!string.IsNullOrWhiteSpace(infoLog))
            throw new Exception($"Error compiling shader ({path}): {infoLog}");

        return handle;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        if (_handle != 0)
        {
            _gl.DeleteProgram(_handle);
            _handle = 0;
        }
    }
}
