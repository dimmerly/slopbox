// Copied temporarily into Assets by smoke.ps1, then removed after the test.
using System;
using System.Linq;
using System.Threading.Tasks;
using Jitter2.Dynamics;
using Prowl.Runtime;
using Prowl.Runtime.Networking;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox;

public sealed class MultiplayerProbe : Component
{
    public static string Result = "waiting";
    public static int Checkpoints;
    private Player _inputPlayer;
    private EquipmentInput _input;
    private bool _overrideInput;
    private static readonly System.Reflection.MethodInfo s_submit = typeof(Player).GetMethod("SubmitEquipment", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    private static readonly System.Reflection.FieldInfo s_nextSend = typeof(Player).GetField("_nextInputSend", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    public override void LateUpdate()
    {
        if (!_overrideInput) return;
        _input.Revision++;
        s_submit.Invoke(_inputPlayer, new object[] { _input });
        _input.PrimaryPressed = _input.SecondaryPressed = _input.Reload = false;
    }

    private void OverrideInput(Player player, EquipmentInput input)
    {
        _inputPlayer = player;
        _input = input;
        _overrideInput = true;
        s_nextSend.SetValue(player, double.MaxValue);
    }

    private void ReleaseInput()
    {
        _overrideInput = false;
        s_nextSend.SetValue(_inputPlayer, 0d);
        typeof(Player).GetField("_hasSentInput", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(_inputPlayer, false);
    }

    public override async void Start()
    {
        if (!Networking.IsClient) return;
        try
        {
            await Until(() => Networking.IsReady && Player.Local.IsValid() && Player.Local.Network.Active, "owned player");
            Player player = Player.Local;
            Check(!player.IsProxy && player.Network.Owner.IsLocal, "local ownership");
            Check(Scene.Current.FindObjectsOfType<Player>().Count(p => p.IsLocalPlayer) == 1, "one local player");
            Check(Scene.Current.FindObjectsOfType<Camera>().Count(c => c.Enabled && c.Target.IsNotValid()) == 1, "one game camera");
            Checkpoint("player and camera");
            if (Networking.Session.LocalPeer.Name == "LateClient")
            {
                await Until(() => Prop.All.Any(p => p.SpawnId == "shape:cube_1" && p.GetComponentInChildren<MeshRenderer>().IsValid()), "late join prop");
                Checkpoint("late join geometry");
                Report("late-pass");
                return;
            }

            player.Controller.Teleport(new Float3(0, 1, 0));
            player.Pitch = 35; player.Yaw = 0;
            await GameTask.DelayRealtime(0.4f);
            int before = Prop.All.Length;
            player.RequestSpawn("shape:cube_1");
            await Until(() => Prop.All.Length > before, "client spawn");
            Prop prop = Prop.All.Last(p => p.SpawnId == "shape:cube_1");
            await Until(() => prop.GetComponentInChildren<MeshRenderer>().IsValid(), "runtime geometry");
            Check(prop.Network.Active && prop.IsProxy && prop.Body.MotionType == MotionType.Kinematic, "native prop proxy");
            Checkpoint("spawn and physics proxy");
            await GameTask.DelayRealtime(0.5f);
            Aim(player, prop.Transform.Position);
            player.Switch(2);
            await GameTask.DelayRealtime(0.4f);
            var colour = player.Tools.Get<ColourTool>();
            colour.Color = new Color(1f, 0f, 0f, 1f);
            player.RequestTool(nameof(ColourTool), ToolSettings.Write(colour), 0, "", "[]");
            await Until(() => prop.Tint == new Color(1f, 0f, 0f, 1f), "colour replication");
            Checkpoint("tool settings and colour");
            player.RequestUndo();
            await Until(() => prop.Tint == Color.White, "undo colour");
            Checkpoint("per-player undo");

            var dupe = player.Tools.Get<DuplicatorTool>();
            Aim(player, prop.Transform.Position);
            await GameTask.DelayRealtime(0.4f);
            player.RequestTool(nameof(DuplicatorTool), ToolSettings.Write(dupe), 1, "", "[]");
            await Until(() => dupe.NetworkCopy.Length > 0, "copy response");
            player.Yaw = 90; player.Pitch = 35;
            await GameTask.DelayRealtime(0.4f);
            before = Prop.All.Length;
            player.RequestTool(nameof(DuplicatorTool), ToolSettings.Write(dupe), 0, dupe.NetworkCopy, "[]");
            await Until(() => Prop.All.Length > before, "paste");
            Checkpoint("duplicator copy and paste");
            player.RequestUndo();
            await Until(() => Prop.All.Length == before, "undo paste");

            Aim(player, prop.Transform.Position);
            await GameTask.DelayRealtime(0.4f);
            var rope = player.Tools.Get<RopeTool>();
            player.RequestTool(nameof(RopeTool), ToolSettings.Write(rope), 0, "", "[]");
            await GameTask.DelayRealtime(0.3f);
            player.Yaw = -90; player.Pitch = 60;
            await GameTask.DelayRealtime(0.4f);
            player.RequestTool(nameof(RopeTool), ToolSettings.Write(rope), 0, "", "[]");
            await Until(() => Scene.Current.FindObjectsOfType<RopeVisual>().Any(r => r.Network.Active && r.GetComponentInChildren<LineRenderer>().IsValid()), "rope visual");
            Checkpoint("multi-click rope and visual");
            player.RequestUndo();
            await Until(() => Scene.Current.FindObjectsOfType<RopeVisual>().Length == 0, "undo rope");
            Aim(player, prop.Transform.Position);
            player.Switch(0);
            await GameTask.DelayRealtime(0.4f);
            OverrideInput(player, new EquipmentInput { Enabled = true, Slot = 0, Primary = true, PrimaryPressed = true });
            await Until(() => player.NetworkHolding, "remote physgun grab");
            _overrideInput = false;
            await GameTask.DelayRealtime(0.75f);
            Check(player.NetworkHolding, "held input must persist without repeated RPCs");
            Checkpoint("remote physgun hold without repeated RPCs");
            _overrideInput = true;
            _input.SecondaryPressed = true;
            await Until(() => !player.NetworkHolding && prop.IsFrozen, "remote physgun freeze");
            ReleaseInput();
            Checkpoint("remote physgun freeze");

            player.Switch(3); player.Yaw = 180; player.Pitch = -15;
            await GameTask.DelayRealtime(0.4f);
            OverrideInput(player, new EquipmentInput { Enabled = true, Slot = 3, Primary = true, PrimaryPressed = true });
            await Until(() => Scene.Current.FindObjectsOfType<RocketProjectile>().Any(r => r.Network.Active && r.IsProxy), "remote rocket");
            ReleaseInput();
            Checkpoint("remote rocket launch");
            await Until(() => Scene.Current.FindObjectsOfType<RocketProjectile>().Length == 0, "rocket destruction");
            Checkpoint("rocket destruction");
            await GameTask.DelayRealtime(0.3f);
            var revisionField = typeof(Player).GetField("_inputRevision", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            int idleRevision = (int)revisionField.GetValue(player);
            await GameTask.DelayRealtime(0.75f);
            Check((int)revisionField.GetValue(player) == idleRevision, "idle equipment must not send RPCs");
            Checkpoint("idle equipment sends no RPCs");
            Report("pass");
            Player host = Scene.Current.FindObjectsOfType<Player>().First(p => p.Network.Owner == null);
            NetworkAnimator hostAnimation = host.GetComponent<NetworkAnimator>();
            await Until(() => hostAnimation.Animator.IsValid() && hostAnimation.Animator.IsGraph, "remote animation graph");
            await Until(() => hostAnimation.Animator.GetBool("Crouch") && hostAnimation.Animator.GetFloat("MoveWeight") > 0.1f, "remote crouch and movement animation");
            Checkpoint("remote locomotion and crouch graph parameters");
            Report("animation-pass");
            await Until(() => hostAnimation.Animator.GetBool("Jump"), "remote jump animation");
            Checkpoint("remote jump animation");
            Report("jump-pass");
        }
        catch (Exception e) { if (_overrideInput) ReleaseInput(); Report("FAIL: " + e); }
    }

    private static void Aim(Player player, Float3 point)
    {
        Float3 direction = point - player.EyePosition;
        player.Yaw = MathF.Atan2(direction.X, direction.Z) * 180 / MathF.PI;
        player.Pitch = -MathF.Atan2(direction.Y, MathF.Sqrt(direction.X * direction.X + direction.Z * direction.Z)) * 180 / MathF.PI;
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
    }

    private static async Task Until(Func<bool> predicate, string description)
    {
        double deadline = Time.TimeSinceStartup + 15;
        while (!predicate())
        {
            if (Time.TimeSinceStartup > deadline) throw new Exception("Timed out: " + description);
            await GameTask.NextFrame();
        }
    }

    [Rpc.Host]
    private static void Checkpoint(string name)
    {
        Checkpoints++;
        Debug.Log("SLOPBOX E2E: " + name);
    }

    [Rpc.Host]
    private static void Report(string result)
    {
        Result = result;
        Debug.Log("SLOPBOX E2E RESULT: " + result);
    }
}
