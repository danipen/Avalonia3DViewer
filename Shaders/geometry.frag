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

void main()
{
    gPosition = vec4(vFragPosView, 1.0);
    gNormal = vec4(normalize(vNormalView), 1.0);

    vec3 baseColor = albedo;
    if (useAlbedoMap)
        baseColor = texture(albedoMap, vTexCoord).rgb;

    gAlbedo = vec4(baseColor, 1.0);
}


