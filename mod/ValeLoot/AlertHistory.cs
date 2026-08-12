using System;
using System.Collections.Generic;

namespace ValeLoot;

/// <summary>Bounded, memory-only pickup decisions. Entries keep the verdict made at pickup time.</summary>
internal static class AlertHistory
{
    internal readonly record struct Arrival(
        string Uid, string Name, string Type, int Quantity, string Rule, string Tag, string? Sound);
    internal readonly record struct Entry(
        long Sequence, DateTime At, string Uid, string Name, string Type, int Quantity,
        string Rule, string Tag, string? Sound, bool SoundWinner, bool SoundPlayed, string Note);

    private static readonly object Gate = new();
    private static readonly List<Entry> Entries = new();
    private static int _cap = 200;
    private static long _sequence;

    public static void Configure(int cap) => _cap = Math.Clamp(cap, 10, 2000);

    public static void Record(IReadOnlyList<Arrival> arrivals, bool soundPlayed)
    {
        if (arrivals.Count == 0) return;
        int winner = -1;
        for (int i = 0; i < arrivals.Count; i++) if (arrivals[i].Sound is not null) { winner = i; break; }
        DateTime at = DateTime.Now;
        lock (Gate)
        {
            for (int i = 0; i < arrivals.Count; i++)
            {
                Arrival item = arrivals[i];
                bool wins = i == winner;
                string note = item.Sound is null
                    ? item.Rule.Length == 0 ? "no rule matched" : "matched a silent rule"
                    : wins ? soundPlayed ? "sound winner" : "sound selected but not played"
                    : "batch used an earlier sound";
                Entries.Add(new Entry(++_sequence, at, item.Uid, item.Name, item.Type, item.Quantity,
                    item.Rule, item.Tag, item.Sound, wins, wins && soundPlayed, note));
            }
            int excess = Entries.Count - _cap;
            if (excess > 0) Entries.RemoveRange(0, excess);
        }
    }

    public static Entry[] Snapshot()
    {
        lock (Gate) return Entries.ToArray();
    }

    public static void Clear()
    {
        lock (Gate) Entries.Clear();
    }
}
