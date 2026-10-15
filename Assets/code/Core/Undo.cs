using System;
using System.Collections.Generic;
using Prowl.Runtime;

namespace Slopbox;

/// <summary>Something the player did that pressing undo can take back.</summary>
public interface IUndoable
{
    /// <summary>Shown as "Undone {Label}".</summary>
    string Label { get; }

    /// <summary>False once whatever it would undo is already gone, so it is skipped.</summary>
    bool IsValid { get; }

    void Undo();
}

/// <summary>
/// Undoes by destroying what was created: spawned GameObjects, or components added to existing ones
/// such as constraints. Covers spawning and every constraint tool.
/// </summary>
public sealed class CreatedUndo : IUndoable
{
    private readonly List<EngineObject> _created = new();

    public string Label { get; }

    public CreatedUndo(string label, params EngineObject[] created)
    {
        Label = label;
        foreach (EngineObject obj in created)
            if (obj.IsValid()) _created.Add(obj);
    }

    public bool IsValid
    {
        get
        {
            foreach (EngineObject obj in _created)
                if (Alive(obj)) return true;
            return false;
        }
    }

    public void Undo()
    {
        foreach (EngineObject obj in _created)
        {
            if (!Alive(obj)) continue;
            if (obj is GameObject go) go.Destroy();
            else if (obj is Component c) c.GameObject.RemoveComponent(c);
        }
    }

    private static bool Alive(EngineObject obj) => obj switch
    {
        GameObject go => go.IsValid() && !go.IsDestroyQueued,
        Component c => c.IsValid() && c.GameObject.IsValid() && !c.GameObject.IsDestroyQueued,
        _ => obj.IsValid(),
    };
}

/// <summary>Undoes by running a callback, for changes that are not creations (colour, properties).</summary>
public sealed class ActionUndo(string label, Action undo, Func<bool> isValid = null) : IUndoable
{
    public string Label { get; } = label;
    public bool IsValid => isValid == null || isValid();
    public void Undo() => undo();
}

/// <summary>The local player's undo stack. Press <see cref="Controls.Undo"/> to pop.</summary>
public sealed class UndoHistory
{
    public const int Capacity = 512;

    private readonly LinkedList<IUndoable> _entries = new();

    public int Count => _entries.Count;

    public void Add(IUndoable entry)
    {
        _entries.AddLast(entry);
        if (_entries.Count > Capacity) _entries.RemoveFirst();
    }

    public void Add(string label, params EngineObject[] created) => Add(new CreatedUndo(label, created));

    /// <summary>Undoes the newest entry that still has something to undo.</summary>
    public bool UndoLast()
    {
        while (_entries.Count > 0)
        {
            IUndoable entry = _entries.Last!.Value;
            _entries.RemoveLast();
            if (!entry.IsValid) continue;

            try
            {
                entry.Undo();
                Notify.Undo($"Undone {entry.Label}");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Notify.Error("Undo failed");
            }
            return true;
        }

        Notify.Error("Nothing to undo");
        return false;
    }

    public void Clear() => _entries.Clear();
}
