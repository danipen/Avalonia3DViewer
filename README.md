# Avalonia 3D Viewer

A cross-platform, real-time 3D model viewer built with [Avalonia UI](https://avaloniaui.net/) and OpenGL.

![Demo](demo.gif) It features a physically-based rendering (PBR) pipeline with image-based lighting, post-processing effects, and an interactive control panel — all running natively on Windows, macOS, and Linux.

---

## Features

### Rendering Pipeline
- **PBR shading** with full material support: albedo, normal maps, metallic, roughness, and ambient occlusion
- **Image-Based Lighting (IBL)** — load any equirectangular HDR/EXR image as an environment; a built-in procedural sky is used as a fallback
- **Four-light rig** — key, fill, rim, and top lights, each with adjustable intensity
- **Directional shadow mapping** with a shadow-catcher ground plane (receives shadows but stays invisible)
- **Alpha modes** — opaque, masked (cutout transparency), and blended

### Post-Processing
| Effect | Details |
|--------|---------|
| HDR pipeline | Dual-framebuffer: MSAA scene + HDR composite |
| MSAA | 2×, 4×, or 8× multisampling |
| Tonemapping | Exposure compensation |
| Bloom | Configurable intensity |
| SSAO | Screen-space ambient occlusion |
| FXAA | Fast approximate anti-aliasing with tunable parameters |

### Camera & Navigation
- **Orbit** — left-drag to rotate (yaw / pitch)
- **Pan** — middle-drag
- **Zoom** — scroll wheel, with dolly-forward when close to the surface
- **Frame to fit** — automatically centers and scales the camera to the loaded model

### File Format Support
Powered by [Assimp](https://github.com/assimp/assimp), the viewer can open most common 3D formats:

`OBJ` `FBX` `glTF` `glB` `DAE` `3DS` `Blend` `STL`

Environment maps: `HDR` `EXR` `JPG` `JPEG` (equirectangular projection)

### UI Controls
The side panel exposes over 30 parameters in real time:

- Exposure and tonemapping
- Per-light intensity (key, fill, rim, top, ambient)
- Key-light direction bias (side / up)
- Shadow strength
- SSAO and bloom intensity
- FXAA sharpness
- Specular, roughness, and metallic offsets
- IBL intensity
- Light / dark background toggle
- Debug modes: texture visualization, IBL-only rendering, material inspection
- Built-in model library for quick access to sample scenes

---

## Getting Started

### Prerequisites
- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- A GPU with OpenGL 3.3+ support

### Build & Run

```bash
git clone https://github.com/your-username/Avalonia3DViewer.git
cd Avalonia3DViewer
dotnet run
```

That's it — no native dependency installation required. All rendering is handled via [Silk.NET](https://github.com/dotnet/Silk.NET).

---

## Project Structure

```
Avalonia3DViewer/
├── Rendering/          # Camera, mesh, model loader, IBL, textures
├── Shaders/            # Compiled GLSL shaders (PBR, shadows, post-FX)
├── ShadersSrc/         # GLSL source files
├── PostProcessing/     # Bloom, SSAO, FXAA, composite passes
├── Controls/           # Avalonia UI controls and view models
├── Diagnostics/        # Performance monitoring
└── sample-models/      # Bundled example scenes
```

### Key Source Files

| File | Purpose |
|------|---------|
| `Rendering/ModelLoader.cs` | Assimp-backed async model loading, texture caching |
| `Rendering/IBLEnvironment.cs` | Equirectangular-to-cubemap conversion, irradiance |
| `Rendering/Camera.cs` | Orbit, pan, zoom, frame-to-fit |
| `Shaders/pbr.frag` | Core PBR fragment shader |
| `Shaders/composite.frag` | HDR composite + tonemapping |
| `PostProcessing/` | Bloom extract → blur → composite; SSAO; FXAA |

---

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| [Avalonia](https://avaloniaui.net/) | 11.3 | Cross-platform UI framework |
| [Silk.NET](https://github.com/dotnet/Silk.NET) | 2.21 | OpenGL bindings |
| [AssimpNetter](https://github.com/assimp/assimp-net) | 6.0 | 3D model parsing |
| [SixLabors.ImageSharp](https://github.com/SixLabors/ImageSharp) | 3.1 | Texture decoding |

---

## License

MIT
