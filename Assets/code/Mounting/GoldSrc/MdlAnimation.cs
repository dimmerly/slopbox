using System;
using System.Collections.Generic;
using System.IO;
using Prowl.Vector;
using Slopbox.Mounting.GoldSrc.Mdl;

namespace Slopbox.Mounting.GoldSrc;

/// <summary>
/// A studio model's sequences. Each sequence is decoded into Prowl space the first time it plays, then kept.
/// Sequences in other files ("01.mdl") are read when first needed.
/// </summary>
internal sealed class MdlAnimation : IModelAnimation
{
    private readonly MdlFile _mdl;
    private readonly byte[] _bytes;
    private readonly string _stem;
    private readonly Dictionary<int, (Float3[] Positions, Quaternion[] Rotations)[]> _frames = new();

    public IReadOnlyList<SequenceInfo> Sequences { get; }
    public int DefaultSequence { get; }

    public MdlAnimation(MdlFile mdl, byte[] bytes, string stem, int defaultSequence)
    {
        _mdl = mdl;
        _bytes = bytes;
        _stem = stem;
        DefaultSequence = defaultSequence;

        var sequences = new SequenceInfo[mdl.Sequences.Count];
        for (int i = 0; i < sequences.Length; i++)
        {
            SequenceDesc seq = mdl.Sequences[i];
            sequences[i] = new SequenceInfo(seq.Label, seq.Fps, Math.Max(1, seq.NumFrames), (seq.Flags & 1) != 0);
        }
        Sequences = sequences;
    }

    public void Sample(int sequence, float frame, Float3[] positions, Quaternion[] rotations)
    {
        var frames = Frames(sequence);
        if (frames.Length == 0) return;

        int a = Math.Clamp((int)MathF.Floor(frame), 0, frames.Length - 1);
        int b = Math.Min(a + 1, frames.Length - 1);
        float t = Math.Clamp(frame - a, 0f, 1f);

        (Float3[] pa, Quaternion[] ra) = frames[a];
        (Float3[] pb, Quaternion[] rb) = frames[b];
        int count = Math.Min(positions.Length, pa.Length);
        for (int i = 0; i < count; i++)
        {
            positions[i] = pa[i] + (pb[i] - pa[i]) * t;
            rotations[i] = t <= 0f ? ra[i] : Quaternion.Slerp(ra[i], rb[i], t);
        }
    }

    private (Float3[], Quaternion[])[] Frames(int sequence)
    {
        if (_frames.TryGetValue(sequence, out var cached)) return cached;

        int count = sequence >= 0 && sequence < Sequences.Count ? Sequences[sequence].FrameCount : 0;
        var frames = new (Float3[], Quaternion[])[count];
        var groups = new Dictionary<int, BinaryReader>();
        try
        {
            using var self = new BinaryReader(new MemoryStream(_bytes));
            for (int f = 0; f < count; f++)
            {
                BoneTransform[] pose = _mdl.GetPose(self, group => OpenGroup(groups, group), sequence, f);
                var positions = new Float3[pose.Length];
                var rotations = new Quaternion[pose.Length];
                for (int i = 0; i < pose.Length; i++)
                {
                    positions[i] = GoldSrcAssets.Position(pose[i].Position);
                    rotations[i] = GoldSrcAssets.Rotation(pose[i].Rotation);
                }
                frames[f] = (positions, rotations);
            }
        }
        finally
        {
            foreach (BinaryReader reader in groups.Values) reader?.Dispose();
        }

        RemoveDrift(sequence, frames);
        _frames[sequence] = frames;
        return frames;
    }

    /// <summary>
    /// A looping sequence that moves (LX, LY: walks, runs) keeps a little of its travel, so standing still it slides
    /// back over the loop and snaps forward at the end, where the game would have moved the entity on. The drift
    /// from the first frame to the one before the repeated last is taken out of the motion bone and everything on it.
    /// </summary>
    private void RemoveDrift(int sequence, (Float3[] Positions, Quaternion[] Rotations)[] frames)
    {
        if (sequence < 0 || sequence >= _mdl.Sequences.Count || frames.Length < 3) return;
        SequenceDesc seq = _mdl.Sequences[sequence];
        if ((seq.Flags & 1) == 0 || (seq.MotionType & (MotionFlags.LX | MotionFlags.LY)) == 0) return;

        int bone = Math.Clamp(seq.MotionBone, 0, _mdl.Bones.Count - 1);
        var moves = new bool[_mdl.Bones.Count];
        for (int i = 0; i < moves.Length; i++)
            for (int b = i; b >= 0; b = _mdl.Bones[b].Parent)
                if (b == bone) { moves[i] = true; break; }

        // Only across the ground; rising and falling in a stride stays.
        int last = frames.Length - 2;
        Float3 drift = frames[last].Positions[bone] - frames[0].Positions[bone];
        drift = new Float3(drift.X, 0f, drift.Z);

        // The last frame repeats the first for the loop, so it is lined up with the first rather than detrended.
        Float3 end = frames[^1].Positions[bone] - frames[0].Positions[bone];
        end = new Float3(end.X, 0f, end.Z);

        for (int f = 0; f < frames.Length; f++)
        {
            Float3 offset = f == frames.Length - 1 ? end : drift * (f / (float)last);
            Float3[] positions = frames[f].Positions;
            for (int i = 0; i < positions.Length && i < moves.Length; i++)
                if (moves[i]) positions[i] -= offset;
        }
    }

    private BinaryReader OpenGroup(Dictionary<int, BinaryReader> groups, int group)
    {
        if (groups.TryGetValue(group, out BinaryReader open)) return open;
        string file = $"{_stem}{group:D2}.mdl";
        open = File.Exists(file) ? new BinaryReader(new MemoryStream(File.ReadAllBytes(file))) : null;
        groups[group] = open;
        return open;
    }
}
