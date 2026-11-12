// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json.Serialization;

namespace SpaceTangent.Application;

/// <summary>Binary layout of independent observers. Leaf numbers index the scenario's view list.</summary>
public sealed record ObserverLayout(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? View,
    ViewSplit Direction = ViewSplit.SideBySide,
    double FirstShare = .5,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ObserverLayout? First = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ObserverLayout? Second = null)
{
    internal const int MaximumViews = 16;

    internal bool IsDefault => View == 0 || (First?.View == 0 && Second?.View == 1 && FirstShare == .5);

    internal static ObserverLayout Leaf(int view) => new(view);

    internal static ObserverLayout Restore(ObserverLayout? layout, int count, ViewSplit direction)
    {
        if (count is < 1 or > MaximumViews || !Enum.IsDefined(direction))
            throw new InvalidDataException("The scenario has an invalid view layout.");
        if (layout is null)
            return count switch
            {
                1 => Leaf(0),
                2 => new(null, direction, .5, Leaf(0), Leaf(1)),
                _ => throw new InvalidDataException("A scenario with more than two views needs its layout.")
            };

        var seen = new bool[count];
        if (layout.Direction != direction || !layout.Validate(seen, 0) || seen.Any(used => !used))
            throw new InvalidDataException("The scenario view layout does not describe each view exactly once.");
        return layout;
    }

    private bool Validate(bool[] seen, int depth)
    {
        if (depth >= MaximumViews || !Enum.IsDefined(Direction) || !double.IsFinite(FirstShare) || FirstShare is <= 0 or >= 1)
            return false;
        if (View is { } view)
        {
            if (First is not null || Second is not null || (uint)view >= (uint)seen.Length || seen[view])
                return false;
            seen[view] = true;
            return true;
        }
        return First is not null && Second is not null && First.Validate(seen, depth + 1) && Second.Validate(seen, depth + 1);
    }

    internal ObserverLayout Insert(int view, int added, ViewSplit direction) => View is { } leaf
        ? (leaf == view ? new(null, direction, .5, this, Leaf(added)) : this)
        : this with { First = First!.Insert(view, added, direction), Second = Second!.Insert(view, added, direction) };

    internal ObserverLayout? Remove(int view)
    {
        if (View is { } leaf)
            return leaf == view ? null : Leaf(leaf > view ? leaf - 1 : leaf);
        ObserverLayout? first = First!.Remove(view), second = Second!.Remove(view);
        return first is null || second is null ? first ?? second : this with { First = first, Second = second };
    }

    internal ObserverLayout Resize(ReadOnlySpan<char> path, double share)
    {
        if (View is not null)
            throw new ArgumentException("A leaf has no divider.", nameof(path));
        if (path.IsEmpty)
            return this with { FirstShare = share };
        return path[0] switch
        {
            '0' => this with { First = First!.Resize(path[1..], share) },
            '1' => this with { Second = Second!.Resize(path[1..], share) },
            _ => throw new ArgumentException("The view divider path is invalid.", nameof(path))
        };
    }
}
