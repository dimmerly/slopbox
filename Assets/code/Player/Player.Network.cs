using System;
using System.Linq;
using Prowl.Runtime;
using Prowl.Runtime.Networking;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox;

public sealed partial class Player
{
    [Sync] public float AimYaw { get; set; }
    [Sync] public float AimPitch { get; set; }
    [Sync] public Float3 MoveVelocity { get; set; }
    [Sync] public float EyeLevel { get; set; } = 1.65f;
    [Sync] public int EquipmentSlot { get; set; }
    [Sync] public bool LightOn { get; set; }
    [Sync(SyncFlags.FromHost)] public bool NetworkHolding { get; set; }
    [Sync(SyncFlags.FromHost)] public Float3 HeldPoint { get; set; }
    [Sync(SyncFlags.FromHost)] public string ToolStatus { get; set; } = "";
    private Beam _networkBeam;
    public ToolRegistry Tools { get; } = new();
    public UndoHistory Undo { get; } = new();
    [Prowl.Echo.SerializeIgnore] public EquipmentInput EquipmentInput;
    private double _nextInputSend;
    private int _inputRevision;
    private EquipmentInput _pendingInput;
    private EquipmentInput _sentInput;
    private bool _hasSentInput;

    public bool IsLocalPlayer => !Network.Active || !IsProxy;
    public bool CanUseEquipment => IsLocalPlayer || (Networking.IsHost && Network.Active);

    internal void CaptureEquipmentInput()
    {
        var keys = Scene.Current.FindObjectsOfType<ThrusterProp>().SelectMany(t => new[] { t.Forward, t.Back }).Distinct().OrderBy(k => k);
        string down = "," + string.Join(",,", keys.Where(Controls.Down).Select(k => (int)k)) + ",";
        string pressed = string.Join(",", keys.Where(Controls.Pressed).Select(k => $",{(int)k},"));
        EquipmentInput = new EquipmentInput
        {
            Enabled = InputEnabled,
            Primary = Input.GetMouseButton(Controls.Attack1), PrimaryPressed = Input.GetMouseButtonDown(Controls.Attack1),
            Secondary = Input.GetMouseButton(Controls.Attack2), SecondaryPressed = Input.GetMouseButtonDown(Controls.Attack2),
            Reload = Controls.Pressed(Controls.Reload), Use = Controls.Down(Controls.Use), Shift = Input.IsShiftPressed,
            Mouse = Input.MouseDelta, Wheel = Input.MouseWheelDelta, Slot = ActiveIndex,
            KeysDown = down, KeysPressed = pressed
        };
        if (Networking.IsClient && ActiveEquipment is Physgun && NetworkHolding && EquipmentInput.Use) LookLocked = true;
        if (!Networking.IsClient || !Networking.IsReady || !Network.Active) return;
        EquipmentInput input = EquipmentInput;
        // Aim already replicates separately. Only physgun rotation and distance need raw mouse input.
        if (ActiveEquipment is not Physgun || !NetworkHolding)
        {
            input.Mouse = default;
            input.Wheel = 0;
        }
        else if (!input.Use) input.Mouse = default;
        input.PrimaryPressed |= _pendingInput.PrimaryPressed;
        input.SecondaryPressed |= _pendingInput.SecondaryPressed;
        input.Reload |= _pendingInput.Reload;
        input.Mouse += _pendingInput.Mouse;
        input.Wheel += _pendingInput.Wheel;
        input.KeysPressed += _pendingInput.KeysPressed;
        _pendingInput = input;
        if (Time.TimeSinceStartup < _nextInputSend) return;
        if (_hasSentInput && input.SameState(_sentInput) && !input.HasEvents) return;
        _nextInputSend = Time.TimeSinceStartup + 1.0 / 60;
        input.Revision = ++_inputRevision;
        SubmitEquipment(input);
        _sentInput = input;
        _hasSentInput = true;
        _pendingInput = default;
    }

    [Rpc.Host(RpcFlags.OwnerOnly)]
    private void SubmitEquipment(EquipmentInput input)
    {
        if (!Networking.IsHost || !_ready) return;
        input.PrimaryPressed |= EquipmentInput.PrimaryPressed;
        input.SecondaryPressed |= EquipmentInput.SecondaryPressed;
        input.Reload |= EquipmentInput.Reload;
        input.Mouse += EquipmentInput.Mouse;
        input.Wheel += EquipmentInput.Wheel;
        if (!string.IsNullOrEmpty(input.KeysPressed)) input.KeysPressed += EquipmentInput.KeysPressed;
        EquipmentInput = input;
        Switch(input.Slot);
        // Use the replicated aim, not the host's mouse or camera.
        Yaw = AimYaw; Pitch = AimPitch;
    }

    [Rpc.Host(RpcFlags.OwnerOnly)]
    public void RequestSpawn(string id)
    {
        if (!Networking.IsHost) return;
        Spawnable source = GameManager.Instance.Catalog.Find(id);
        if (source == null) { ReceiveNotice("The host does not have that content", NotifyKind.Error); return; }
        RunAuthoritative(() => Spawner.SpawnAtCrosshair(this, source));
    }

    [Rpc.Host(RpcFlags.OwnerOnly)]
    public void RequestUndo()
    {
        if (!Networking.IsHost) return;
        RunAuthoritative(() => Undo.UndoLast());
    }

    [Rpc.Host(RpcFlags.OwnerOnly)]
    public void RequestTool(string name, string settings, int action, string payload, string listed)
    {
        if (!Networking.IsHost || !_ready) return;
        BaseTool tool = Tools.All.FirstOrDefault(t => t.GetType().Name == name);
        if (tool == null) return;
        ToolSettings.Read(tool, settings);
        if (tool is DuplicatorTool duplicator && payload.Length > 0) duplicator.LoadNetworkCopy(payload);
        if (tool is PlacerTool placer) placer.NetworkList = System.Text.Json.JsonSerializer.Deserialize<string[]>(listed, GameJson.Options);
        ToolGun gun = Get<ToolGun>();
        gun.Select(tool);
        RunAuthoritative(() => gun.UseAction(action));
        ToolStatus = tool.Status;
        ReceiveTool(name, ToolSettings.Write(tool), tool is DuplicatorTool dupe ? dupe.NetworkCopy : "", tool is AnimationTool animation ? animation.SelectedObject : null);
    }

    [Rpc.Owner(RpcFlags.HostOnly)]
    private void ReceiveTool(string name, string settings, string copied, GameObject selected)
    {
        if (!IsLocalPlayer) return;
        BaseTool tool = Tools.All.FirstOrDefault(t => t.GetType().Name == name);
        if (tool == null) return;
        ToolSettings.Read(tool, settings);
        if (tool is AnimationTool animation) animation.SelectObject(selected);
        if (tool is DuplicatorTool dupe && copied.Length > 0) dupe.LoadNetworkCopy(copied);
    }

    [Rpc.Owner(RpcFlags.HostOnly)]
    public void ReceiveNotice(string message, NotifyKind kind) => Notify.Post(message, kind);

    [Rpc.Host(RpcFlags.OwnerOnly)]
    public void RequestAnimation(GameObject target, int sequence, float speed)
    {
        if (!Networking.IsHost || target.IsNotValid()) return;
        var animator = target.GetComponentInChildren<Mounting.ModelAnimator>();
        if (animator.IsNotValid()) return;
        animator.Speed = Maths.Clamp(speed, 0, 3);
        animator.Play(sequence);
    }

    private void RunAuthoritative(Action action)
    {
        GameManager game = GameManager.Instance;
        Player previous = game.ActingPlayer;
        game.ActingPlayer = this;
        try { action(); }
        finally { game.ActingPlayer = previous; }
    }

    [Rpc.Broadcast(RpcFlags.HostOnly)]
    public void ShowToolShot(Float3 point) => Get<ToolGun>().ShowShot(point);

    internal void UpdateNetworkEffects()
    {
        if (Networking.IsHost)
        {
            NetworkHolding = ActiveEquipment is Physgun phys && phys.IsHolding || ActiveEquipment is GravityGun grav && grav.IsHolding;
            HeldPoint = ActiveEquipment is Physgun p ? p.HoldPoint : ActiveEquipment is GravityGun g ? g.HoldPoint : Float3.Zero;
            if (!IsLocalPlayer) EquipmentInput.KeysPressed = "";
        }
        else if (Networking.IsClient)
        {
            if (NetworkHolding && ActiveEquipment is Physgun)
            {
                if (_networkBeam == null || !_networkBeam.IsValid) _networkBeam = new Beam("Physgun Beam", new Color(0.3f, 1.1f, 3f, 1f), 0.015f);
                _networkBeam.Curve(MuzzlePosition, Maths.Lerp(MuzzlePosition, HeldPoint, 0.5f) - Float3.UnitY * 0.2f, HeldPoint);
            }
            else _networkBeam?.Hide();
            GameAudio.SetLoop(this, "hum", NetworkHolding, EyePosition, true, 0.1f);
        }
    }

    internal void UpdateNetworkPlayer()
    {
        if (IsLocalPlayer)
        {
            AimYaw = Yaw; AimPitch = Pitch; MoveVelocity = Controller.Velocity; EyeLevel = Controller.EyeHeight;
            EquipmentSlot = ActiveIndex; LightOn = FlashlightOn;
        }
        else
        {
            Yaw = AimYaw; Pitch = AimPitch; Controller.Velocity = MoveVelocity; Controller.EyeHeight = EyeLevel;
            Switch(EquipmentSlot);
            if (_flashlight.IsValid())
            {
                _flashlight.Enabled = LightOn;
                _flashlight.Transform.Position = EyePosition + Forward * 0.75f;
                _flashlight.Transform.Rotation = EyeRotation;
            }
            if (Networking.IsHost)
            {
                if (ActiveEquipment is not ToolGun) RunAuthoritative(() => ActiveEquipment?.Update());
                EquipmentInput.PrimaryPressed = EquipmentInput.SecondaryPressed = EquipmentInput.Reload = false;
                EquipmentInput.Mouse = default;
                EquipmentInput.Wheel = 0;
            }
            UpdateBody(Time.DeltaTime);
        }
    }
}
