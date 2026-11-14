using System;
using Prowl.Echo;
using Prowl.Runtime;
using Prowl.Vector;

namespace Slopbox.Mounting;

/// <summary>
/// Plays a <see cref="MountedModel"/>'s sequences by moving its bones. Starts in the model's resting sequence.
/// Looping sequences repeat; the rest play once and hold their last frame. Bound without bones it sleeps, the
/// model standing as it was built, until the first <see cref="Play"/> gives the model its skeleton.
/// </summary>
[AddComponentMenu("Slopbox/Model Animator")]
public sealed class ModelAnimator : Component
{
    /// <summary>Playback rate, 1 for the sequence's own speed.</summary>
    public float Speed = 1f;

    [SerializeIgnore] private MountedModel _model;
    [SerializeIgnore] private Transform[] _bones;
    [SerializeIgnore] private Float3[] _positions;
    [SerializeIgnore] private Quaternion[] _rotations;
    [SerializeIgnore] private int _sequence;
    [SerializeIgnore] private float _time;

    public IModelAnimation Animation => _model?.Animation;

    /// <summary>The playing sequence's index.</summary>
    public int Sequence => _sequence;

    public SequenceInfo Current => Animation != null && _sequence < Animation.Sequences.Count ? Animation.Sequences[_sequence] : default;

    internal void Bind(MountedModel model, Transform[] bones)
    {
        _model = model;
        _sequence = model.Animation.DefaultSequence;
        _time = 0f;
        SetBones(bones);
    }

    /// <summary>Whether the model still stands unskinned, nothing having played on it yet.</summary>
    public bool Asleep => _bones == null;

    private void SetBones(Transform[] bones)
    {
        _bones = bones;
        if (bones == null) return;
        _positions = new Float3[bones.Length];
        _rotations = new Quaternion[bones.Length];
    }

    // Skins the model the first time something plays on it, the prop it belongs to keeping track of its renderers
    private void Wake()
    {
        if (_bones != null || _model == null) return;
        Prop prop = GetComponentInParent<Prop>();
        SetBones(_model.Skin(GameObject, prop.IsValid() ? prop.ReplaceRenderer : null));
    }

    /// <summary>Plays a sequence from its start. Out of range indices wrap round.</summary>
    public void Play(int sequence)
    {
        if (Animation == null || Animation.Sequences.Count == 0) return;
        int count = Animation.Sequences.Count;
        _sequence = ((sequence % count) + count) % count;
        _time = 0f;
        Wake();
    }

    /// <summary>Back to the resting sequence. Nothing to do for a model nothing has played on.</summary>
    public void Rest()
    {
        if (Asleep) return;
        Play(Animation?.DefaultSequence ?? 0);
    }

    public override void Update()
    {
        if (Animation == null || _bones == null) return;

        SequenceInfo sequence = Current;
        if (sequence.FrameCount <= 1)
        {
            Pose(0f);
            return;
        }

        _time += Time.DeltaTime * Speed;
        float frame = _time * MathF.Max(1f, sequence.Fps);
        float last = sequence.FrameCount - 1;
        frame = sequence.Loops ? frame % last : MathF.Min(frame, last);
        Pose(frame);
    }

    private void Pose(float frame)
    {
        Animation.Sample(_sequence, frame, _positions, _rotations);
        for (int i = 0; i < _bones.Length; i++)
        {
            Transform bone = _bones[i];
            if (bone == null) continue;
            bone.LocalPosition = _positions[i];
            bone.LocalRotation = _rotations[i];
        }
    }
}
