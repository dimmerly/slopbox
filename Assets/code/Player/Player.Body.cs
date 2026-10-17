using System;
using System.Linq;
using Prowl.Echo;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox;

/// <summary>
/// The player's visible body: Half-Life 2's citizen when the game is mounted, else <see cref="GameManager.PlayerModel"/>,
/// driven by <see cref="GameManager.PlayerAnimation"/> from the controller's velocity. Shown in third person, a shadow
/// only in first person.
/// </summary>
public sealed partial class Player
{
    // Where each gait sits in the graph's blend spaces, in m/s. Match models/player/Player.animgraph.
    private const float WalkSpeed = 2f, RunSpeed = 4.5f, SprintSpeed = 7.5f, CrouchSpeed = 1.6f;

    // Hand rotations relative to the held model (x right, y up, z forward), as turns from the T-pose rest:
    // fingers out sideways, palms down and thumbs forward. The rig's own rest rotation goes on top.
    // Right: fingers forward and a little down along the grip, palm against it, thumb up.
    private static readonly Quaternion RightGrip = Quaternion.FromEuler(25f, 0f, 0f) * Quaternion.LookRotation(Float3.UnitY, Float3.UnitX);
    // Left: palm up under the front, fingers forward and in toward the middle, thumb out.
    private static readonly Quaternion LeftGrip = Quaternion.FromEuler(0f, 25f, 0f) * Quaternion.LookRotation(-Float3.UnitX, -Float3.UnitY);

    // The citizen, from Half-Life 2 or the copy Garry's Mod ships with.
    private const string CitizenModel = "models/humans/group01/male_07.mdl";
    private static readonly string[] CitizenGames = ["hl2", "garrysmod"];

    private GameObject _body;
    private bool _bodyIsCitizen;
    private int _bodyMountVersion;
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
    private Quaternion _rightHandRest = Quaternion.Identity, _leftHandRest = Quaternion.Identity;
    private Float3 _holdPosition;
    private Quaternion _holdRotation = Quaternion.Identity;
    private float _holdWeight, _supportWeight;

    /// <summary>The way the body faces, in degrees. It trails the view while standing still.</summary>
    public float BodyYaw => _bodyYaw;

    private void SetupBody()
    {
        // Games mount in the background after start, so this runs again when one arrives.
        _bodyMountVersion = Mounting.MountDirectory.Version;
        Mounting.MountedModel citizen = Mounting.MountDirectory.FindCharacter(CitizenModel, CitizenGames);
        if (_body.IsValid() && (citizen != null) == _bodyIsCitizen) return;

        // Set on the GameManager, which keeps them loaded with the scene.
        PrefabAsset prefab = GameManager.Instance.PlayerModel;
        AnimationGraph graph = GameManager.Instance.PlayerAnimation;
        if (citizen == null && prefab.IsNotValid()) return;

        if (_body.IsValid()) _body.Destroy();
        _haveShoulder = false;
        _bodyIsCitizen = citizen != null;

        // The model keeps its own root name, which the rig binds by. The holder is what turns and moves.
        _body = new GameObject("Body");
        _body.SetParent(GameObject, false);
        GameObject model = citizen != null ? citizen.Instantiate() : GameObject.Instantiate(prefab);
        model.SetParent(_body, false);
        Quaternion rightHandRest = Quaternion.Identity, leftHandRest = Quaternion.Identity;
        _animator = citizen != null
            ? AddRig(model, citizen, out rightHandRest, out leftHandRest)
            : model.GetComponentInChildren<Animator>();
        if (_animator.IsValid() && graph.IsValid()) _animator.Graph = graph;
        _bodyRenderers = _body.GetComponentsInChildren<SkinnedMeshRenderer>().ToArray();
        _body.AddComponent<MapLit>();
        _shoulder = FindHumanBone(model.Transform, Prowl.Motion.HumanBodyBone.RightUpperArm);
        _rightHandRest = citizen != null ? rightHandRest : RestRotation(model.Transform, Prowl.Motion.HumanBodyBone.RightHand);
        _leftHandRest = citizen != null ? leftHandRest : RestRotation(model.Transform, Prowl.Motion.HumanBodyBone.LeftHand);

        _bodyYaw = Yaw;
        PlaceBody(0f);
        UpdateBodyVisibility();
    }

    /// <summary>
    /// An animator for a mounted character, with an avatar made from its skeleton, which maps a humanoid by its
    /// bones' names and shape. The graph then drives it like any imported humanoid. Also gives each hand's rotation
    /// in the avatar's T-pose, relative to the model, which the grips turn from.
    /// </summary>
    private static Animator AddRig(GameObject model, Mounting.MountedModel character, out Quaternion rightHandRest, out Quaternion leftHandRest)
    {
        // Every bone relative to the model. Source characters face along X; turning the root bones turns the
        // skinned meshes with them.
        Quaternion facing = Quaternion.FromEuler(0f, -90f, 0f);
        int count = character.Bones.Count;
        var ids = new Prowl.Motion.StringID[count];
        var parents = new int[count];
        var positions = new Float3[count];
        var rotations = new Quaternion[count];
        for (int i = 0; i < count; i++)
        {
            Mounting.MountedModel.Bone bone = character.Bones[i];
            ids[i] = new Prowl.Motion.StringID(bone.Name);
            parents[i] = bone.Parent;
            positions[i] = facing * bone.Position;
            rotations[i] = facing * bone.Rotation;
            if (bone.Parent < 0 && FindBone(model.Transform, bone.Name) is { } root)
            {
                root.LocalPosition = positions[i];
                root.LocalRotation = rotations[i];
            }
        }

        var skeleton = new Prowl.Motion.Skeleton(ids, parents, ParentSpacePose(parents, positions, rotations));
        Avatar.CreateAutomatic(skeleton, out Prowl.Motion.HumanoidMapResult report, character.Name);

        // Valve's bipeds number their neck and head, which matching by name misses, and carry the shoulders on a
        // fourth spine bone, the upper chest.
        Prowl.Motion.HumanDescription human = report.Description;
        MapBone(human, skeleton, Prowl.Motion.HumanBodyBone.Neck, "ValveBiped.Bip01_Neck1");
        MapBone(human, skeleton, Prowl.Motion.HumanBodyBone.Head, "ValveBiped.Bip01_Head1");
        MapBone(human, skeleton, Prowl.Motion.HumanBodyBone.Chest, "ValveBiped.Bip01_Spine2", replace: true);
        MapBone(human, skeleton, Prowl.Motion.HumanBodyBone.UpperChest, "ValveBiped.Bip01_Spine4", replace: true);

        rightHandRest = leftHandRest = Quaternion.Identity;
        Avatar avatar;
        if (human is { HasAllRequiredBones: true })
        {
            avatar = Avatar.CreateHumanoid(skeleton, human, character.Name);

            // Source characters rest with their arms down, and the grips turn the hands from a T-pose, so take the
            // hands' rest from the arms straightened out sideways.
            StraightenArm(human, parents, positions, rotations, Float3.UnitX,
                Prowl.Motion.HumanBodyBone.RightUpperArm, Prowl.Motion.HumanBodyBone.RightLowerArm,
                Prowl.Motion.HumanBodyBone.RightHand, Prowl.Motion.HumanBodyBone.RightMiddleProximal);
            StraightenArm(human, parents, positions, rotations, -Float3.UnitX,
                Prowl.Motion.HumanBodyBone.LeftUpperArm, Prowl.Motion.HumanBodyBone.LeftLowerArm,
                Prowl.Motion.HumanBodyBone.LeftHand, Prowl.Motion.HumanBodyBone.LeftMiddleProximal);
            rightHandRest = rotations[human.GetSkeletonBoneIndex(Prowl.Motion.HumanBodyBone.RightHand)];
            leftHandRest = rotations[human.GetSkeletonBoneIndex(Prowl.Motion.HumanBodyBone.LeftHand)];
        }
        else
        {
            Debug.LogWarning($"{character.Name} did not map as a humanoid, missing {string.Join(", ", report.UnmappedBones)}");
            avatar = Avatar.CreateGeneric(skeleton, 0, character.Name);
        }

        Animator animator = model.AddComponent<Animator>();
        animator.Avatar = avatar;
        return animator;
    }

    /// <summary>Each bone relative to its parent, from bones relative to the model.</summary>
    private static Prowl.Vector.Spatial.Transform3D[] ParentSpacePose(int[] parents, Float3[] positions, Quaternion[] rotations)
    {
        var pose = new Prowl.Vector.Spatial.Transform3D[parents.Length];
        for (int i = 0; i < pose.Length; i++)
        {
            Float3 position = positions[i];
            Quaternion rotation = rotations[i];
            if (parents[i] >= 0)
            {
                Quaternion inverse = Quaternion.Inverse(rotations[parents[i]]);
                position = inverse * (position - positions[parents[i]]);
                rotation = inverse * rotation;
            }
            pose[i] = new Prowl.Vector.Spatial.Transform3D(position, rotation, Float3.One);
        }
        return pose;
    }

    /// <summary>Turns each joint of an arm, with everything below it, so the arm points along <paramref name="outward"/>.</summary>
    private static void StraightenArm(Prowl.Motion.HumanDescription human, int[] parents, Float3[] positions, Quaternion[] rotations,
        Float3 outward, params Prowl.Motion.HumanBodyBone[] chain)
    {
        for (int j = 0; j + 1 < chain.Length; j++)
        {
            if (!human.HasBone(chain[j]) || !human.HasBone(chain[j + 1])) return;
            int joint = human.GetSkeletonBoneIndex(chain[j]), next = human.GetSkeletonBoneIndex(chain[j + 1]);
            Float3 along = positions[next] - positions[joint];
            if (Float3.Length(along) < 1e-5f) continue;
            Quaternion turn = Quaternion.FromToRotation(Float3.Normalize(along), outward);
            Float3 pivot = positions[joint];
            // Parents come first, so a bone below the joint always follows one already turned.
            for (int i = joint; i < parents.Length; i++)
            {
                if (i != joint && !IsBelow(parents, i, joint)) continue;
                positions[i] = pivot + turn * (positions[i] - pivot);
                rotations[i] = turn * rotations[i];
            }
        }
    }

    private static bool IsBelow(int[] parents, int bone, int ancestor)
    {
        for (int p = parents[bone]; p >= 0; p = parents[p])
            if (p == ancestor) return true;
        return false;
    }

    private static void MapBone(Prowl.Motion.HumanDescription human, Prowl.Motion.Skeleton skeleton, Prowl.Motion.HumanBodyBone bone,
        string name, bool replace = false)
    {
        if (human == null || (human.HasBone(bone) && !replace)) return;
        int index = skeleton.GetBoneIndex(new Prowl.Motion.StringID(name));
        if (index >= 0) human.SetSkeletonBoneIndex(bone, index);
    }

    private void OnJumpedBody() => _jumpPulse = 0.15f;

    private void OnLandedBody(float fallSpeed)
    {
        if (fallSpeed > 9f) _hardLandPulse = 0.15f;
    }

    /// <summary>Runs after movement each frame, so the animator (which runs after every Update) sees this frame's state.</summary>
    private void UpdateBody(float dt)
    {
        if (Mounting.MountDirectory.Version != _bodyMountVersion) SetupBody();
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
            float scale = held.ModelScale;
            _animator.SetTarget("RightHandTarget", Prowl.Motion.Target.FromWorld(new Prowl.Vector.Spatial.Transform3D(_holdPosition + _holdRotation * (held.Grip * scale), _holdRotation * RightGrip * _rightHandRest, Float3.One)));
            if (held.Support is Float3 support)
                _animator.SetTarget("LeftHandTarget", Prowl.Motion.Target.FromWorld(new Prowl.Vector.Spatial.Transform3D(_holdPosition + _holdRotation * (support * scale), _holdRotation * LeftGrip * _leftHandRest, Float3.One)));
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

    /// <summary>The bone the avatar maps to a body part, whatever the rig calls it.</summary>
    private Transform FindHumanBone(Transform root, Prowl.Motion.HumanBodyBone bone)
    {
        Prowl.Motion.HumanDescription human = _animator.IsValid() && _animator.Avatar.IsValid() ? _animator.Avatar.Description : null;
        if (human == null || !human.HasBone(bone)) return null;
        return FindBone(root, _animator.Avatar.Skeleton.GetBoneID(human.GetSkeletonBoneIndex(bone)).ToString());
    }

    /// <summary>A bone's rotation in the model's space while it is still in its rest pose.</summary>
    private Quaternion RestRotation(Transform model, Prowl.Motion.HumanBodyBone bone)
    {
        Transform found = FindHumanBone(model, bone);
        return found != null ? Quaternion.Inverse(model.Rotation) * found.Rotation : Quaternion.Identity;
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
