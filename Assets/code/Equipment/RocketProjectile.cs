using System;
using System.Collections.Generic;
using Jitter2.Dynamics;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox;

/// <summary>Scene-owned projectile, swept each physics step so fast rockets cannot tunnel.</summary>
public sealed class RocketProjectile : Component
{
    private const float CollisionRadius = 0.1f;
    private Float3 _direction;
    private float _speed, _radius, _blastSpeed, _age;
    private bool _exploded;
    private Material _metal, _glow, _trailMaterial;
    private LineRenderer _trail;
    private Float3 _launchPosition;

    public static void Launch(Float3 eye, Float3 muzzle, Float3 direction, float speed, float radius, float blastSpeed)
    {
        var go = new GameObject("Rocket");
        go.Transform.Position = muzzle;
        go.Transform.LookAt(muzzle + direction);
        var rocket = go.AddComponent<RocketProjectile>();
        rocket._direction = direction;
        rocket._speed = speed;
        rocket._radius = radius;
        rocket._blastSpeed = blastSpeed;
        rocket._launchPosition = muzzle;
        Scene.Current.Add(go);

        // The muzzle can be beyond a nearby wall: sweep from the eye before allowing flight.
        Float3 offset = muzzle - eye;
        float distance = Float3.Length(offset);
        if (distance > 0f && Scene.Current.Physics.SphereCast(eye, CollisionRadius, offset / distance, distance, out ShapeCastHit hit))
            rocket.Explode(eye + offset / distance * hit.Distance - hit.Normal * CollisionRadius, hit.Normal);
    }

    public override void Start()
    {
        if (_exploded) return;
        _metal = Materials.Lit(new Color(0.2f, 0.23f, 0.16f, 1f), 0.7f, 0.3f);
        _glow = Materials.Lit(Color.Black).Emissive(new Color(1f, 0.35f, 0.04f, 1f), 6f);
        AddPart(Shapes.Cylinder, _metal, Float3.Zero, new Float3(0.13f, 0.45f, 0.13f), new Float3(90f, 0f, 0f));
        AddPart(Shapes.Sphere, _metal, new Float3(0f, 0f, 0.23f), new Float3(0.13f, 0.13f, 0.2f));
        AddPart(Shapes.Sphere, _glow, new Float3(0f, 0f, -0.3f), new Float3(0.1f, 0.1f, 0.3f));
        for (int i = 0; i < 4; i++)
        {
            float a = i * MathF.PI * 0.5f;
            AddPart(Shapes.Cube, _metal, new Float3(MathF.Cos(a) * 0.08f, MathF.Sin(a) * 0.08f, -0.15f),
                new Float3(0.12f, 0.02f, 0.14f), new Float3(0f, 0f, i * 90f));
        }
        var trail = new GameObject("Rocket Exhaust");
        trail.SetParent(GameObject, false);
        _trail = trail.AddComponent<LineRenderer>();
        _trailMaterial = Materials.Line();
        _trail.Material = _trailMaterial;
        _trail.StartWidth = 0.07f;
        _trail.EndWidth = 0.015f;
        _trail.StartColor = new Color(3f, 1f, 0.1f, 1f);
        _trail.EndColor = new Color(0.3f, 0.3f, 0.3f, 0f);
        GameAudio.SetLoop(this, "rocket_thruster", true, Transform.Position, true, 0.35f, maxDistance: 80f);
    }

    private void AddPart(Mesh mesh, Material material, Float3 position, Float3 scale, Float3 rotation = default)
    {
        var go = new GameObject("Rocket Part");
        go.SetParent(GameObject, false);
        go.Transform.LocalPosition = position;
        go.Transform.LocalScale = scale;
        go.Transform.LocalEulerAngles = rotation;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.Mesh = mesh;
        renderer.Material = material;
    }

    public override void FixedUpdate()
    {
        if (_exploded) return;
        float distance = _speed * Time.FixedDeltaTime;
        Float3 start = Transform.Position;
        if (Scene.Current.Physics.SphereCast(start, CollisionRadius, _direction, distance, out ShapeCastHit hit))
        {
            Explode(start + _direction * hit.Distance - hit.Normal * CollisionRadius, hit.Normal);
            return;
        }
        Transform.Position = start + _direction * distance;
        _age += Time.FixedDeltaTime;
        if (_age >= 6f) Explode(Transform.Position);
    }

    public override void LateUpdate()
    {
        if (_exploded) return;
        GameAudio.SetLoop(this, "rocket_thruster", true, Transform.Position, true, 0.35f, maxDistance: 80f);
        if (_trail.IsNotValid()) return;
        Float3 tail = Transform.Position - _direction * 0.3f;
        _trail.Points.Clear();
        _trail.Points.Add(_trail.Transform.InverseTransformPoint(tail));
        _trail.Points.Add(_trail.Transform.InverseTransformPoint(tail - _direction * MathF.Min(3f, Float3.Distance(Transform.Position, _launchPosition))));
        _trail.MarkDirty();
    }

    private void Explode(Float3 position, Float3 normal = default)
    {
        if (_exploded) return;
        _exploded = true;
        GameAudio.StopLoop(this);
        Blast(position, _radius, _blastSpeed);
        RocketBlast.Create(position, _radius, normal);
        GameAudio.Play("rocket_explosion_" + Random.Shared.Next(1, 4), 0.9f, position: position, maxDistance: 120f, minDistance: 6f);
        GameObject.Destroy();
    }

    /// <summary>Overlap colliders, then apply a single mass-scaled impulse to each body.</summary>
    public static void Blast(Float3 position, float radius, float speed)
    {
        var hits = new List<ShapeCastHit>();
        Scene.Current.Physics.OverlapSphere(position, radius, hits);
        var affected = new HashSet<Rigidbody3D>();
        foreach (ShapeCastHit hit in hits)
        {
            Rigidbody3D body = hit.Rigidbody;
            if (body.IsNotValid() || !affected.Add(body)) continue;
            Prop prop = body.GetComponent<Prop>();
            // Leave structural static geometry alone, while still releasing frozen physics props.
            if (body.MotionType == MotionType.Static && prop.IsNotValid()) continue;
            if (prop.IsNotValid()) prop = Prop.Of(body);
            prop.SetFrozen(false);
            Float3 offset = body.CenterOfMass - position;
            float distance = Float3.Length(offset);
            Float3 outward = distance > 0.001f ? offset / distance : Float3.UnitY;
            Float3 launch = Float3.Normalize(outward + Float3.UnitY * 0.65f);
            float falloff = 1f - Math.Clamp(distance / radius, 0f, 1f);
            body.SetActive(true);
            body.ApplyImpulse(launch * (body.Mass * speed * (0.2f + 0.8f * falloff)));
            body.AngularVelocity += new Float3(outward.Z, 0.5f, -outward.X) * (3f + 5f * falloff);
        }
    }

    protected override void OnDispose()
    {
        GameAudio.StopLoop(this);
        if (_metal.IsValid()) _metal.Dispose();
        if (_glow.IsValid()) _glow.Dispose();
        if (_trailMaterial.IsValid()) _trailMaterial.Dispose();
        base.OnDispose();
    }

    public override void OnDisable() => GameAudio.StopLoop(this);
}
