using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SpotiSync.Sync;

/// <summary>One "move item so it ends at index" step.</summary>
/// <param name="Item">Item id.</param>
/// <param name="NewIndex">Final index after the move.</param>
public readonly record struct MoveStep(Guid Item, int NewIndex);

/// <summary>Minimal set of playlist operations turning the current content into the desired one.</summary>
public sealed class PlaylistPlan
{
    /// <summary>Gets the items to remove.</summary>
    public List<Guid> Remove { get; } = [];

    /// <summary>Gets the items to append (in order).</summary>
    public List<Guid> Append { get; } = [];

    /// <summary>Gets the moves, to be applied in order after removing and appending.</summary>
    public List<MoveStep> Moves { get; } = [];

    /// <summary>Gets or sets a value indicating whether the whole content should be replaced instead.</summary>
    public bool Replace { get; set; }

    /// <summary>Gets a value indicating whether nothing needs to be written.</summary>
    public bool IsEmpty => !Replace && Remove.Count == 0 && Append.Count == 0 && Moves.Count == 0;

    /// <summary>Gets the number of write calls the plan needs.</summary>
    public int WriteCalls => Replace ? 1 : (Remove.Count > 0 ? 1 : 0) + (Append.Count > 0 ? 1 : 0) + Moves.Count;
}

/// <summary>Computes playlist diffs.</summary>
public static class PlaylistDiff
{
    /// <summary>
    /// Above this many moves a full replace is used: in Jellyfin 12.2 every single move rewrites the
    /// whole playlist, while a replace costs two rewrites.
    /// </summary>
    public const int MaxMoves = 8;

    /// <summary>Computes the plan.</summary>
    /// <param name="current">Current items in order.</param>
    /// <param name="desired">Desired items in order (distinct).</param>
    /// <param name="mirror">Mirror (remove + reorder) or add-only.</param>
    /// <returns>The plan.</returns>
    public static PlaylistPlan Compute(IReadOnlyList<Guid> current, IReadOnlyList<Guid> desired, bool mirror)
    {
        var plan = new PlaylistPlan();
        var currentSet = new HashSet<Guid>(current.Count);
        bool duplicates = false;
        foreach (var id in current)
        {
            duplicates |= !currentSet.Add(id);
        }

        if (!mirror)
        {
            foreach (var id in desired)
            {
                if (!currentSet.Contains(id))
                {
                    plan.Append.Add(id);
                }
            }

            return plan;
        }

        var desiredPos = new Dictionary<Guid, int>(desired.Count);
        for (int i = 0; i < desired.Count; i++)
        {
            desiredPos.TryAdd(desired[i], i);
        }

        // Jellyfin removes by item id, so duplicate entries cannot be removed individually.
        if (duplicates)
        {
            plan.Replace = true;
            return plan;
        }

        var sim = new List<Guid>(Math.Max(current.Count, desired.Count));
        foreach (var id in current)
        {
            if (desiredPos.ContainsKey(id))
            {
                sim.Add(id);
            }
            else
            {
                plan.Remove.Add(id);
            }
        }

        foreach (var id in desired)
        {
            if (!currentSet.Contains(id))
            {
                plan.Append.Add(id);
                sim.Add(id);
            }
        }

        // Items on a longest increasing subsequence (by desired position) stay put; the rest move.
        var keep = LongestIncreasing(sim, desiredPos);
        for (int i = 0; i < desired.Count; i++)
        {
            var id = desired[i];
            if (keep.Contains(id))
            {
                continue;
            }

            sim.Remove(id);
            int target = i == 0 ? 0 : sim.IndexOf(desired[i - 1]) + 1;
            sim.Insert(target, id);
            plan.Moves.Add(new MoveStep(id, target));
        }

        int ops = plan.Remove.Count + plan.Append.Count + plan.Moves.Count;
        if (plan.Moves.Count > MaxMoves || (ops > desired.Count && desired.Count > 0))
        {
            plan.Remove.Clear();
            plan.Append.Clear();
            plan.Moves.Clear();
            plan.Replace = true;
        }

        return plan;
    }

    /// <summary>Applies a plan to a list (used by tests and fakes to verify plans).</summary>
    /// <param name="list">List to mutate.</param>
    /// <param name="plan">Plan.</param>
    /// <param name="desired">Desired content (for replace).</param>
    public static void ApplyTo(List<Guid> list, PlaylistPlan plan, IReadOnlyList<Guid> desired)
    {
        if (plan.Replace)
        {
            list.Clear();
            list.AddRange(desired);
            return;
        }

        var rm = new HashSet<Guid>(plan.Remove);
        list.RemoveAll(rm.Contains);
        list.AddRange(plan.Append);
        foreach (var m in plan.Moves)
        {
            list.Remove(m.Item);
            list.Insert(Math.Min(m.NewIndex, list.Count), m.Item);
        }
    }

    private static HashSet<Guid> LongestIncreasing(List<Guid> seq, Dictionary<Guid, int> pos)
    {
        int n = seq.Count;
        var tails = new int[n];
        var tailIdx = new int[n];
        var prev = new int[n];
        int len = 0;
        for (int i = 0; i < n; i++)
        {
            int v = pos[seq[i]];
            int lo = 0, hi = len;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (tails[mid] < v)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            tails[lo] = v;
            tailIdx[lo] = i;
            prev[i] = lo > 0 ? tailIdx[lo - 1] : -1;
            if (lo == len)
            {
                len++;
            }
        }

        var keep = new HashSet<Guid>(len);
        for (int i = len > 0 ? tailIdx[len - 1] : -1; i >= 0; i = prev[i])
        {
            keep.Add(seq[i]);
        }

        return keep;
    }
}
