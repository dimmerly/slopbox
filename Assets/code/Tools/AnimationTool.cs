using System.Collections.Generic;
using Prowl.PaperUI;
using Prowl.PaperUI.LayoutEngine;
using Prowl.Runtime;
using Prowl.Runtime.Networking;
using Slopbox.Mounting;
using static Slopbox.Theme;

namespace Slopbox;

[Tool("Animation", "Plays the animations of models from mounted games, such as Half-Life's NPCs. Aim at one and click through its sequences.", Category = "Construction", Order = 11)]
public sealed class AnimationTool : BaseTool
{
    [ToolSetting("Speed", Min = 0f, Max = 3f, Format = "0.00x")] public float Speed = 1f;

    private ModelAnimator _last;
    internal GameObject SelectedObject => _last.IsValid() ? _last.GetComponentInParent<Prop>().GameObject : null;
    internal void SelectObject(GameObject target) => _last = target.IsValid() ? target.GetComponentInChildren<ModelAnimator>() : null;

    public override string PrimaryHint => "Next sequence";
    public override string SecondaryHint => "Previous sequence";
    public override string ReloadHint => "Back to idle";

    public override string Status
    {
        get
        {
            ModelAnimator animator = AimedAt();
            if (animator.IsNotValid()) return "Aim at a model from a mounted game";
            IReadOnlyList<SequenceInfo> sequences = animator.Animation.Sequences;
            return $"{animator.GameObject.Name}: {animator.Current.Name} ({animator.Sequence + 1} of {sequences.Count})";
        }
    }

    private ModelAnimator AimedAt()
    {
        if (Owner.IsNotValid() || !Owner.Trace(Spawner.Reach, out RaycastHit hit) || hit.Transform == null) return null;
        ModelAnimator animator = hit.Transform.GameObject.GetComponentInParent<ModelAnimator>();
        return animator.IsValid() && animator.Animation != null && animator.Animation.Sequences.Count > 0 ? animator : null;
    }

    private static ModelAnimator Of(in ToolTrace tr)
    {
        if (!tr.Hit || tr.Object.IsNotValid()) return null;
        ModelAnimator animator = tr.Object.GetComponentInParent<ModelAnimator>();
        return animator.IsValid() && animator.Animation != null && animator.Animation.Sequences.Count > 0 ? animator : null;
    }

    public override bool Primary(in ToolTrace tr) => Step(Of(tr), 1);

    public override bool Secondary(in ToolTrace tr) => Step(Of(tr), -1);

    public override bool Reload(in ToolTrace tr)
    {
        ModelAnimator animator = Of(tr);
        if (animator.IsNotValid()) return false;
        animator.Speed = Speed;
        animator.Rest();
        _last = animator;
        return true;
    }

    private bool Step(ModelAnimator animator, int step)
    {
        if (animator.IsNotValid()) return false;
        animator.Speed = Speed;
        animator.Play(animator.Sequence + step);
        _last = animator;
        return true;
    }

    /// <summary>Every sequence of the model last clicked, to pick one directly.</summary>
    public override void Inspector(Paper paper)
    {
        Ui.Caption(paper, "Sequences", 2);
        using (paper.Column("sequences").Height(UnitValue.Auto).Padding(S2, S2, 0, 0).Gap(Px(1f)).Enter())
        {
            if (_last.IsNotValid() || _last.Animation == null)
            {
                Ui.Description(paper, "Click a model to list its sequences here.", 0, Theme.White(0.5f));
                return;
            }

            IReadOnlyList<SequenceInfo> sequences = _last.Animation.Sequences;
            for (int i = 0; i < sequences.Count; i++)
            {
                int index = i;
                SequenceInfo sequence = sequences[i];
                string label = sequence.Loops ? sequence.Name : $"{sequence.Name} (once)";
                Ui.ToolItem(paper, label, _last.Sequence == i, () =>
                {
                    if (_last.IsNotValid()) return;
                    if (Networking.IsClient) Owner.RequestAnimation(SelectedObject, index, Speed);
                    else { _last.Speed = Speed; _last.Play(index); }
                }, i);
            }
        }
    }
}
