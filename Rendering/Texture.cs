using System;
using System.Buffers;
using System.IO;
using Silk.NET.OpenGL;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Avalonia3DViewer.Rendering;

public class Texture : IDisposable
{
    private readonly GL _gl;
    public uint Handle { get; private set; }
    public TextureTarget Target { get; private set; }
    private bool _disposed;

    public bool HasNonOpaqueAlpha { get; private set; }
    public float PartialAlphaFraction { get; private set; }
    public bool IsMostlyBinaryAlpha { get; private set; }
    
    private const int GlTextureMaxAnisotropyExt = 0x84FE;
    private const int GlMaxTextureMaxAnisotropyExt = 0x84FF;
    public const int MaxTextureSize = 2048;

    public Texture(GL gl, TextureTarget target = TextureTarget.Texture2D)
    {
        _gl = gl;
        Target = target;
        Handle = _gl.GenTexture();
    }

    public static Texture LoadFromFile(GL gl, string path, bool srgb = false)
    {
        var texture = new Texture(gl);
        
        using var img = Image.Load<Rgba32>(path);
        DownscaleIfNeeded(img, path);

        texture.Bind();
        texture.UploadRgba32Image(img, srgb);
        gl.GenerateMipmap(TextureTarget.Texture2D);
        ApplyHighQualityFiltering(gl);

        return texture;
    }

    public static Texture CreateFromPixelData(GL gl, byte[] pixelData, int width, int height, bool srgb = false)
    {
        var texture = new Texture(gl);
        texture.Bind();

        AnalyzeAlpha(pixelData, out bool hasAlpha, out float partialFrac, out bool mostlyBinary);
        texture.HasNonOpaqueAlpha = hasAlpha;
        texture.PartialAlphaFraction = partialFrac;
        texture.IsMostlyBinaryAlpha = mostlyBinary;

        unsafe
        {
            fixed (byte* p = pixelData)
            {
                gl.TexImage2D(TextureTarget.Texture2D, 0, srgb ? (int)InternalFormat.SrgbAlpha : (int)InternalFormat.Rgba8,
                    (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
            }
        }

        gl.GenerateMipmap(TextureTarget.Texture2D);
        ApplyHighQualityFiltering(gl);

        return texture;
    }

    public static Texture LoadHDR(GL gl, string path)
    {
        var texture = new Texture(gl);
        
        using var img = Image.Load<RgbaVector>(path);
        img.Mutate(x => x.Flip(FlipMode.Vertical));
        DownscaleHdrIfNeeded(img, path);

        texture.Bind();
        texture.UploadHdrImage(img);

        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);

        return texture;
    }

    public static Texture LoadFromMemory(GL gl, byte[] data, bool srgb = false)
    {
        var texture = new Texture(gl);
        
        using var stream = new MemoryStream(data);
        using var img = Image.Load<Rgba32>(stream);
        DownscaleIfNeeded(img, "memory");

        texture.Bind();
        texture.UploadRgba32ImageAsBytes(img, srgb);
        gl.GenerateMipmap(TextureTarget.Texture2D);
        ApplyHighQualityFiltering(gl);

        return texture;
    }

    private void UploadRgba32Image(Image<Rgba32> img, bool srgb)
    {
        int pixelCount = img.Width * img.Height;
        Rgba32[] pixels = ArrayPool<Rgba32>.Shared.Rent(pixelCount);
        try
        {
            img.CopyPixelDataTo(pixels.AsSpan(0, pixelCount));

            AnalyzeAlpha(pixels.AsSpan(0, pixelCount), out bool hasAlpha, out float partialFrac, out bool mostlyBinary);
            HasNonOpaqueAlpha = hasAlpha;
            PartialAlphaFraction = partialFrac;
            IsMostlyBinaryAlpha = mostlyBinary;

            unsafe
            {
                fixed (Rgba32* p = pixels)
                {
                    _gl.TexImage2D(TextureTarget.Texture2D, 0, srgb ? (int)InternalFormat.SrgbAlpha : (int)InternalFormat.Rgba8,
                        (uint)img.Width, (uint)img.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
                }
            }
        }
        finally
        {
            ArrayPool<Rgba32>.Shared.Return(pixels);
        }
    }

    private void UploadRgba32ImageAsBytes(Image<Rgba32> img, bool srgb)
    {
        int byteCount = img.Width * img.Height * 4;
        byte[] pixels = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            img.CopyPixelDataTo(pixels.AsSpan(0, byteCount));

            AnalyzeAlpha(pixels.AsSpan(0, byteCount), out bool hasAlpha, out float partialFrac, out bool mostlyBinary);
            HasNonOpaqueAlpha = hasAlpha;
            PartialAlphaFraction = partialFrac;
            IsMostlyBinaryAlpha = mostlyBinary;

            unsafe
            {
                fixed (byte* p = pixels)
                {
                    _gl.TexImage2D(TextureTarget.Texture2D, 0, srgb ? (int)InternalFormat.SrgbAlpha : (int)InternalFormat.Rgba8,
                        (uint)img.Width, (uint)img.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    private void UploadHdrImage(Image<RgbaVector> img)
    {
        int pixelCount = img.Width * img.Height;
        RgbaVector[] pixels = ArrayPool<RgbaVector>.Shared.Rent(pixelCount);
        try
        {
            img.CopyPixelDataTo(pixels.AsSpan(0, pixelCount));

            unsafe
            {
                fixed (RgbaVector* p = pixels)
                {
                    _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba16f,
                        (uint)img.Width, (uint)img.Height, 0, PixelFormat.Rgba, PixelType.Float, p);
                }
            }
        }
        finally
        {
            ArrayPool<RgbaVector>.Shared.Return(pixels);
        }
    }

    private static void DownscaleIfNeeded(Image<Rgba32> img, string path)
    {
        if (img.Width <= MaxTextureSize && img.Height <= MaxTextureSize) return;

        float scale = Math.Min((float)MaxTextureSize / img.Width, (float)MaxTextureSize / img.Height);
        int newWidth = Math.Max(1, (int)(img.Width * scale));
        int newHeight = Math.Max(1, (int)(img.Height * scale));
        img.Mutate(x => x.Resize(newWidth, newHeight));
        Console.WriteLine($"[Texture] Downscaled {path} to {newWidth}x{newHeight}");
    }

    private static void DownscaleHdrIfNeeded(Image<RgbaVector> img, string path)
    {
        const int MaxHdrSize = 4096;
        if (img.Width <= MaxHdrSize && img.Height <= MaxHdrSize) return;

        float scale = Math.Min((float)MaxHdrSize / img.Width, (float)MaxHdrSize / img.Height);
        int newWidth = Math.Max(1, (int)(img.Width * scale));
        int newHeight = Math.Max(1, (int)(img.Height * scale));
        img.Mutate(x => x.Resize(newWidth, newHeight));
        Console.WriteLine($"[Texture] Downscaled HDR {path} to {newWidth}x{newHeight}");
    }

    private static void ApplyHighQualityFiltering(GL gl)
    {
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        
        float maxAnisotropy = GetMaxAnisotropy(gl);
        gl.TexParameter(TextureTarget.Texture2D, (TextureParameterName)GlTextureMaxAnisotropyExt, maxAnisotropy);
    }

    private static float GetMaxAnisotropy(GL gl)
    {
        try
        {
            gl.GetFloat((GetPName)GlMaxTextureMaxAnisotropyExt, out float maxAnisotropy);
            return Math.Clamp(maxAnisotropy, 1.0f, 16.0f);
        }
        catch
        {
            return 16.0f;
        }
    }

    private static void AnalyzeAlpha(ReadOnlySpan<Rgba32> pixels, out bool hasNonOpaqueAlpha, out float partialAlphaFraction, out bool isMostlyBinaryAlpha)
    {
        int total = pixels.Length;
        if (total <= 0)
        {
            hasNonOpaqueAlpha = false;
            partialAlphaFraction = 0f;
            isMostlyBinaryAlpha = false;
            return;
        }

        int nonOpaque = 0;
        int partial = 0;

        for (int i = 0; i < total; i++)
        {
            byte a = pixels[i].A;
            if (a == 255) continue;
            
            nonOpaque++;
            if (a != 0) partial++;
        }

        hasNonOpaqueAlpha = nonOpaque > 0;
        partialAlphaFraction = (float)partial / total;
        isMostlyBinaryAlpha = hasNonOpaqueAlpha && partialAlphaFraction < 0.05f;
    }

    private static void AnalyzeAlpha(ReadOnlySpan<byte> rgbaPixels, out bool hasNonOpaqueAlpha, out float partialAlphaFraction, out bool isMostlyBinaryAlpha)
    {
        int totalPixels = rgbaPixels.Length / 4;
        if (totalPixels <= 0)
        {
            hasNonOpaqueAlpha = false;
            partialAlphaFraction = 0f;
            isMostlyBinaryAlpha = false;
            return;
        }

        int nonOpaque = 0;
        int partial = 0;

        for (int i = 0; i < rgbaPixels.Length; i += 4)
        {
            byte a = rgbaPixels[i + 3];
            if (a == 255) continue;
            
            nonOpaque++;
            if (a != 0) partial++;
        }

        hasNonOpaqueAlpha = nonOpaque > 0;
        partialAlphaFraction = (float)partial / totalPixels;
        isMostlyBinaryAlpha = hasNonOpaqueAlpha && partialAlphaFraction < 0.05f;
    }

    public void Bind(int unit = 0)
    {
        _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + unit));
        _gl.BindTexture(Target, Handle);
    }

    public void Unbind() => _gl.BindTexture(Target, 0);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        if (Handle != 0)
        {
            _gl.DeleteTexture(Handle);
            Handle = 0;
        }
    }
}
