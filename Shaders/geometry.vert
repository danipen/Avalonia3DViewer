in vec3 aPos;
in vec3 aNormal;
in vec2 aTexCoord;

out vec3 vFragPosView;
out vec3 vNormalView;
out vec2 vTexCoord;

uniform mat4 model;
uniform mat4 view;
uniform mat4 projection;

void main()
{
    vec4 worldPos = model * vec4(aPos, 1.0);
    vec4 viewPos  = view * worldPos;

    // View-space position & normal (for SSAO)
    vFragPosView = viewPos.xyz;

    mat3 normalMatrix = mat3(transpose(inverse(view * model)));
    vNormalView = normalize(normalMatrix * aNormal);

    vTexCoord = aTexCoord;

    gl_Position = projection * viewPos;
}


