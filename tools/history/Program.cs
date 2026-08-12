using System;
using ValeLoot;

AlertHistory.Configure(10);
AlertHistory.Record(new[]
{
    new AlertHistory.Arrival("one", "Azure Cutlass", "Sword", 1, "rule \"Keeper\"", "KEEP", "chime"),
    new AlertHistory.Arrival("two", "Spider Card", "Card", 4, "rule \"Cards\"", "CARD", "ding"),
    new AlertHistory.Arrival("three", "Lost Shoe", "Junk", 2, "rule \"trash\"", "", null),
}, soundPlayed: true);
AlertHistory.Entry[] entries = AlertHistory.Snapshot();
Must(entries.Length == 3, "records every batch contribution");
Must(entries[0].SoundWinner && entries[0].SoundPlayed, "first sounding item wins");
Must(!entries[1].SoundWinner && entries[1].Note == "batch used an earlier sound", "later sound explains collapse");
Must(entries[2].Quantity == 2 && entries[2].Note == "matched a silent rule", "quantity and silent match retained");
for (int i = 0; i < 12; i++) AlertHistory.Record(new[] { new AlertHistory.Arrival(i.ToString(), "x", "Junk", 1, "", "", null) }, false);
Must(AlertHistory.Snapshot().Length == 10, "configured cap bounds memory");
AlertHistory.Clear();
Must(AlertHistory.Snapshot().Length == 0, "manual clear empties history");
Console.WriteLine("alert history contracts passed");

static void Must(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
