#version 330 core

layout (location = 0) in vec3 aPos;
layout (location = 1) in vec3 aNormal;
layout (location = 2) in vec2 aTexCoord;

out VS_OUT
{
    vec3 FragPosView;
    vec3 NormalView;
    vec2 TexCoord;
} vs_out;

uniform mat4 model;
uniform mat4 view;
uniform mat4 projection;

void main()
{
    vec4 worldPos = model * vec4(aPos, 1.0);
    vec4 viewPos  = view * worldPos;

    // View-space position & normal (for SSAO)
    vs_out.FragPosView = viewPos.xyz;

    mat3 normalMatrix = mat3(transpose(inverse(view * model)));
    vs_out.NormalView = normalize(normalMatrix * aNormal);

    vs_out.TexCoord = aTexCoord;

    gl_Position = projection * viewPos;
}


