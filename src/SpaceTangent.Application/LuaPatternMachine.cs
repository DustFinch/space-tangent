// SPDX-License-Identifier: GPL-2.0-or-later
using MoonSharp.Interpreter;

namespace SpaceTangent.Application;

/// <summary>One operation's deterministic allowance, shared by every search of a substitution or iterator.</summary>
internal sealed class LuaPatternBudget(long maximum = 4_000_000)
{
    internal long Steps { get; private set; }
    internal void Charge()
    {
        if (++Steps > maximum)
            throw new ScriptRuntimeException("The pattern exceeds the sandbox matching work limit.");
    }
}

/// <summary>Ordered NFA simulation: earlier starts and the first split branch have higher priority.</summary>
/// <remarks>No backtracking or per-character capture allocations. Each instruction is visited at most once at a
/// position; the highest-priority path supplies its captures. A match discards lower-priority paths, but higher-priority
/// greedy paths can still extend it. The program and its capture count are bounded by the pattern compiler.</remarks>
internal sealed class LuaPatternMachine
{
    internal enum Op { Character, Split, Jump, Save, End, Match }
    internal readonly record struct Instruction(Op Kind, int Next = 0, int Other = 0, bool[]? Set = null);
    internal sealed record Match(int[] Positions)
    {
        internal int Start => Positions[0];
        internal int End => Positions[1];
    }

    private sealed class Threads(int instructions, int captures)
    {
        internal readonly int[] Pcs = new int[instructions];
        internal readonly int[] Seen = new int[instructions];
        internal readonly int[][] Captures = Enumerable.Range(0, instructions).Select(_ => new int[captures]).ToArray();
        internal int Count;
        internal int Generation;
        internal void Reset()
        {
            Count = 0;
            if (++Generation == int.MaxValue) { Array.Clear(Seen); Generation = 1; }
        }
    }

    private readonly Instruction[] _program;
    private readonly string _subject;
    private readonly bool _anchored;
    private readonly int[] _scratch, _best;
    private Threads _current, _next;
    internal LuaPatternBudget Budget { get; }

    internal LuaPatternMachine(Instruction[] program, string subject, int captures, bool anchored, LuaPatternBudget? budget = null)
    {
        _program = program;
        _subject = subject;
        _anchored = anchored;
        Budget = budget ?? new();
        _scratch = new int[captures];
        _best = new int[captures];
        _current = new(program.Length, captures);
        _next = new(program.Length, captures);
    }

    internal Match? Find(int start)
    {
        if (start > _subject.Length) return null;
        bool found = false;
        _current.Reset();
        _next.Reset();
        for (int position = Math.Max(0, start); ; position++)
        {
            if (!found && (!_anchored || position == start))
            {
                Array.Fill(_scratch, -1);
                Add(_current, 0, position);
            }
            for (int index = 0; index < _current.Count; index++)
            {
                Budget.Charge();
                int pc = _current.Pcs[index];
                Instruction instruction = _program[pc];
                if (instruction.Kind == Op.Match)
                {
                    _current.Captures[pc].CopyTo(_best, 0);
                    found = true;
                    break; // Keep only higher-priority paths already placed in _next.
                }
                if (position < _subject.Length && _subject[position] < 128 && instruction.Set![_subject[position]])
                {
                    _current.Captures[pc].CopyTo(_scratch, 0);
                    Add(_next, pc + 1, position + 1);
                }
            }
            if (position == _subject.Length || _next.Count == 0 && (found || _anchored))
                return found ? new((int[])_best.Clone()) : null;
            (_current, _next) = (_next, _current);
            _next.Reset();
        }
    }

    private void Add(Threads threads, int pc, int position)
    {
        Budget.Charge();
        if (threads.Seen[pc] == threads.Generation) return;
        threads.Seen[pc] = threads.Generation;
        Instruction instruction = _program[pc];
        switch (instruction.Kind)
        {
            case Op.Split:
                Add(threads, instruction.Next, position);
                Add(threads, instruction.Other, position);
                break;
            case Op.Jump:
                Add(threads, instruction.Next, position);
                break;
            case Op.Save:
                int old = _scratch[instruction.Next];
                _scratch[instruction.Next] = position;
                Add(threads, pc + 1, position);
                _scratch[instruction.Next] = old;
                break;
            case Op.End:
                if (position == _subject.Length) Add(threads, pc + 1, position);
                break;
            default:
                threads.Pcs[threads.Count++] = pc;
                _scratch.CopyTo(threads.Captures[pc], 0);
                break;
        }
    }
}
