using System;
using System.Linq;
using Prowl.Echo;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox;

/// <summary>
/// The player's visible body: <see cref="GameManager.PlayerModel"/> driven by <see cref="GameManager.PlayerAnimation"/> from the
/// controller's velocity. Shown in third person, a shadow only in first person.
/// </summary>
public sealed partial class Player
{
    // Where each gait sits in the graph's blend spaces, in m/s. Match models/player/Player.animgraph.
    private const float WalkSpeed = 2f, RunSpeed = 4.5f, SprintSpeed = 7.5f, CrouchSpeed = 1.6f;

    // Hand rotations relative to the held model (x right, y up, z forward). At rest every bone of the model
    // has no rotation: fingers point out sideways, palms face down and thumbs forward.
    // Right: fingers forward and a little down along the grip, palm against it, thumb up.
    private static readonly Quaternion RightGrip = Quaternion.FromEuler(25f, 0f, 0f) * Quaternion.LookRotation(Float3.UnitY, Float3.UnitX);
    // Left: palm up under the front, fingers forward and in toward the middle, thumb out.
    private static readonly Quaternion LeftGrip = Quaternion.FromEuler(0f, 25f, 0f) * Quaternion.LookRotation(-Float3.UnitX, -Float3.UnitY);

    private GameObject _body;
    private Animator _animator;
    private SkinnedMeshRenderer[] _bodyRenderers = [];

    private float _bodyYaw;
    private bool _bodyTurning;
    private Float2 _move;
    private float _moveWeight, _rate = 1f;
    private float _airTime, _jumpPulse, _hardLandPulse;

    // Holding equipment: the right shoulder in body space from the last animated frame, and where the held
    // model goes this frame, which the hands reach for.
    private Transform _shoulder;
    private Float3 _shoulderLocal;
    private bool _haveShoulder;
    private Float3 _holdPosition;
    private Quaternion _holdRotation = Quaternion.Identity;
    private float _holdWeight, _supportWeight;

    /// <summary>The way the body faces, in degrees. It trails the view while standing still.</summary>
    public float BodyYaw => _bodyYaw;

    private void SetupBody()
    {
        // Set on the GameManager, which keeps them loaded with the scene.
        PrefabAsset prefab = GameManager.Instance.PlayerModel;
        AnimationGraph graph = GameManager.Instance.PlayerAnimation;
        if (prefab.IsNotValid()) return;

        // The model keeps its own root name, which the rig binds by. The holder is what turns and moves.
        _body = new GameObject("Body");
        _body.SetParent(GameObject, false);
        GameObject model = GameObject.Instantiate(prefab);
        model.SetParent(_body, false);
        _animator = model.GetComponentInChildren<Animator>();
        if (_animator.IsValid() && graph.IsValid()) _animator.Graph = graph;
        _bodyRenderers = _body.GetComponentsInChildren<SkinnedMeshRenderer>().ToArray();
        _body.AddComponent<MapLit>();
        _shoulder = FindBone(model.Transform, "RightUpperArm");

        _bodyYaw = Yaw;
        PlaceBody(0f);
        UpdateBodyVisibility();
    }

    private void OnJumpedBody() => _jumpPulse = 0.15f;

    private void OnLandedBody(float fallSpeed)
    {
        if (fallSpeed > 9f) _hardLandPulse = 0.15f;
    }

    /// <summary>Runs after movement each frame, so the animator (which runs after every Update) sees this frame's state.</summary>
    private void UpdateBody(float dt)
    {
        if (_body.IsNotValid()) return;

        Float3 vel = Controller.Velocity;
        var flat = new Float3(vel.X, 0f, vel.Z);
        float speed = Float3.Length(flat);
        bool noclip = Controller.Mode == Controller.Noclip;
        bool moving = speed > 0.3f;

        UpdateBodyYaw(dt, moving);
        PlaceBody(dt);

        // Velocity in the body's frame: X right, Y forward.
        Quaternion facing = Quaternion.FromEuler(0f, _bodyYaw, 0f);
        Float3 forward = facing * Float3.UnitZ, right = facing * Float3.UnitX;
        var local = new Float2(Float3.Dot(flat, right), Float3.Dot(flat, forward));

        // The blend spaces have no centre: idle is faded in underneath, so only the direction matters at low speed.
        Float2 target = speed > 0.05f ? local / speed * MathF.Max(speed, Controller.IsDucked ? CrouchSpeed : WalkSpeed) : _move;
        _move = Maths.Lerp(_move, target, Damp(12f, dt));
        _moveWeight = Maths.Lerp(_moveWeight, Smooth01(speed / 1.4f), Damp(10f, dt));

        // Playback rate so the feet roughly keep up with the ground.
        float gaitRate = Controller.IsDucked
            ? Math.Clamp(1.35f * speed / CrouchSpeed, 0.9f, 1.8f)
            : speed < WalkSpeed ? 1.15f
            : speed < RunSpeed ? Maths.Lerp(1.15f, 1f, (speed - WalkSpeed) / (RunSpeed - WalkSpeed))
            : speed < SprintSpeed ? Maths.Lerp(1f, 1.1f, (speed - RunSpeed) / (SprintSpeed - RunSpeed))
            : 1.1f * speed / SprintSpeed;
        _rate = Maths.Lerp(_rate, Maths.Lerp(1f, gaitRate, _moveWeight), Damp(8f, dt));

        // A short grace before counting as airborne, so stairs and bumps do not flash the fall pose.
        _airTime = Controller.IsGrounded && !noclip ? 0f : _airTime + dt;
        _jumpPulse = MathF.Max(0f, _jumpPulse - dt);
        _hardLandPulse = MathF.Max(0f, _hardLandPulse - dt);

        if (_animator.IsNotValid() || !_animator.IsGraph) return;
        _animator.SetFloat("MoveX", _move.X);
        _animator.SetFloat("MoveY", _move.Y);
        _animator.SetFloat("MoveWeight", _moveWeight);
        _animator.SetFloat("Rate", _rate);
        _animator.SetBool("Crouch", Controller.IsDucked && !noclip);
        _animator.SetBool("Grounded", _airTime < 0.15f);
        _animator.SetBool("Jump", _jumpPulse > 0f);
        _animator.SetBool("HardLand", _hardLandPulse > 0f);

        UpdateHold(dt);
    }

    /// <summary>Works out where the held equipment sits and points the head, chest and hands at it.</summary>
    private void UpdateHold(float dt)
    {
        ViewModel held = WorldModel;
        bool holding = held.IsValid() && held.HasModel;
        _holdWeight = Maths.Lerp(_holdWeight, holding ? 1f : 0f, Damp(10f, dt));
        _supportWeight = Maths.Lerp(_supportWeight, holding && held.Support.HasValue ? 1f : 0f, Damp(10f, dt));

        Float3 aim = Forward;
        Float3 eye = EyePosition;
        Float3 aimPoint = Trace(60f, out RaycastHit hit) && hit.Distance > 1.5f ? hit.Point : eye + aim * 60f;
        Quaternion aimRotation = Quaternion.LookRotation(aim, Float3.UnitY);

        // The shoulder as the last frame left it, carried along with the body so moving does not drag it behind.
        Float3 shoulder = _haveShoulder
            ? _body.Transform.TransformPoint(_shoulderLocal)
            : _body.Transform.Position + Float3.UnitY * (Controller.EyeHeight - 0.2f) + _body.Transform.Right * 0.18f;

        if (holding)
        {
            _holdPosition = shoulder + aimRotation * held.Hold;
            // Point the muzzle at what is aimed at rather than parallel to the view, so shots meet the crosshair.
            Float3 toAim = aimPoint - _holdPosition;
            _holdRotation = Float3.Length(toAim) > 0.5f ? Quaternion.LookRotation(Float3.Normalize(toAim), Float3.UnitY) : aimRotation;
        }

        if (_animator.IsNotValid() || !_animator.IsGraph) return;
        _animator.SetTarget("AimTarget", Prowl.Motion.Target.FromWorld(new Prowl.Vector.Spatial.Transform3D(eye + aim * 10f, Quaternion.Identity, Float3.One)));
        _animator.SetFloat("AimWeight", Maths.Lerp(0.55f, 1f, _holdWeight));
        // With nothing held the targets go unset, which also turns off the hand rotation constraints.
        if (!holding)
        {
            _animator.SetTarget("RightHandTarget", default);
            _animator.SetTarget("LeftHandTarget", default);
        }
        else
        {
            float scale = held.Scale;
            _animator.SetTarget("RightHandTarget", Prowl.Motion.Target.FromWorld(new Prowl.Vector.Spatial.Transform3D(_holdPosition + _holdRotation * (held.Grip * scale), _holdRotation * RightGrip, Float3.One)));
            if (held.Support is Float3 support)
                _animator.SetTarget("LeftHandTarget", Prowl.Motion.Target.FromWorld(new Prowl.Vector.Spatial.Transform3D(_holdPosition + _holdRotation * (support * scale), _holdRotation * LeftGrip, Float3.One)));
            else
                _animator.SetTarget("LeftHandTarget", default);
        }
        _animator.SetFloat("RightHandWeight", _holdWeight);
        _animator.SetFloat("LeftHandWeight", _supportWeight);
    }

    /// <summary>After animation: remember the shoulder and put the held equipment where the hands went.</summary>
    private void LateUpdateBody()
    {
        if (_body.IsValid() && _shoulder != null)
        {
            _shoulderLocal = _body.Transform.InverseTransformPoint(_shoulder.Position);
            _haveShoulder = true;
        }

        if (WorldModel.IsNotValid()) return;
        WorldModel.Transform.Position = _holdPosition;
        WorldModel.Transform.Rotation = _holdRotation;
    }

    private static Transform FindBone(Transform root, string name)
    {
        if (root.GameObject.Name == name) return root;
        for (int i = 0; i < root.ChildCount; i++)
            if (FindBone(root.GetChild(i), name) is { } found) return found;
        return null;
    }

    private void UpdateBodyYaw(float dt, bool moving)
    {
        float delta = ((Yaw - _bodyYaw) % 360f + 540f) % 360f - 180f;

        // Moving, the body faces the view. Standing, it lets the view wander and turns once it looks too far round.
        if (moving || MathF.Abs(delta) > 70f) _bodyTurning = true;
        else if (MathF.Abs(delta) < 3f) _bodyTurning = false;

        if (_bodyTurning) _bodyYaw += delta * Damp(moving ? 12f : 7f, dt);
        _bodyYaw = (_bodyYaw % 360f + 360f) % 360f;
    }

    private void PlaceBody(float dt)
    {
        _body.Transform.Position = Transform.Position;
        _body.Transform.Rotation = Quaternion.FromEuler(0f, _bodyYaw, 0f);
    }

    private void UpdateBodyVisibility()
    {
        // In first person the body still casts its shadow.
        ShadowCasting mode = _orbitShown ? ShadowCasting.On : ShadowCasting.ShadowsOnly;
        foreach (SkinnedMeshRenderer renderer in _bodyRenderers)
            if (renderer.IsValid()) renderer.ShadowCasting = mode;
    }

    private static float Damp(float rate, float dt) => 1f - MathF.Exp(-rate * dt);

    private static float Smooth01(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }
}
