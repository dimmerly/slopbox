using System;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;
using Slopbox.Mounting;
using Slopbox.Mounting.Source;

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

    /// <summary>Which foot falls next, for games whose surfaces sound each foot differently.</summary>
    private bool _leftFoot;

    /// <summary>
    /// A footstep for the surface underfoot: the mounted game's own step sound for a Source material's surface,
    /// otherwise the game's step sound for what the material or object is named as.
    /// </summary>
    private void Footstep(float gain)
    {
        _leftFoot = !_leftFoot;
        string surface = "grass";
        Float3 origin = Transform.Position + Float3.UnitY * 0.25f;
        if (Scene.Current.Physics.Raycast(origin, -Float3.UnitY, out RaycastHit hit, 0.8f, QueryFilter.Default))
        {
            Material material = MaterialSurfaces.MaterialAt(hit, origin, -Float3.UnitY);
            string surfaceProp = MaterialSurfaces.Get(material);
            if (surfaceProp != null && SourceFootsteps.Find(surfaceProp, _leftFoot) is { } step)
            {
                GameAudio.PlayClip(step.Clip, gain * step.Volume * 0.9f, step.Pitch, Transform.Position);
                return;
            }

            GameObject ground = hit.Transform != null ? hit.Transform.GameObject : null;
            surface = GameAudio.SurfaceOf(surfaceProp) ?? GameAudio.SurfaceOf(material.IsValid() ? material.Name : null)
                ?? GameAudio.Surface(ground, "grass");
        }
        if (surface == "glass") surface = "concrete";
        GameAudio.Variant("step_" + surface, surface == "grass" ? 5 : 4, gain * 0.22f);
    }
}
