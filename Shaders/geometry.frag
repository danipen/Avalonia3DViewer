out vec4 gPosition;
out vec4 gNormal;
out vec4 gAlbedo;

in VS_OUT
{
    vec3 FragPosView;
    vec3 NormalView;
    vec2 TexCoord;
} fs_in;

// Albedo is optional for SSAO, but kept for completeness.
uniform bool useAlbedoMap;
uniform sampler2D albedoMap;
uniform vec3 albedo;

void main()
{
    gPosition = vec4(fs_in.FragPosView, 1.0);
    gNormal = vec4(normalize(fs_in.NormalView), 1.0);

    vec3 baseColor = albedo;
    if (useAlbedoMap)
        baseColor = texture(albedoMap, fs_in.TexCoord).rgb;

    gAlbedo = vec4(baseColor, 1.0);
}


