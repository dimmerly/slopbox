using System;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox;

public sealed partial class Player
{
    private float _stepDistance;

    private void OnJumped()
    {
        Footstep(0.2f);
        _stepDistance = 0f;
    }

    private void UpdateMovementAudio(float dt)
    {
        if (dt <= 0f || Controller.Mode != Controller.Walk || !Controller.IsGrounded)
        {
            _stepDistance = 0f;
            return;
        }
        Float3 velocity = Controller.Velocity;
        float speed = MathF.Sqrt(velocity.X * velocity.X + velocity.Z * velocity.Z);
        if (speed < 0.3f) { _stepDistance = 0f; return; }
        _stepDistance += speed * dt;
        float stride = Controller.IsDucked ? 1.25f : 1.8f;
        if (_stepDistance < stride) return;
        _stepDistance %= stride;
        Footstep(Controller.IsDucked ? 0.1f : Math.Clamp(speed * 0.055f, 0.12f, 0.4f));
    }

    private void Footstep(float gain)
    {
        string surface = "grass";
        if (Scene.Current.Physics.Raycast(Transform.Position + Float3.UnitY * 0.25f, -Float3.UnitY,
            out RaycastHit hit, 0.8f, QueryFilter.Default))
            surface = GameAudio.Surface(hit.Transform != null ? hit.Transform.GameObject : null, "grass");
        if (surface == "glass") surface = "concrete";
        GameAudio.Variant("step_" + surface, surface == "grass" ? 5 : 4, gain * 0.22f);
    }
}
