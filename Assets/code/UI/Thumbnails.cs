using System;
using System.Collections.Generic;
using System.Linq;
using Prowl.Runtime;
using Prowl.Runtime.Networking;
using Prowl.Runtime.Rendering;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox;

/// <summary>
/// Renders UI icons from the real objects: spawn menu thumbnails and equipment icons. Each one is built
/// far out of sight, shot by a camera into a transparent render texture, and torn down a couple of frames
/// later. Requests are queued and handled one at a time, so opening a menu never hitches. A request that is
/// not repeated within a few frames is dropped before it renders, so callers ask every frame for what they
/// are showing and scrolling past an icon never costs a render.
/// </summary>
public sealed class Thumbnails : Component
{
    /// <summary>One icon to render.</summary>
    /// <param name="Id">Cache key. The same id always returns the same texture.</param>
    /// <param name="Build">Makes the subject, detached from the scene. Physics is stripped afterwards.</param>
    /// <param name="ViewFrom">Direction from the subject to the camera.</param>
    /// <param name="Rotation">How the subject is turned before it is framed.</param>
    /// <param name="FitWidth">Frame the subject to the image's width, for long things such as weapons.</param>
    /// <param name="Lighting">Multiplies the studio lights.</param>
    public readonly record struct Shot(string Id, Func<GameObject> Build, Float3 ViewFrom, Quaternion Rotation,
        int Width, int Height, bool FitWidth = false, float Lighting = 1f);

    public const int SpawnIconSize = 192;

    private static readonly Float3 Stage = new(0f, 5000f, 0f);
    private const float Fov = 28f;
    private const int FramesToRender = 2;
    // A layer nothing else uses, so the studio lights reach only the subject. 31 is the gizmo layer.
    private const int StudioLayer = 30;
    // A queued shot nobody has asked for in this many frames is dropped.
    private const int StaleFrames = 3;

    private readonly Dictionary<string, RenderTexture> _done = new();
    private readonly Queue<Shot> _queue = new();
    private readonly HashSet<string> _queued = new();
    private readonly Dictionary<string, int> _wantedAt = new();

    private Shot? _current;
    private GameObject _subject, _rig;
    private RenderTexture _target;
    private int _frames, _shots, _frame;

    /// <summary>The icon for <paramref name="shot"/>, or null while it is still being rendered.</summary>
    public Texture2D Get(in Shot shot)
    {
        if (_done.TryGetValue(shot.Id, out RenderTexture rt)) return rt.MainTexture;
        _wantedAt[shot.Id] = _frame;
        if (_queued.Add(shot.Id)) _queue.Enqueue(shot);
        return null;
    }

    /// <summary>The icon for <paramref name="id"/> if it is already rendered. Never queues a render.</summary>
    public Texture2D Peek(string id) => _done.TryGetValue(id, out RenderTexture rt) ? rt.MainTexture : null;

    /// <summary>A spawn menu thumbnail: three quarter view from above, square and transparent.</summary>
    public Texture2D Get(Spawnable spawnable) => Get(new Shot(
        SpawnId(spawnable), spawnable.Create,
        new Float3(0f, 0.55f, -1f), Quaternion.FromEuler(0f, 210f, 0f),
        SpawnIconSize, SpawnIconSize, Lighting: spawnable.ThumbnailLighting));

    public Texture2D Peek(Spawnable spawnable) => Peek(SpawnId(spawnable));

    private static string SpawnId(Spawnable spawnable) => "spawn:" + spawnable.Id;

    /// <summary>An equipment icon: its view model from the side, muzzle pointing right, wide and transparent.</summary>
    public Texture2D Get(Equipment equipment) => Get(new Shot(
        // With the mount version, so the icon is drawn again once a game's model replaces the built one.
        $"equipment:{equipment.GetType().FullName}:{Mounting.MountDirectory.Version}",
        () => BuildViewModel(equipment),
        new Float3(1f, 0.3f, 0.35f), Quaternion.Identity,
        256, 128, FitWidth: true, Lighting: 0.5f));

    private static GameObject BuildViewModel(Equipment equipment)
    {
        var go = new GameObject("Equipment Icon");
        ViewModel vm = go.AddComponent<ViewModel>();
        vm.Scale = 1f;
        vm.Rebuild(equipment);
        return go;
    }

    public override void Update()
    {
        _frame++;
        if (_current != null)
        {
            if (++_frames < FramesToRender) return;
            Finish();
        }

        // Skip whatever went out of view while it waited. Dropped ids can be queued again.
        while (_queue.Count > 0)
        {
            Shot shot = _queue.Dequeue();
            if (_frame - _wantedAt.GetValueOrDefault(shot.Id) > StaleFrames)
            {
                _queued.Remove(shot.Id);
                continue;
            }
            _wantedAt.Remove(shot.Id);
            Begin(shot);
            break;
        }
    }

    protected override void OnDispose()
    {
        Cleanup();
        foreach (RenderTexture rt in _done.Values)
            if (rt.IsValid()) rt.Dispose();
        _done.Clear();
        base.OnDispose();
    }

    private void Begin(Shot shot)
    {
        GameObject subject;
        try
        {
            subject = shot.Build();
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            subject = null;
        }
        if (subject.IsNotValid()) return;

        // A picture only: no physics, so nothing falls or collides out there. Everything goes on the
        // studio layer, the only one the studio lights reach.
        foreach (Component c in subject.GetComponentsInChildren<Component>(true, true).ToList())
        {
            c.GameObject.LayerIndex = StudioLayer;
            if (c is Rigidbody3D or Collider or Prop) c.GameObject.RemoveComponent(c);
        }

        // Each shot gets its own spot, so nothing from a previous one can stray into frame.
        subject.Transform.Position = Stage + new Float3(_shots++ * 50f, 0f, 0f);
        subject.Transform.Rotation = shot.Rotation;
        Scene.Current.Add(subject);

        // Frame the bounding sphere, by height or, for long subjects, by width.
        AABB local = Spawner.LocalBounds(subject);
        Float3 center = subject.Transform.TransformPoint(local.Center);
        float radius = MathF.Max(Float3.Length(local.Extents), 0.02f);
        float aspect = shot.Width / (float)shot.Height;
        float halfFov = Fov * 0.5f * Maths.Deg2Rad;
        if (shot.FitWidth) halfFov = MathF.Atan(MathF.Tan(halfFov) * aspect);
        float distance = radius / MathF.Sin(halfFov) * 1.12f;
        Float3 direction = Float3.Normalize(shot.ViewFrom);

        _target = new RenderTexture(shot.Width, shot.Height, true, [TextureFormat.RGBA8]);

        _rig = new GameObject("Thumbnail Camera");
        _rig.Network.Mode = NetworkMode.Never;
        _rig.Transform.Position = center + direction * distance;
        _rig.Transform.LookAt(center);
        Camera camera = _rig.AddComponent<Camera>();
        camera.Target = _target;
        camera.Depth = -100;
        camera.FieldOfView = Fov;
        camera.NearClipPlane = MathF.Max(0.01f, distance - radius * 2f);
        camera.FarClipPlane = distance + radius * 2f;
        // A colour background is cleared opaque, so draw as an overlay: it clears to transparent black and
        // the icon keeps its alpha.
        camera.Background = CameraBackground.None;
        camera.PostProcessing = false;

        // Studio lighting: directional lights that reach only the studio layer, so the world is never lit by them
        // and every icon is exposed the same whatever its size. The front light is the key, so the face
        // toward the viewer is always lit; the top and rim lights give the form some shape.
        Float3 right = _rig.Transform.Right, up = _rig.Transform.Up;
        AddLight(-(direction + up * 0.35f), 1.4f * shot.Lighting);
        AddLight(-(up * 1.2f - right * 0.8f + direction * 0.5f), 0.6f * shot.Lighting);
        AddLight(-(right + direction * 0.2f - up * 0.2f), 0.3f * shot.Lighting);

        Scene.Current.Add(_rig);

        _current = shot;
        _subject = subject;
        _frames = 0;
    }

    // A directional light shines along its Forward, so it faces the way the light travels
    private void AddLight(Float3 travel, float intensity)
    {
        var go = new GameObject("Thumbnail Light");
        go.SetParent(_rig, true);
        go.Transform.Rotation = Quaternion.LookRotation(Float3.Normalize(travel), Float3.UnitY);
        var light = go.AddComponent<Light>();
        light.Type = LightType.Directional;
        light.LayerMask = LayerMask.FromMask(1u << StudioLayer);
        light.Intensity = intensity;
        light.CastShadows = false;
    }

    private void Finish()
    {
        _done[_current.Value.Id] = _target;
        _target = null;
        Cleanup();
    }

    private void Cleanup()
    {
        // Dispose rather than Destroy: Destroy waits for the end of the frame, after the next shot is set up.
        if (_subject.IsValid()) _subject.Dispose();
        if (_rig.IsValid()) _rig.Dispose();
        if (_target.IsValid()) _target.Dispose();
        _subject = _rig = null;
        _target = null;
        _current = null;
    }
}
