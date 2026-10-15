using System;
using System.Collections.Generic;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox;

/// <summary>
/// Runs whichever <see cref="BaseTool"/> is selected: left, right and R go to the tool with a trace of
/// what is aimed at. Tools are picked in the spawn menu's tool list.
/// </summary>
public sealed class ToolGun : Equipment
{
    public float Range = 300f;

    private BaseTool _tool;
    private Beam _tracer;
    private Float3 _tracerEnd;
    private float _tracerFade;
    private Material _bodyMat, _darkMat, _screenMat;

    public override string Name => "Tool Gun";
    public override string Description => Tool != null ? Tool.Description : "Pick a tool in the spawn menu.";
    public override Color Tint => new(0.9f, 0.9f, 0.95f, 1f);

    public BaseTool Tool
    {
        get
        {
            if (_tool == null && GameManager.Instance.Tools.All.Count > 0) Select(GameManager.Instance.Tools.All[0]);
            return _tool;
        }
    }

    public override IEnumerable<InputHint> Hints
    {
        get
        {
            BaseTool tool = Tool;
            if (tool == null) yield break;
            if (tool.PrimaryHint.Length > 0) yield return new("LMB", tool.PrimaryHint);
            if (tool.SecondaryHint.Length > 0) yield return new("RMB", tool.SecondaryHint);
            if (tool.ReloadHint.Length > 0) yield return new("R", tool.ReloadHint);
        }
    }

    public void Select(BaseTool tool)
    {
        if (tool == _tool) return;
        _tool?.OnDeselect();
        _tool = tool;
        if (tool == null) return;
        tool.Gun = this;
        tool.OnSelect();
    }

    public override void OnHolster() => _tool?.OnDeselect();

    public override void BuildViewModel(ViewModel vm)
    {
        if (_bodyMat.IsNotValid()) _bodyMat = Materials.Lit(new Color(0.35f, 0.36f, 0.38f, 1f), 0.7f, 0.35f);
        if (_darkMat.IsNotValid()) _darkMat = Materials.Lit(new Color(0.04f, 0.04f, 0.045f, 1f), 0.3f, 0.5f);
        if (_screenMat.IsNotValid()) _screenMat = Materials.Lit(new Color(0f, 0f, 0f, 1f)).Emissive(new Color(0.49f, 0.8f, 0.17f, 1f), 2.5f);

        vm.AddPart(Shapes.Cube, _bodyMat, new Float3(0f, 0f, 0f), new Float3(0.07f, 0.09f, 0.26f));
        vm.AddPart(Shapes.Cube, _darkMat, new Float3(0f, -0.07f, -0.07f), new Float3(0.045f, 0.1f, 0.055f), new Float3(-15f, 0f, 0f));
        vm.AddPart(Shapes.Cylinder, _darkMat, new Float3(0f, 0.015f, 0.17f), new Float3(0.025f, 0.12f, 0.025f), new Float3(90f, 0f, 0f));
        vm.AddPart(Shapes.Cube, _screenMat, new Float3(-0.037f, 0.01f, -0.02f), new Float3(0.005f, 0.05f, 0.09f));
        vm.SetMuzzle(new Float3(0f, 0.015f, 0.23f));
    }

    public override void Update()
    {
        BaseTool tool = Tool;
        if (tool == null) return;

        tool.Think();
        if (!Owner.InputEnabled) return;

        if (Attack1Pressed) Use(tool.Primary);
        else if (Attack2Pressed) Use(tool.Secondary);
        else if (ReloadPressed) Use(tool.Reload);
    }

    private delegate bool ToolAction(in ToolTrace tr);

    private void Use(ToolAction action)
    {
        bool hit = Owner.Trace(Range, out RaycastHit info);
        var tr = new ToolTrace(hit, info);

        bool did;
        try
        {
            did = action(tr);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            did = false;
        }

        if (!did)
        {
            GameAudio.Error();
            return;
        }
        GameAudio.Play("confirm", 0.4f, 1.2f);
        GameAudio.Play("flashlight", 0.35f, 1.4f);
        Owner.ViewModel.Kick(0.6f);
        _tracerEnd = tr.Hit ? tr.Point : Owner.EyePosition + Owner.Forward * Range;
        _tracerFade = 1f;
    }

    public override void LateUpdate()
    {
        if (_tracerFade <= 0f)
        {
            _tracer?.Hide();
            return;
        }

        if (_tracer == null || !_tracer.IsValid) _tracer = new Beam("Tool Gun Tracer", Color.White, 0.01f);
        _tracerFade = MathF.Max(0f, _tracerFade - Time.DeltaTime * 5f);
        float a = _tracerFade;
        _tracer.SetColors(new Color(1.5f * a, 3f * a, 0.4f * a, a), new Color(1f * a, 1f * a, 1f * a, a));
        _tracer.SetWidth(0.012f * a, 0.004f * a);
        _tracer.Line(Owner.ViewModel.MuzzlePosition, _tracerEnd);
    }
}
