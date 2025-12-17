using System;
using System.Numerics;

namespace Avalonia3DViewer.Rendering;

public class Camera
{
    public Vector3 Position { get; set; } = new(0, 0, 5);
    public Vector3 Target { get; set; } = Vector3.Zero;
    public Vector3 Up { get; set; } = Vector3.UnitY;
    
    public float Fov { get; set; } = 45.0f;
    public float AspectRatio { get; set; } = 16.0f / 9.0f;
    
    public float Far => Math.Max(1000.0f, _distance * 100.0f);
    public float Near => Math.Max(Far / 10000.0f, _distance * 0.01f);

    private float _distance = 5.0f;
    private float _yaw = 0.0f;
    private float _pitch = 0.0f;
    private Vector3 _targetPosition = Vector3.Zero;
    
    private const float MinOrbitDistance = 0.1f;
    private const float DefaultYaw = 125.0f;
    private const float DefaultPitch = 9.0f;

    public Camera()
    {
        UpdatePosition();
    }

    public Matrix4x4 GetViewMatrix() => Matrix4x4.CreateLookAt(Position, Target, Up);

    public Matrix4x4 GetProjectionMatrix() =>
        Matrix4x4.CreatePerspectiveFieldOfView(Fov * MathF.PI / 180.0f, AspectRatio, Near, Far);

    public void Orbit(float deltaYaw, float deltaPitch)
    {
        _yaw += deltaYaw;
        _pitch = Math.Clamp(_pitch + deltaPitch, -89.0f, 89.0f);
        UpdatePosition();
    }

    public void Pan(float deltaX, float deltaY)
    {
        Vector3 forward = Vector3.Normalize(Target - Position);
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Up));
        Vector3 up = Vector3.Cross(right, forward);

        float speed = Math.Max(0.001f, _distance * 0.001f);
        _targetPosition += right * deltaX * speed + up * deltaY * speed;
        Target = _targetPosition;
        UpdatePosition();
    }

    public void Zoom(float delta)
    {
        float minZoomSpeed = 0.05f;
        float zoomFactor = Math.Max(minZoomSpeed, _distance * 0.15f);
        float newDistance = _distance - delta * zoomFactor;
        
        if (newDistance < MinOrbitDistance)
        {
            DollyForward(newDistance);
        }
        else
        {
            _distance = newDistance;
        }
        
        UpdatePosition();
    }

    private void DollyForward(float newDistance)
    {
        Vector3 forward = Vector3.Normalize(Target - Position);
        float pushAmount = MinOrbitDistance - newDistance;
        _targetPosition += forward * pushAmount;
        Target = _targetPosition;
        _distance = MinOrbitDistance;
    }

    public void SetTarget(Vector3 target)
    {
        _targetPosition = target;
        Target = target;
        UpdatePosition();
    }

    public void SetDistance(float distance)
    {
        _distance = Math.Max(MinOrbitDistance, distance);
        UpdatePosition();
    }
    
    public float GetDistance() => _distance;

    public void FrameModel(Vector3 center, float radius)
    {
        _yaw = DefaultYaw;
        _pitch = DefaultPitch;
        
        _targetPosition = center;
        Target = center;
        
        float fovRad = Fov * MathF.PI / 180.0f;
        float distanceToFit = radius / MathF.Sin(fovRad / 2.0f);
        _distance = Math.Max(MinOrbitDistance, distanceToFit * 1.1f);
        
        UpdatePosition();
    }

    public void ResetOrientation()
    {
        _yaw = DefaultYaw;
        _pitch = DefaultPitch;
        UpdatePosition();
    }

    public void Move(float forward, float strafe, float vertical = 0)
    {
        float speed = Math.Max(0.1f, _distance * 0.5f);
        
        float yawRad = _yaw * MathF.PI / 180.0f;
        var forwardDir = Vector3.Normalize(new Vector3(-MathF.Cos(yawRad), 0, -MathF.Sin(yawRad)));
        var rightDir = new Vector3(-forwardDir.Z, 0, forwardDir.X);
        
        Vector3 movement = forwardDir * forward * speed 
                         + rightDir * strafe * speed 
                         + Vector3.UnitY * vertical * speed;
        
        _targetPosition += movement;
        Target = _targetPosition;
        UpdatePosition();
    }

    private void UpdatePosition()
    {
        float yawRad = _yaw * MathF.PI / 180.0f;
        float pitchRad = _pitch * MathF.PI / 180.0f;

        Position = _targetPosition + new Vector3(
            _distance * MathF.Cos(pitchRad) * MathF.Cos(yawRad),
            _distance * MathF.Sin(pitchRad),
            _distance * MathF.Cos(pitchRad) * MathF.Sin(yawRad)
        );
    }
}
