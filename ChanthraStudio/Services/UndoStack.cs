using System;
using System.Collections.Generic;
using System.Linq;

namespace ChanthraStudio.Services;

/// <summary>
/// Generic snapshot-based undo / redo stack. The owner provides a
/// capture function (current state → opaque snapshot) and an apply
/// function (snapshot → mutate state back). Push() saves the CURRENT
/// state onto the undo stack and clears redo (we're forking from a new
/// history point); Undo() restores the previous state by popping
/// undo onto redo; Redo() does the inverse.
///
/// <para>
/// Capacity defaults to 50 entries. When the cap is hit, the OLDEST
/// snapshot is dropped — the rebuild walks the stack newest-first
/// (<see cref="System.Collections.Generic.Stack{T}.ToArray"/> order),
/// keeps the first N, then re-pushes oldest-first so the top stays
/// the most-recent snapshot (the 7.11b fix preserved here).
/// </para>
///
/// <para>
/// Extracted from EditorViewModel in 7.14 so the VM stays focused on
/// UI state.
/// </para>
/// </summary>
public sealed class UndoStack<TSnapshot>
{
    private readonly Func<TSnapshot> _capture;
    private readonly Action<TSnapshot> _apply;
    private readonly Stack<TSnapshot> _undo = new();
    private readonly Stack<TSnapshot> _redo = new();
    private readonly int _limit;

    /// <summary>Raised after every Push / Undo / Redo so the owner can
    /// re-query CanExecute on its RelayCommands.</summary>
    public event Action? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public UndoStack(Func<TSnapshot> capture, Action<TSnapshot> apply, int limit = 50)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _limit = Math.Max(1, limit);
    }

    /// <summary>Capture the CURRENT state and push onto undo. Clears the
    /// redo stack — forking from history makes the old future unreachable.
    /// Call BEFORE applying the mutation you want to be undoable.</summary>
    public void Push()
    {
        _undo.Push(_capture());
        TrimToLimit();
        _redo.Clear();
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(_capture());
        _apply(_undo.Pop());
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(_capture());
        _apply(_redo.Pop());
        Changed?.Invoke();
    }

    /// <summary>Clear both stacks — useful when the owner switches projects.</summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }

    private void TrimToLimit()
    {
        if (_undo.Count <= _limit) return;
        // ToArray returns newest-first (top→bottom). Keep N newest, then
        // re-push oldest-first so the top of the rebuilt stack is the
        // most-recent snapshot.
        var topToBottom = _undo.ToArray();
        var keepNewestFirst = topToBottom.Take(_limit);
        var pushOrder = keepNewestFirst.Reverse().ToList();
        _undo.Clear();
        foreach (var s in pushOrder) _undo.Push(s);
    }
}
