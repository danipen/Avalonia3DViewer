using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering;
using Avalonia.Threading;
using Silk.NET.OpenGL;
using Avalonia3DViewer.Rendering;
using Avalonia3DViewer.PostProcessing;
using ShaderProgram = Avalonia3DViewer.Rendering.Shader;

namespace Avalonia3DViewer.Controls;

public class GLViewport : OpenGlControlBase, ICustomHitTest
{
    private GL? _gl;
    private Camera? _camera;
    private ShaderProgram? _pbrShader;
    private Model? _model;
    private IBLEnvironment? _iblEnvironment;
    private ProceduralHDRI? _proceduralHDRI;
    private Mesh? _testTriangle;
    private Mesh? _groundPlane;
    
    // Post-processing effects
    private SSAOEffect? _ssaoEffect;
    private BloomEffect? _bloomEffect;
    private ShadowMap? _shadowMap;
    private FXAAEffect? _fxaaEffect;
    private BlitEffect? _blitEffect;
    private GBuffer? _gBuffer;
    private ScreenQuad? _screenQuad;
    private Rendering.Shader? _compositeShader;
    
    // Framebuffers for main rendering
    private uint _hdrFBO;
    private uint _hdrColorBuffer;
    private uint _depthRBO;

    // Optional MSAA framebuffer for HDR rendering (resolved into _hdrFBO/_hdrColorBuffer)
    private uint _hdrMsaaFBO;
    private uint _hdrMsaaColorRBO;
    private uint _hdrMsaaDepthRBO;

    // Framebuffer for final composite (pre-FXAA) output
    private uint _finalFBO;
    private uint _finalColorBuffer;
    
    private string? _pendingModelPath;
    private string? _pendingEnvironmentPath;
    
    // Async model loading
    private ModelLoadData? _pendingModelData;
    private string _loadingStatus = "";
    private CancellationTokenSource? _loadingCts;

    // Prevent races between UI thread (LoadModelAsync/LoadModel) and render thread (OnOpenGlRender)
    private readonly object _pendingLoadLock = new();
    
    // Event for loading status updates (isLoading, statusText, progressPercentage 0-100, -1 for indeterminate)
    public event Action<bool, string, int>? LoadingStatusChanged;
    
    private bool _needsResize;
    private int _pendingWidth;
    private int _pendingHeight;
    private double _lastRenderScaling = 1.0;
    
    // Helper to get physical pixel dimensions accounting for DPI scaling
    private (int width, int height) GetPhysicalPixelSize()
    {
        double scaling = VisualRoot?.RenderScaling ?? 1.0;
        int width = Math.Max(1, (int)(Bounds.Width * scaling));
        int height = Math.Max(1, (int)(Bounds.Height * scaling));
        return (width, height);
    }

    private Point _lastMousePosition;
    private bool _isLeftButtonPressed;
    private bool _isMiddleButtonPressed;

    // Rendering and lighting properties
    public float Exposure { get; set; } = 1.15f;
    // Applied only when tonemapping is enabled (helps match perceived brightness when toggling tonemap).
    public float TonemapExposureCompensation { get; set; } = 1.25f;
    public bool UseBloom { get; set; } = false;
    public float BloomIntensity { get; set; } = 0.1f;
    public bool UseSSAO { get; set; } = true;
    public float SsaoIntensity { get; set; } = 0.5f;
    public bool ShowGround { get; set; } = true;
    
    // Light intensities - increased for better illumination
    public float MainLightIntensity { get; set; } = 5.0f;
    public float FillLightIntensity { get; set; } = 2.5f;
    public float RimLightIntensity { get; set; } = 0.75f;
    public float TopLightIntensity { get; set; } = 2.4f;
    public float AmbientIntensity { get; set; } = 0.4f;
    
    // Shadow properties
    public bool UseShadows { get; set; } = true;
    public float ShadowStrength { get; set; } = 0.3f;

    // Key light direction control
    // If true, the main directional light will be derived from the current camera direction so the model
    // is lit from the viewer side (helpful for scanning details / faces).
    public bool KeyLightFollowsCamera { get; set; } = true;
    // Negative = from camera-left, positive = from camera-right.
    public float KeyLightSideBias { get; set; } = -0.6f;
    // Negative = from above (light rays point downward).
    public float KeyLightUpBias { get; set; } = -0.36f;

    // Render pipeline controls
    public bool UseIBL { get; set; } = true;
    public bool UseTonemapping { get; set; } = true;
    public bool UseFXAA { get; set; } = true;

    // Anti-aliasing controls
    // Note: MSAA only affects the HDR scene render. Post-processing (SSAO/Bloom/Composite/FXAA) remains single-sampled.
    public bool UseMSAA { get; set; } = true;
    public int MsaaSamples { get; set; } = 4;

    // FXAA tuning (useful when zoomed out). Higher Subpix/Span smooths more but can soften fine detail.
    public float FxaaSubpix { get; set; } = 0.75f;
    public float FxaaEdgeThreshold { get; set; } = 0.125f;
    public float FxaaEdgeThresholdMin { get; set; } = 0.0312f;
    public float FxaaSpanMax { get; set; } = 12.0f;
    public float FxaaReduceMul { get; set; } = 1.0f / 8.0f;
    public float FxaaReduceMin { get; set; } = 1.0f / 128.0f;

    // Background
    // Light background is white (as current). Dark background is a very dark gray (not black).
    public bool UseDarkBackground { get; set; } = false;
    
    // Material override controls
    public float SpecularScale { get; set; } = 0.05f;
    public float RoughnessOffset { get; set; } = 0.5f;
    public float MetallicOffset { get; set; } = 0.0f;
    public float IblIntensity { get; set; } = 1f;
    
    // Debug mode: 0=normal, 1=raw texture, 2=linear albedo, 3=IBL only, 4=pre-tonemap, 5=diffuse only
    public int DebugMode { get; set; } = 0;

    // Background colors
    private static readonly Vector4 LightBackgroundColor = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 DarkBackgroundColor = new(0.04f, 0.04f, 0.04f, 1f);
    
    // Alpha mode constants for shader
    private const int AlphaOpaque = 0;
    private const int AlphaMask = 1;
    private const int AlphaBlend = 2;
    private const float DefaultAlphaCutoff = 0.5f;

    private Vector4 GetBackgroundColor() => UseDarkBackground ? DarkBackgroundColor : LightBackgroundColor;

    private Vector3 GetMainLightDir()
    {
        // `lightDir` is treated as the direction of light rays (from light -> surface).
        // In the shader, the lighting uses `-lightDir` as the surface->light vector.
        if (_camera == null || !KeyLightFollowsCamera)
        {
            // Side-front-ish default (world-space). This is a fallback if camera isn't ready.
            return Vector3.Normalize(new Vector3(-0.35f, -0.85f, 0.25f));
        }

        var forward = Vector3.Normalize(_camera.Target - _camera.Position);
        var right = Vector3.Normalize(Vector3.Cross(forward, _camera.Up));

        var dir = forward + right * KeyLightSideBias + _camera.Up * KeyLightUpBias;
        if (dir.LengthSquared() < 1e-6f)
            dir = forward;

        return Vector3.Normalize(dir);
    }
    


    public bool HitTest(Point point)
    {
        return this.Bounds.Contains(point);
    }

    public GLViewport()
    {
        //this.Background = Brushes.Transparent;

        Focusable = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Request focus when attached to ensure pointer events work
        Focus();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Cancel any background loading when control is detached
        CancelAndDisposeLoadingCts();
        DisposePendingModelData();

        base.OnDetachedFromVisualTree(e);
    }

    private void CancelAndDisposeLoadingCts()
    {
        var cts = _loadingCts;
        _loadingCts = null;
        if (cts != null)
        {
            try { cts.Cancel(); } catch { /* ignore */ }
            cts.Dispose();
        }
    }

    private void DisposePendingModelData()
    {
        ModelLoadData? old;
        lock (_pendingLoadLock)
        {
            old = _pendingModelData;
            _pendingModelData = null;
            _pendingModelPath = null;
        }
        old?.Dispose();
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);

        _gl = GL.GetApi(gl.GetProcAddress);
        
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);  // Use Lequal instead of Less for better decal handling

        // Enable multisampling for better quality
        _gl.Enable(EnableCap.Multisample);
        
        // Enable backface culling for better performance
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Back);
        
        // Enable blending for transparency
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        
        // Initialize camera
        _camera = new Camera
        {
            AspectRatio = (float)Bounds.Width / (float)Bounds.Height
        };
        _camera.SetDistance(5.0f);

        // Load shaders
        try
        {
            _pbrShader = new ShaderProgram(_gl, "Shaders/pbr.vert", "Shaders/pbr.frag");
            
            // Load default environment
            _iblEnvironment = new IBLEnvironment(_gl);
            
            // Create procedural HDRI as fallback
            _proceduralHDRI = new ProceduralHDRI(_gl);
            
            // Initialize post-processing effects with physical pixel dimensions
            var (width, height) = GetPhysicalPixelSize();
            _lastRenderScaling = VisualRoot?.RenderScaling ?? 1.0;
            
            _screenQuad = new ScreenQuad(_gl);
            _gBuffer = new GBuffer(_gl, width, height);
            _ssaoEffect = new SSAOEffect(_gl, width, height);
            _bloomEffect = new BloomEffect(_gl, width, height);
            _shadowMap = new ShadowMap(_gl);
            _fxaaEffect = new FXAAEffect(_gl);
            _blitEffect = new BlitEffect(_gl);
            _compositeShader = new Rendering.Shader(_gl, "Shaders/screen_quad.vert", "Shaders/composite.frag");
            
            
            // Create HDR framebuffer for main rendering
            CreateHDRFramebuffer(width, height);
            CreateFinalFramebuffer(width, height);
            
            // Create test triangle to verify rendering works
            var testVertices = new Vertex[]
            {
                new Vertex { Position = new Vector3(0, 1, 0), Normal = new Vector3(0, 0, 1), TexCoord = new Vector2(0.5f, 1) },
                new Vertex { Position = new Vector3(-1, -1, 0), Normal = new Vector3(0, 0, 1), TexCoord = new Vector2(0, 0) },
                new Vertex { Position = new Vector3(1, -1, 0), Normal = new Vector3(0, 0, 1), TexCoord = new Vector2(1, 0) }
            };
            var testIndices = new uint[] { 0, 1, 2 };
            _testTriangle = new Mesh(_gl, testVertices, testIndices);
            
            // Ground plane will be created dynamically when model is loaded
            _groundPlane = null;
            
            // Auto-load model for autonomous testing
            string autoLoadPath = "/Users/danipen/Downloads/1969_dodge_charger_rt.glb";
            if (File.Exists(autoLoadPath))
            {
                _pendingModelPath = autoLoadPath;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GLViewport] ERROR initializing GL: {ex.Message}");
            Console.WriteLine($"[GLViewport] Stack trace: {ex.StackTrace}");
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        base.OnOpenGlDeinit(gl);
        
        // Dispose all shaders
        _pbrShader?.Dispose();
        _compositeShader?.Dispose();
        
        // Dispose model and geometry
        _model?.Dispose();
        _testTriangle?.Dispose();
        _groundPlane?.Dispose();
        
        // Dispose environment/lighting resources
        _iblEnvironment?.Dispose();
        _proceduralHDRI?.Dispose();
        
        // Dispose post-processing effects
        _ssaoEffect?.Dispose();
        _bloomEffect?.Dispose();
        _shadowMap?.Dispose();
        _fxaaEffect?.Dispose();
        _blitEffect?.Dispose();
        _gBuffer?.Dispose();
        _screenQuad?.Dispose();
        
        // Dispose HDR framebuffer resources
        if (_hdrFBO != 0)
        {
            _gl?.DeleteFramebuffer(_hdrFBO);
            _gl?.DeleteTexture(_hdrColorBuffer);
            if (_depthRBO != 0)
                _gl?.DeleteRenderbuffer(_depthRBO);
        }

        if (_hdrMsaaFBO != 0)
        {
            _gl?.DeleteFramebuffer(_hdrMsaaFBO);
            if (_hdrMsaaColorRBO != 0)
                _gl?.DeleteRenderbuffer(_hdrMsaaColorRBO);
            if (_hdrMsaaDepthRBO != 0)
                _gl?.DeleteRenderbuffer(_hdrMsaaDepthRBO);
        }

        if (_finalFBO != 0)
        {
            _gl?.DeleteFramebuffer(_finalFBO);
            _gl?.DeleteTexture(_finalColorBuffer);
        }
        
        // Dispose cancellation token source
        CancelAndDisposeLoadingCts();
        DisposePendingModelData();
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_gl == null || _camera == null)
        {
            Console.WriteLine("[GLViewport] Render skipped: GL or Camera null");
            return;
        }
        
        // Check if DPI scaling changed (e.g., window moved to different display)
        double currentScaling = VisualRoot?.RenderScaling ?? 1.0;
        if (Math.Abs(currentScaling - _lastRenderScaling) > 0.001)
        {
            // DPI changed, trigger resize
            var (width, height) = GetPhysicalPixelSize();
            _needsResize = true;
            _pendingWidth = width;
            _pendingHeight = height;
            _lastRenderScaling = currentScaling;
        }

        // Handle pending resize on render thread (where OpenGL context is active)
        if (_needsResize)
        {
            CreateHDRFramebuffer(_pendingWidth, _pendingHeight);
            CreateFinalFramebuffer(_pendingWidth, _pendingHeight);
            _gBuffer?.Resize(_pendingWidth, _pendingHeight);
            _ssaoEffect?.Resize(_pendingWidth, _pendingHeight);
            _bloomEffect?.Resize(_pendingWidth, _pendingHeight);
            _needsResize = false;
        }

        // IMPORTANT: Must bind Avalonia's provided framebuffer!
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
        
        // Use physical pixel dimensions for viewport
        var (viewportWidth, viewportHeight) = GetPhysicalPixelSize();
        _gl.Viewport(0, 0, (uint)viewportWidth, (uint)viewportHeight);

        var background = GetBackgroundColor();
        _gl.ClearColor(background.X, background.Y, background.Z, background.W);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        // Process pending model loads (async or sync)
        // Note: Only ONE of these will be set per frame - they are mutually exclusive paths
        ProcessPendingModelLoad();
        
        // Process pending environment load on render thread
        string? pendingEnvPath = null;
        lock (_pendingLoadLock)
        {
            if (_pendingEnvironmentPath != null)
            {
                pendingEnvPath = _pendingEnvironmentPath;
                _pendingEnvironmentPath = null;
            }
        }
        if (pendingEnvPath != null && _iblEnvironment != null)
        {
            try
            {
                _iblEnvironment.LoadEnvironment(pendingEnvPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GLViewport] ERROR loading environment: {ex.Message}");
            }
        }

        if (_model == null)
        {
            // No model loaded - render test triangle instead
            if (_testTriangle != null && _pbrShader != null && _camera != null)
            {
                // Set matrices for test triangle
                var testModel = Matrix4x4.Identity;
                var testView = _camera.GetViewMatrix();
                var testProjection = _camera.GetProjectionMatrix();
                

                
                _pbrShader.Use();
                
                _pbrShader.SetUniform("uModel", testModel);
                _pbrShader.SetUniform("uView", testView);
                _pbrShader.SetUniform("uProjection", testProjection);
                _pbrShader.SetUniform("camPos", _camera.Position);
                
                // Set material uniforms for PBR shader
                _pbrShader.SetUniform("albedo", new Vector3(1.0f, 0.5f, 0.2f)); // Orange color
                _pbrShader.SetUniform("metallic", 0.0f);
                _pbrShader.SetUniform("roughness", 0.5f);
                _pbrShader.SetUniform("ao", 1.0f);
                _pbrShader.SetUniform("opacity", 1.0f);
                
                // Disable all texture maps
                _pbrShader.SetUniform("useAlbedoMap", false);
                _pbrShader.SetUniform("useNormalMap", false);
                _pbrShader.SetUniform("useMetallicMap", false);
                _pbrShader.SetUniform("useRoughnessMap", false);
                _pbrShader.SetUniform("useAOMap", false);
                _pbrShader.SetUniform("useIBL", false);
                
                // Set simple directional light
                _pbrShader.SetUniform("lightDir", Vector3.Normalize(new Vector3(-0.5f, -1.0f, -0.5f)));
                
                // Bind dummy textures to prevent sampler errors
                if (_iblEnvironment != null)
                {
                    uint dummyTexture = _iblEnvironment.BrdfLUT;
                    
                    // Bind 2D textures (albedo, normal, metallic, roughness, ao, brdf)
                    for (int i = 0; i < 3; i++) // 0,1,2
                    {
                        _gl.ActiveTexture(TextureUnit.Texture0 + i);
                        _gl.BindTexture(TextureTarget.Texture2D, dummyTexture);
                    }
                    _gl.ActiveTexture(TextureUnit.Texture5); // brdfLUT at slot 5
                    _gl.BindTexture(TextureTarget.Texture2D, dummyTexture);
                    _gl.ActiveTexture(TextureUnit.Texture6); // roughness at slot 6
                    _gl.BindTexture(TextureTarget.Texture2D, dummyTexture);
                    _gl.ActiveTexture(TextureUnit.Texture7); // ao at slot 7
                    _gl.BindTexture(TextureTarget.Texture2D, dummyTexture);
                    
                    // Bind cubemap textures (irradiance, prefilter)
                    uint dummyCubemap = _iblEnvironment.IrradianceMap; // Use actual cubemap
                    _gl.ActiveTexture(TextureUnit.Texture3);
                    _gl.BindTexture(TextureTarget.TextureCubeMap, dummyCubemap);
                    _gl.ActiveTexture(TextureUnit.Texture4);
                    _gl.BindTexture(TextureTarget.TextureCubeMap, _iblEnvironment.PrefilterMap);
                    
                    _pbrShader.SetUniform("albedoMap", 0);
                    _pbrShader.SetUniform("normalMap", 1);
                    _pbrShader.SetUniform("metallicMap", 2);
                    _pbrShader.SetUniform("irradianceMap", 3);
                    _pbrShader.SetUniform("prefilterMap", 4);
                    _pbrShader.SetUniform("brdfLUT", 5);
                    _pbrShader.SetUniform("roughnessMap", 6);
                    _pbrShader.SetUniform("aoMap", 7);
                }
                
                _testTriangle.Draw();
                
                // Flush to ensure draw commands execute
                _gl.Flush();
            }
            return;
        }
        
        // === MODEL LOADED - POST-PROCESSING PIPELINE ===

        
        // Check if all post-processing components are ready
        bool usePostProcessing = _gBuffer != null && _ssaoEffect != null && 
                                 _bloomEffect != null && _screenQuad != null && _compositeShader != null;
        
        var model = Matrix4x4.Identity;
        var view = _camera!.GetViewMatrix();
        var projection = _camera.GetProjectionMatrix();
        
        if (usePostProcessing)
        {
            // Step 0: Render shadow map with top-down light for floor shadows
            Vector3 lightDir = GetMainLightDir();
            Vector3 sceneCenter = _model!.Center;
            float sceneRadius = _model.Radius;
            
            _shadowMap!.BeginRender(lightDir, sceneCenter, sceneRadius);
            
            // Only render model to shadow map (not ground - ground receives shadows, doesn't cast them)
            foreach (var mesh in _model.Meshes)
            {
                _shadowMap.RenderMesh(model);
                mesh.Draw();
            }
            _shadowMap.EndRender(viewportWidth, viewportHeight);
            
            // Step 1: Render G-buffer for SSAO
            _gBuffer!.BeginRender();
            _gl!.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            RenderSceneGeometry(model, view, projection);
            _gBuffer.EndRender();
            
            // Step 2: Generate SSAO
            _ssaoEffect!.Render(_gBuffer.PositionTexture, _gBuffer.NormalTexture, projection, _screenQuad!);
            
            // Step 3: Render scene to HDR framebuffer
            if (UseMSAA && _hdrMsaaFBO != 0)
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _hdrMsaaFBO);
            else
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _hdrFBO);
            _gl.Viewport(0, 0, (uint)viewportWidth, (uint)viewportHeight);
            background = GetBackgroundColor();
            _gl.ClearColor(background.X, background.Y, background.Z, background.W);
            _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            

            
            RenderSceneGeometry(model, view, projection);

            // Resolve MSAA to the HDR texture used by bloom/composite
            if (UseMSAA && _hdrMsaaFBO != 0)
            {
                _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _hdrMsaaFBO);
                _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _hdrFBO);
                _gl.BlitFramebuffer(
                    0, 0, viewportWidth, viewportHeight,
                    0, 0, viewportWidth, viewportHeight,
                    ClearBufferMask.ColorBufferBit,
                    BlitFramebufferFilter.Nearest);
            }
            

            
            // Step 4: Extract bright areas and blur for bloom
            _bloomEffect!.Render(_hdrColorBuffer, 1.0f, _screenQuad!);
            
            // Step 5: Composite everything to an intermediate texture (so we can optionally FXAA)
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _finalFBO);
            _gl.Viewport(0, 0, (uint)viewportWidth, (uint)viewportHeight);
            _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            
            // Disable depth test for fullscreen quad composite
            _gl.Disable(EnableCap.DepthTest);
            _compositeShader!.Use();
            _compositeShader.SetUniform("exposure", Exposure);
            _compositeShader.SetUniform("tonemapExposureCompensation", TonemapExposureCompensation);
            _compositeShader.SetUniform("useBloom", UseBloom);
            _compositeShader.SetUniform("useSSAO", UseSSAO);
            _compositeShader.SetUniform("useTonemapping", UseTonemapping);
            _compositeShader.SetUniform("bloomIntensity", BloomIntensity);
            _compositeShader.SetUniform("ssaoIntensity", SsaoIntensity);
            
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, _hdrColorBuffer);
            _compositeShader.SetUniform("scene", 0);
            
            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(TextureTarget.Texture2D, _bloomEffect.BloomTexture);
            _compositeShader.SetUniform("bloomBlur", 1);
            
            _gl.ActiveTexture(TextureUnit.Texture2);
            _gl.BindTexture(TextureTarget.Texture2D, _ssaoEffect.SSAOTexture);
            _compositeShader.SetUniform("ssaoTexture", 2);

            _screenQuad?.Draw();

            // Step 6: Present to screen (FXAA optional)
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
            _gl.Viewport(0, 0, (uint)viewportWidth, (uint)viewportHeight);
            _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            if (UseFXAA && _fxaaEffect != null)
            {
                _fxaaEffect.Subpix = FxaaSubpix;
                _fxaaEffect.EdgeThreshold = FxaaEdgeThreshold;
                _fxaaEffect.EdgeThresholdMin = FxaaEdgeThresholdMin;
                _fxaaEffect.SpanMax = FxaaSpanMax;
                _fxaaEffect.ReduceMul = FxaaReduceMul;
                _fxaaEffect.ReduceMin = FxaaReduceMin;
                _fxaaEffect.Render(_finalColorBuffer, _screenQuad!);
            }
            else
                _blitEffect?.Render(_finalColorBuffer, _screenQuad!);
            
            // Re-enable depth test
            _gl.Enable(EnableCap.DepthTest);
            

            
            return;
        }
    }
    


    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        
        if (_gl == null || _camera == null) return; // GL not initialized yet
        
        if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            _camera.AspectRatio = (float)(e.NewSize.Width / e.NewSize.Height);
            
            // Use physical pixel dimensions for OpenGL (accounting for DPI scaling)
            var (width, height) = GetPhysicalPixelSize();
            
            // IMPORTANT: Resize operations must happen on the render thread with OpenGL context active
            _needsResize = true;
            _pendingWidth = width;
            _pendingHeight = height;
            _lastRenderScaling = VisualRoot?.RenderScaling ?? 1.0;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var point = e.GetCurrentPoint(this);
        _lastMousePosition = point.Position;

        if (point.Properties.IsLeftButtonPressed)
        {
            _isLeftButtonPressed = true;
            e.Pointer.Capture(this);
        }
        if (point.Properties.IsMiddleButtonPressed)
        {
            _isMiddleButtonPressed = true;
            e.Pointer.Capture(this);
        }
        
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        
        var point = e.GetCurrentPoint(this);
        var props = point.Properties;

        // Check which button was released
        if (e.InitialPressMouseButton == MouseButton.Left)
        {
            _isLeftButtonPressed = false;
        }
        if (e.InitialPressMouseButton == MouseButton.Middle)
        {
            _isMiddleButtonPressed = false;
        }

        // Release capture only if no buttons are pressed
        if (!props.IsLeftButtonPressed && !props.IsMiddleButtonPressed)
        {
            e.Pointer.Capture(null);
        }
        
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        
        if (_camera == null)
            return;

        var point = e.GetCurrentPoint(this);
        var currentPos = point.Position;
        var delta = currentPos - _lastMousePosition;

        if (_isLeftButtonPressed)
        {
            // Orbit
            _camera.Orbit((float)delta.X * 0.5f, (float)delta.Y * 0.5f);
            RequestNextFrameRendering();
            e.Handled = true;
        }
        else if (_isMiddleButtonPressed)
        {
            // Pan
            _camera.Pan((float)-delta.X, (float)delta.Y);
            RequestNextFrameRendering();
            e.Handled = true;
        }

        _lastMousePosition = currentPos;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        
        if (_camera != null)
        {
            _camera.Zoom((float)e.Delta.Y);
            RequestNextFrameRendering();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        
        if (_camera == null)
            return;

        const float rotateSpeed = 5.0f;
        const float moveSpeed = 0.2f;
        const float zoomSpeed = 0.5f;
        
        switch (e.Key)
        {
            // Arrow keys for orbiting/rotating view
            case Key.Left:
                _camera.Orbit(-rotateSpeed, 0);
                RequestNextFrameRendering();
                break;
            case Key.Right:
                _camera.Orbit(rotateSpeed, 0);
                RequestNextFrameRendering();
                break;
            case Key.Up:
                _camera.Orbit(0, rotateSpeed);
                RequestNextFrameRendering();
                break;
            case Key.Down:
                _camera.Orbit(0, -rotateSpeed);
                RequestNextFrameRendering();
                break;
            
            // WASD for first-person movement
            case Key.W:
                _camera.Move(moveSpeed, 0);  // Move forward
                RequestNextFrameRendering();
                break;
            case Key.S:
                _camera.Move(-moveSpeed, 0); // Move backward
                RequestNextFrameRendering();
                break;
            case Key.A:
                _camera.Move(0, -moveSpeed); // Strafe left
                RequestNextFrameRendering();
                break;
            case Key.D:
                _camera.Move(0, moveSpeed);  // Strafe right
                RequestNextFrameRendering();
                break;
            
            // Q/E for vertical movement
            case Key.Q:
                _camera.Move(0, 0, -moveSpeed); // Move down
                RequestNextFrameRendering();
                break;
            case Key.E:
                _camera.Move(0, 0, moveSpeed);  // Move up
                RequestNextFrameRendering();
                break;
            
            // +/- for zoom
            case Key.OemPlus:
            case Key.Add:
                _camera.Zoom(zoomSpeed);
                RequestNextFrameRendering();
                break;
            case Key.OemMinus:
            case Key.Subtract:
                _camera.Zoom(-zoomSpeed);
                RequestNextFrameRendering();
                break;
            
            // F to frame/reset view to model
            case Key.F:
                if (_model != null)
                {
                    _camera.FrameModel(_model.Center, _model.Radius);
                    RequestNextFrameRendering();
                }
                break;
        }
        
        e.Handled = true;
    }

    public void LoadModel(string path)
    {
        // Cancel any ongoing async load and drop any pending CPU-side model data
        CancelAndDisposeLoadingCts();
        DisposePendingModelData();

        lock (_pendingLoadLock)
        {
            _pendingModelPath = path;
        }
        RequestNextFrameRendering();
    }

    /// <summary>
    /// Requests recreation of render targets (HDR/MSAA/final) on the render thread.
    /// Useful when toggling MSAA/sample count at runtime.
    /// </summary>
    public void RequestFramebufferRecreate()
    {
        var (width, height) = GetPhysicalPixelSize();
        _needsResize = true;
        _pendingWidth = width;
        _pendingHeight = height;
        RequestNextFrameRendering();
    }
    
    /// <summary>
    /// Loads a model asynchronously with progress reporting.
    /// Automatically cancels any previous loading operation.
    /// </summary>
    public async Task LoadModelAsync(string path, IProgress<ModelLoadProgress>? progress = null)
    {
        // Cancel/cleanup any previous loading operation and pending CPU data
        CancelAndDisposeLoadingCts();
        DisposePendingModelData();

        _loadingCts = new CancellationTokenSource();
        var cancellationToken = _loadingCts.Token;
        
        try
        {
            LoadingStatusChanged?.Invoke(true, "Loading...", -1);
            
            // Parse model on background thread
            var loadProgress = new Progress<ModelLoadProgress>(p =>
            {
                _loadingStatus = p.Stage;
                int percent = p.Total > 0 ? (int)(p.Current * 100 / p.Total) : -1;
                LoadingStatusChanged?.Invoke(true, p.Stage, percent);
                progress?.Report(p);
            });
            
            ModelLoadData? data = null;
            try
            {
                data = await ModelLoader.LoadAsync(path, loadProgress, cancellationToken);

                // Check if cancelled before uploading to GPU
                cancellationToken.ThrowIfCancellationRequested();

                // Queue for GPU upload on render thread (dispose any older pending data first)
                lock (_pendingLoadLock)
                {
                    _pendingModelPath = null; // Clear any pending sync load
                    _pendingModelData?.Dispose();
                    _pendingModelData = data;
                    data = null; // ownership transferred to render thread
                }

                RequestNextFrameRendering();
            }
            finally
            {
                // If canceled or failed after data was loaded but before it was queued, free it.
                data?.Dispose();
            }
            
            LoadingStatusChanged?.Invoke(true, "Uploading to GPU...", 0);
        }
        catch (OperationCanceledException)
        {
            // Only hide loading if this was the active load (not replaced by another)
            if (_loadingCts?.Token == cancellationToken)
            {
                LoadingStatusChanged?.Invoke(false, "", -1);
            }
        }
        catch (Exception ex)
        {
            LoadingStatusChanged?.Invoke(false, $"Error: {ex.Message}", -1);
            throw;
        }
    }

    public void LoadEnvironment(string path)
    {
        lock (_pendingLoadLock)
        {
            _pendingEnvironmentPath = path;
        }
        RequestNextFrameRendering();
    }
    
    private unsafe void CreateHDRFramebuffer(int width, int height)
    {
        if (_hdrFBO != 0)
        {
            _gl?.DeleteFramebuffer(_hdrFBO);
            _gl?.DeleteTexture(_hdrColorBuffer);
            if (_depthRBO != 0)
                _gl?.DeleteRenderbuffer(_depthRBO);
        }

        if (_hdrMsaaFBO != 0)
        {
            _gl?.DeleteFramebuffer(_hdrMsaaFBO);
            if (_hdrMsaaColorRBO != 0)
                _gl?.DeleteRenderbuffer(_hdrMsaaColorRBO);
            if (_hdrMsaaDepthRBO != 0)
                _gl?.DeleteRenderbuffer(_hdrMsaaDepthRBO);
        }

        _hdrMsaaFBO = 0;
        _hdrMsaaColorRBO = 0;
        _hdrMsaaDepthRBO = 0;
        _depthRBO = 0;
        
        int maxSamples = 1;
        try
        {
            maxSamples = _gl!.GetInteger(GLEnum.MaxSamples);
        }
        catch
        {
            maxSamples = 1;
        }
        int samples = UseMSAA ? Math.Clamp(MsaaSamples, 1, Math.Max(1, maxSamples)) : 1;

        _hdrFBO = _gl!.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _hdrFBO);
        
        // HDR color buffer
        _hdrColorBuffer = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _hdrColorBuffer);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba16f, 
            (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.Float, (void*)null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, 
            TextureTarget.Texture2D, _hdrColorBuffer, 0);

        // Depth is only required if we render directly into the resolve FBO (no MSAA path).
        if (samples <= 1)
        {
            _depthRBO = _gl.GenRenderbuffer();
            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthRBO);
            _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24,
                (uint)width, (uint)height);
            _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                RenderbufferTarget.Renderbuffer, _depthRBO);
        }
        
        var fbStatus = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (fbStatus != GLEnum.FramebufferComplete)
        {
            Console.WriteLine($"[GLViewport] ERROR: HDR Framebuffer is not complete! Status: {fbStatus}");
            Console.WriteLine($"[GLViewport]   Size: {width}x{height}");
            Console.WriteLine($"[GLViewport]   FBO: {_hdrFBO}, ColorBuffer: {_hdrColorBuffer}, DepthRBO: {_depthRBO}");
        }
        
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        // Create MSAA framebuffer (renderbuffer attachments) for the HDR scene, then resolve into _hdrColorBuffer.
        if (samples > 1)
        {
            _hdrMsaaFBO = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _hdrMsaaFBO);

            _hdrMsaaColorRBO = _gl.GenRenderbuffer();
            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _hdrMsaaColorRBO);
            _gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)samples, InternalFormat.Rgba16f,
                (uint)width, (uint)height);
            _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                RenderbufferTarget.Renderbuffer, _hdrMsaaColorRBO);

            _hdrMsaaDepthRBO = _gl.GenRenderbuffer();
            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _hdrMsaaDepthRBO);
            _gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)samples, InternalFormat.DepthComponent24,
                (uint)width, (uint)height);
            _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                RenderbufferTarget.Renderbuffer, _hdrMsaaDepthRBO);

            var msaaStatus = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (msaaStatus != GLEnum.FramebufferComplete)
            {
                Console.WriteLine($"[GLViewport] ERROR: HDR MSAA Framebuffer is not complete! Status: {msaaStatus}");
                Console.WriteLine($"[GLViewport]   Size: {width}x{height}, Samples: {samples}");
                Console.WriteLine($"[GLViewport]   FBO: {_hdrMsaaFBO}, ColorRBO: {_hdrMsaaColorRBO}, DepthRBO: {_hdrMsaaDepthRBO}");
                // Fall back to non-MSAA path.
                _gl.DeleteFramebuffer(_hdrMsaaFBO);
                _gl.DeleteRenderbuffer(_hdrMsaaColorRBO);
                _gl.DeleteRenderbuffer(_hdrMsaaDepthRBO);
                _hdrMsaaFBO = 0;
                _hdrMsaaColorRBO = 0;
                _hdrMsaaDepthRBO = 0;

                // Ensure resolve FBO has depth for direct rendering.
                if (_depthRBO == 0)
                {
                    _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _hdrFBO);
                    _depthRBO = _gl.GenRenderbuffer();
                    _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthRBO);
                    _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24,
                        (uint)width, (uint)height);
                    _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                        RenderbufferTarget.Renderbuffer, _depthRBO);
                    _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                }
            }

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }
    }

    private unsafe void CreateFinalFramebuffer(int width, int height)
    {
        if (_finalFBO != 0)
        {
            _gl?.DeleteFramebuffer(_finalFBO);
            _gl?.DeleteTexture(_finalColorBuffer);
        }

        _finalFBO = _gl!.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _finalFBO);

        // Composite outputs sRGB-encoded color (plus dithering), so store in an 8-bit target.
        _finalColorBuffer = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _finalColorBuffer);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8,
            (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, (void*)null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _finalColorBuffer, 0);

        var fbStatus = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (fbStatus != GLEnum.FramebufferComplete)
        {
            Console.WriteLine($"[GLViewport] ERROR: Final Framebuffer is not complete! Status: {fbStatus}");
            Console.WriteLine($"[GLViewport]   Size: {width}x{height}");
            Console.WriteLine($"[GLViewport]   FBO: {_finalFBO}, ColorBuffer: {_finalColorBuffer}");
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    // Helper to render scene geometry to specified framebuffer
    private void RenderSceneGeometry(Matrix4x4 model, Matrix4x4 view, Matrix4x4 projection)
    {
        if (_model == null || _pbrShader == null || _gl == null || _camera == null) return;

        int meshCount = 0;

        int ResolveAlphaMode(Assimp.Material assimpMaterial, LoadedMaterialTextures loadedTextures, out float alphaCutoff)
        {
            alphaCutoff = DefaultAlphaCutoff;

            float opacity = MaterialHelper.GetOpacity(assimpMaterial);
            if (opacity < 0.99f)
                return AlphaBlend;

            var albedoTex = loadedTextures.AlbedoMap;
            if (albedoTex != null && albedoTex.HasNonOpaqueAlpha)
            {
                // Heuristic:
                // - Mostly-binary alpha => decals/labels/leaf cutouts => MASK (alpha-test, depth write)
                // - Lots of partial alpha => glass/soft transparency => BLEND
                return albedoTex.IsMostlyBinaryAlpha ? AlphaMask : AlphaBlend;
            }

            return AlphaOpaque;
        }
        
        _pbrShader.Use();
        _pbrShader.SetUniform("uModel", model);
        _pbrShader.SetUniform("uView", view);
        _pbrShader.SetUniform("uProjection", projection);
        _pbrShader.SetUniform("camPos", _camera!.Position);
        
        // Set configurable lighting parameters
        _pbrShader.SetUniform("mainLightIntensity", MainLightIntensity);
        _pbrShader.SetUniform("fillLightIntensity", FillLightIntensity);
        _pbrShader.SetUniform("rimLightIntensity", RimLightIntensity);
        _pbrShader.SetUniform("topLightIntensity", TopLightIntensity);
        _pbrShader.SetUniform("ambientIntensity", AmbientIntensity);
        
        // Set debug mode for shader
        _pbrShader.SetUniform("debugMode", DebugMode);
        
        // Set material override uniforms
        _pbrShader.SetUniform("specularScale", SpecularScale);
        _pbrShader.SetUniform("roughnessOffset", RoughnessOffset);
        _pbrShader.SetUniform("metallicOffset", MetallicOffset);
        _pbrShader.SetUniform("iblIntensity", IblIntensity);
        
        // Setup shadow mapping - use texture unit 8 to avoid conflicts
        if (_shadowMap != null && UseShadows)
        {
            _pbrShader.SetUniform("useShadows", true);
            _pbrShader.SetUniform("shadowStrength", ShadowStrength);
            _pbrShader.SetUniform("lightSpaceMatrix", _shadowMap.LightSpaceMatrix);
            _gl.ActiveTexture(TextureUnit.Texture8);
            _gl.BindTexture(TextureTarget.Texture2D, _shadowMap.DepthMapTexture);
            _pbrShader.SetUniform("shadowMap", 8);
        }
        else
        {
            _pbrShader.SetUniform("useShadows", false);
        }

        // Setup IBL textures
        // Use procedural HDRI when no environment map is explicitly loaded
        bool hasExplicitHDRI = _iblEnvironment != null && _iblEnvironment.EnvironmentMap != 0;
        bool useProceduralHDRI = !hasExplicitHDRI && _proceduralHDRI != null;
        bool hasIBL = (hasExplicitHDRI || useProceduralHDRI) && UseIBL; // Respect UI toggle
        
        // Pass IBL toggle to shader
        _pbrShader.SetUniform("useIBL", hasIBL);

        // Use fixed world-space light direction for consistent lighting
        // Default: light from viewer side-front (camera-relative) to better illuminate faces/details.
        Vector3 mainLightDir = GetMainLightDir();
        
        _pbrShader.SetUniform("lightDir", mainLightDir);
        
        // Bind IBL textures (irradiance, prefilter, BRDF LUT)
        BindIblTextures(hasExplicitHDRI, useProceduralHDRI);

        // Draw opaque + masked meshes (no blending, depth writes on)
        _gl.Disable(EnableCap.Blend);
        _gl.DepthMask(true);
        foreach (var mesh in _model.Meshes)
        {
            int matIndex = mesh.MaterialIndex;
            if (matIndex >= 0 && matIndex < _model.Materials.Count)
            {
                var assimpMaterial = _model.Materials[matIndex];
                var loadedTextures = _model.LoadedTextures[matIndex];
                
                int alphaMode = ResolveAlphaMode(assimpMaterial, loadedTextures, out float alphaCutoff);
                if (alphaMode == AlphaBlend) continue; // Skip blended transparency in opaque pass

                SetupMaterialUniforms(assimpMaterial, loadedTextures, alphaMode, alphaCutoff);
            }
            else
            {
                SetupFallbackMaterial();
            }

            mesh.Draw();
            meshCount++;
        }

        // Draw blended transparent meshes back-to-front (blending on, no depth writes)
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false);
        var camPos = _camera.Position;
        var transparentMeshes = _model.Meshes
            .Select((mesh, index) => new { mesh, index })
            .Where(x => {
                int matIndex = x.mesh.MaterialIndex;
                if (matIndex >= 0 && matIndex < _model.Materials.Count)
                {
                    var m = _model.Materials[matIndex];
                    var t = _model.LoadedTextures[matIndex];
                    int mode = ResolveAlphaMode(m, t, out _);
                    return mode == AlphaBlend;
                }
                return false;
            })
            .OrderByDescending(x => Vector3.Distance(camPos, _model.Center))
            .ToList();

        foreach (var item in transparentMeshes)
        {
            var mesh = item.mesh;
            var assimpMaterial = _model.Materials[mesh.MaterialIndex];
            var loadedTextures = _model.LoadedTextures[mesh.MaterialIndex];

            int alphaMode = ResolveAlphaMode(assimpMaterial, loadedTextures, out float alphaCutoff);
            SetupMaterialUniforms(assimpMaterial, loadedTextures, alphaMode, alphaCutoff);

            mesh.Draw();
            meshCount++;
        }

        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        
        // Render ground plane with simple gray material
        RenderGroundPlane();
    }
    
    /// <summary>
    /// Sets up PBR material uniforms and binds textures for a mesh.
    /// </summary>
    private void SetupMaterialUniforms(Assimp.Material assimpMaterial, LoadedMaterialTextures loadedTextures, int alphaMode, float alphaCutoff)
    {
        // Set PBR material properties from Assimp material
        _pbrShader!.SetUniform("albedo", MaterialHelper.GetAlbedo(assimpMaterial));
        _pbrShader.SetUniform("metallic", MaterialHelper.GetMetallic(assimpMaterial));
        _pbrShader.SetUniform("roughness", MaterialHelper.GetRoughness(assimpMaterial));
        _pbrShader.SetUniform("ao", MaterialHelper.GetAO(assimpMaterial));
        _pbrShader.SetUniform("opacity", MaterialHelper.GetOpacity(assimpMaterial));
        _pbrShader.SetUniform("alphaMode", alphaMode);
        _pbrShader.SetUniform("alphaCutoff", alphaCutoff);
        
        // Bind material textures
        BindMaterialTextures(loadedTextures);
    }
    
    /// <summary>
    /// Sets up fallback material uniforms when no valid material is found.
    /// </summary>
    private void SetupFallbackMaterial()
    {
        _pbrShader!.SetUniform("albedo", new Vector3(0.7f));
        _pbrShader.SetUniform("metallic", 0.0f);
        _pbrShader.SetUniform("roughness", 0.6f);
        _pbrShader.SetUniform("ao", 1.0f);
        _pbrShader.SetUniform("opacity", 1.0f);
        _pbrShader.SetUniform("alphaMode", AlphaOpaque);
        _pbrShader.SetUniform("alphaCutoff", DefaultAlphaCutoff);
        _pbrShader.SetUniform("useAlbedoMap", false);
        _pbrShader.SetUniform("useNormalMap", false);
        _pbrShader.SetUniform("useMetallicMap", false);
        _pbrShader.SetUniform("useRoughnessMap", false);
        _pbrShader.SetUniform("useAOMap", false);
    }
    
    /// <summary>
    /// Binds material textures to appropriate texture units.
    /// </summary>
    private void BindMaterialTextures(LoadedMaterialTextures textures)
    {
        // Albedo (texture unit 0)
        _gl!.ActiveTexture(TextureUnit.Texture0);
        _pbrShader!.SetUniform("useAlbedoMap", textures.AlbedoMap != null);
        if (textures.AlbedoMap != null)
        {
            _gl.BindTexture(TextureTarget.Texture2D, textures.AlbedoMap.Handle);
            _pbrShader.SetUniform("albedoMap", 0);
        }
        else
        {
            _gl.BindTexture(TextureTarget.Texture2D, _iblEnvironment?.BrdfLUT ?? 0);
        }

        // Normal (texture unit 1)
        _pbrShader.SetUniform("useNormalMap", textures.NormalMap != null);
        if (textures.NormalMap != null)
        {
            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(TextureTarget.Texture2D, textures.NormalMap.Handle);
            _pbrShader.SetUniform("normalMap", 1);
        }

        // Metallic (texture unit 2)
        _pbrShader.SetUniform("useMetallicMap", textures.MetallicMap != null);
        if (textures.MetallicMap != null)
        {
            _gl.ActiveTexture(TextureUnit.Texture2);
            _gl.BindTexture(TextureTarget.Texture2D, textures.MetallicMap.Handle);
            _pbrShader.SetUniform("metallicMap", 2);
        }

        // Roughness (texture unit 6)
        _pbrShader.SetUniform("useRoughnessMap", textures.RoughnessMap != null);
        if (textures.RoughnessMap != null)
        {
            _gl.ActiveTexture(TextureUnit.Texture6);
            _gl.BindTexture(TextureTarget.Texture2D, textures.RoughnessMap.Handle);
            _pbrShader.SetUniform("roughnessMap", 6);
        }

        // Ambient Occlusion (texture unit 7)
        _pbrShader.SetUniform("useAOMap", textures.AOMap != null);
        if (textures.AOMap != null)
        {
            _gl.ActiveTexture(TextureUnit.Texture7);
            _gl.BindTexture(TextureTarget.Texture2D, textures.AOMap.Handle);
            _pbrShader.SetUniform("aoMap", 7);
        }
    }
    
    /// <summary>
    /// Binds IBL textures (irradiance map, prefilter map, BRDF LUT) to texture units 3, 4, 5.
    /// </summary>
    private void BindIblTextures(bool useExplicitHdri, bool useProceduralHdri)
    {
        uint irradianceMap, prefilterMap;
        
        if (useExplicitHdri && _iblEnvironment != null)
        {
            irradianceMap = _iblEnvironment.IrradianceMap;
            prefilterMap = _iblEnvironment.PrefilterMap;
        }
        else if (useProceduralHdri && _proceduralHDRI != null)
        {
            irradianceMap = _proceduralHDRI.IrradianceMap;
            prefilterMap = _proceduralHDRI.PrefilterMap;
        }
        else if (_iblEnvironment != null)
        {
            // Fallback: bind dummy textures to prevent sampler errors
            irradianceMap = _iblEnvironment.IrradianceMap;
            prefilterMap = _iblEnvironment.PrefilterMap;
        }
        else
        {
            return; // No IBL available
        }
        
        _gl!.ActiveTexture(TextureUnit.Texture3);
        _gl.BindTexture(TextureTarget.TextureCubeMap, irradianceMap);
        _pbrShader!.SetUniform("irradianceMap", 3);

        _gl.ActiveTexture(TextureUnit.Texture4);
        _gl.BindTexture(TextureTarget.TextureCubeMap, prefilterMap);
        _pbrShader.SetUniform("prefilterMap", 4);

        // BRDF LUT always comes from IBLEnvironment
        if (_iblEnvironment != null)
        {
            _gl.ActiveTexture(TextureUnit.Texture5);
            _gl.BindTexture(TextureTarget.Texture2D, _iblEnvironment.BrdfLUT);
            _pbrShader.SetUniform("brdfLUT", 5);
        }
    }
    
    /// <summary>
    /// Renders the ground plane if conditions are met.
    /// </summary>
    private void RenderGroundPlane()
    {
        if (_groundPlane == null || !ShowGround || _camera == null || _model == null)
            return;
            
        // Calculate view direction for ground visibility check
        Vector3 viewDir = Vector3.Normalize(_camera.Target - _camera.Position);
        
        // Only show ground when camera is above the ground AND looking downward
        float groundY = _model.BoundsMin.Y - 0.01f;
        bool cameraAboveGround = _camera.Position.Y > groundY;
        bool lookingDownward = viewDir.Y < 0.1f;
        
        if (!cameraAboveGround || !lookingDownward)
            return;
        
        _pbrShader!.SetUniform("albedo", new Vector3(0.4f, 0.4f, 0.4f));
        _pbrShader.SetUniform("metallic", 0.0f);
        _pbrShader.SetUniform("roughness", 0.85f);
        _pbrShader.SetUniform("ao", 1.0f);
        _pbrShader.SetUniform("opacity", 1.0f);
        _pbrShader.SetUniform("alphaMode", AlphaOpaque);
        _pbrShader.SetUniform("alphaCutoff", DefaultAlphaCutoff);
        _pbrShader.SetUniform("useAlbedoMap", false);
        _pbrShader.SetUniform("useNormalMap", false);
        _pbrShader.SetUniform("useMetallicMap", false);
        _pbrShader.SetUniform("useRoughnessMap", false);
        _pbrShader.SetUniform("useAOMap", false);
        
        _groundPlane.Draw();
    }
    
    /// <summary>
    /// Processes any pending model load (async or sync path).
    /// Called once per frame from OnOpenGlRender.
    /// </summary>
    private void ProcessPendingModelLoad()
    {
        // Check for pending data from either loading path
        ModelLoadData? asyncData = null;
        string? syncPath = null;
        
        lock (_pendingLoadLock)
        {
            // Async path takes priority (it's already parsed)
            if (_pendingModelData != null)
            {
                asyncData = _pendingModelData;
                _pendingModelData = null;
            }
            else if (_pendingModelPath != null)
            {
                syncPath = _pendingModelPath;
                _pendingModelPath = null;
            }
        }
        
        // Nothing to load
        if (asyncData == null && syncPath == null)
            return;
        
        // Keep reference to old model for disposal
        Model? oldModel = _model;
        _model = null;
        
        try
        {
            if (asyncData != null)
            {
                // Async path: model was pre-parsed on background thread, just upload to GPU
                _model = Model.CreateFromLoadData(_gl!, asyncData);
                LoadingStatusChanged?.Invoke(false, "", 100);
            }
            else if (syncPath != null)
            {
                // Sync path: parse and upload in one step (blocks render thread)
                _model = Model.LoadFromFile(_gl!, syncPath);
            }
            
            if (_model != null)
            {
                CreateGroundPlaneForModel(_model);
                _camera?.FrameModel(_model.Center, _model.Radius);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GLViewport] ERROR loading model: {ex.Message}");
            LoadingStatusChanged?.Invoke(false, $"Error: {ex.Message}", -1);
        }
        finally
        {
            // Dispose old model and any temporary load data
            oldModel?.Dispose();
            asyncData?.Dispose();
            
            // Hint GC to collect freed memory from large model data
            // This is important because models can be hundreds of MB
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
        }
    }
    
    private void CreateGroundPlaneForModel(Model model)
    {
        if (_gl == null) return;
        
        // Dispose old ground plane if exists
        _groundPlane?.Dispose();
        
        // Position ground at the bottom of the model's bounding box
        float groundY = model.BoundsMin.Y - 0.01f; // Slightly below to avoid z-fighting
        float groundSize = model.Radius * 5.0f; // Size relative to model
        
        // Center the ground plane on the model's X/Z center
        float centerX = model.Center.X;
        float centerZ = model.Center.Z;
        
        var groundVertices = new Vertex[]
        {
            new Vertex { Position = new Vector3(centerX - groundSize, groundY, centerZ - groundSize), Normal = new Vector3(0, 1, 0), TexCoord = new Vector2(0, 0) },
            new Vertex { Position = new Vector3(centerX + groundSize, groundY, centerZ - groundSize), Normal = new Vector3(0, 1, 0), TexCoord = new Vector2(1, 0) },
            new Vertex { Position = new Vector3(centerX + groundSize, groundY, centerZ + groundSize), Normal = new Vector3(0, 1, 0), TexCoord = new Vector2(1, 1) },
            new Vertex { Position = new Vector3(centerX - groundSize, groundY, centerZ + groundSize), Normal = new Vector3(0, 1, 0), TexCoord = new Vector2(0, 1) }
        };
        var groundIndices = new uint[] { 0, 1, 2, 0, 2, 3 };
        _groundPlane = new Mesh(_gl, groundVertices, groundIndices);
    }
}
