in vec3 aPosition;
in vec3 aNormal;
in vec2 aTexCoord;
in vec3 aTangent;
in vec3 aBitangent;

out vec3 vFragPos;
out vec3 vNormal;
out vec2 vTexCoord;
out mat3 vTBN;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;

void main()
{
    vec4 worldPos = uModel * vec4(aPosition, 1.0);
    vFragPos = worldPos.xyz;
    
    // Transform normal to world space
    mat3 normalMatrix = transpose(inverse(mat3(uModel)));
    vec3 T = normalize(normalMatrix * aTangent);
    vec3 N = normalize(normalMatrix * aNormal);
    T = normalize(T - dot(T, N) * N); // Re-orthogonalize
    vec3 B = cross(N, T);
    
    vTBN = mat3(T, B, N);
    vNormal = N;
    vTexCoord = aTexCoord;
    
    gl_Position = uProjection * uView * worldPos;
}
