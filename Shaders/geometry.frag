out vec4 gPosition;
out vec4 gNormal;
out vec4 gAlbedo;

in vec3 vFragPosView;
in vec3 vNormalView;
in vec2 vTexCoord;

// Albedo is optional for SSAO, but kept for completeness.
uniform bool useAlbedoMap;
uniform sampler2D albedoMap;
uniform vec3 albedo;

// Alpha-cutout support so masked materials (grilles, decals)
// don't occlude in SSAO where they are actually transparent.
uniform bool alphaMask;
uniform float alphaCutoff;

void main()
{
    vec4 baseSample = vec4(albedo, 1.0);
    if (useAlbedoMap)
        baseSample = texture(albedoMap, vTexCoord);

    if (alphaMask && baseSample.a < alphaCutoff)
        discard;

    gPosition = vec4(vFragPosView, 1.0);
    gNormal = vec4(normalize(vNormalView), 1.0);
    gAlbedo = vec4(baseSample.rgb, 1.0);
}


