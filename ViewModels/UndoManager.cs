using System;
using System.Collections.Generic;

namespace PDFEditor.ViewModels;

public interface IUndoAction
{
    string Name { get; }

    void Undo();

    void Redo();
}

public sealed class DelegateUndoAction : IUndoAction
{
    private readonly Action _undo;
    private readonly Action _redo;

    public DelegateUndoAction(string name, Action undo, Action redo)
    {
        Name = name;
        _undo = undo;
        _redo = redo;
    }

    public string Name { get; }

    public void Undo() => _undo();

    public void Redo() => _redo();
}

/// <summary>
/// Historique annuler / retablir. Chaque action recoit un identifiant unique :
/// le document est « modifie » tant que l'action au sommet de la pile n'est pas
/// celle qui l'etait au dernier enregistrement.
/// </summary>
public sealed class UndoManager : ObservableObject
{
    private const int Limit = 120;

    private readonly List<(long Id, IUndoAction Action)> _undo = new();
    private readonly List<(long Id, IUndoAction Action)> _redo = new();
    private long _nextId = 1;
    private long _savedId;

    public event Action? StateChanged;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public bool IsApplying { get; private set; }

    public string UndoLabel => CanUndo ? $"Annuler « {_undo[^1].Action.Name} »" : "Annuler";

    public string RedoLabel => CanRedo ? $"Rétablir « {_redo[^1].Action.Name} »" : "Rétablir";

    private long StateId => _undo.Count == 0 ? 0 : _undo[^1].Id;

    public bool IsAtSavedState => StateId == _savedId;

    /// <summary>Enregistre une action deja effectuee.</summary>
    public void Push(IUndoAction action)
    {
        if (IsApplying)
        {
            return;
        }

        _undo.Add((_nextId++, action));
        _redo.Clear();

        if (_undo.Count > Limit)
        {
            _undo.RemoveAt(0);
        }

        Notify();
    }

    /// <summary>Effectue l'action puis l'enregistre.</summary>
    public void Execute(IUndoAction action)
    {
        action.Redo();
        Push(action);
    }

    public void Push(string name, Action undo, Action redo) => Push(new DelegateUndoAction(name, undo, redo));

    public void Undo()
    {
        if (!CanUndo)
        {
            return;
        }

        var entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        IsApplying = true;
        try
        {
            entry.Action.Undo();
        }
        finally
        {
            IsApplying = false;
        }

        _redo.Add(entry);
        Notify();
    }

    public void Redo()
    {
        if (!CanRedo)
        {
            return;
        }

        var entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);

        IsApplying = true;
        try
        {
            entry.Action.Redo();
        }
        finally
        {
            IsApplying = false;
        }

        _undo.Add(entry);
        Notify();
    }

    public void MarkSaved()
    {
        _savedId = StateId;
        Notify();
    }

    /// <summary>Force l'etat « modifie » (aucune action enregistree ne correspond).</summary>
    public void MarkDirty()
    {
        _savedId = -1;
        Notify();
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _savedId = 0;
        Notify();
    }

    private void Notify()
    {
        Raise(nameof(CanUndo), nameof(CanRedo), nameof(UndoLabel), nameof(RedoLabel), nameof(IsAtSavedState));
        StateChanged?.Invoke();
    }
}
