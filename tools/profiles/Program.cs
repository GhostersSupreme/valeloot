using System;
using System.IO;
using System.Text;
using ValeLoot;

string root = Path.Combine(Path.GetTempPath(), "valeloot-profiles-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    string live = Path.Combine(root, "valeloot-filter.txt");
    File.WriteAllText(live, "Show \"original\"\n");
    var store = new ProfileStore(live);

    ProfileStore.Entry[] migrated = store.List();
    Must(migrated.Length == 1 && migrated[0].Name == "Default" && migrated[0].Active, "single filter migrates to active Default");
    Must(store.Read("Default") == "Show \"original\"\n", "migration preserves exact text");
    Must(File.ReadAllBytes(live).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("Show \"original\"\n")),
         "migration never rewrites the existing live filter");
    byte[] defaultBeforeUpgrade = File.ReadAllBytes(Path.Combine(root, "valeloot-profiles", "Default.txt"));
    store.Create("Existing", Encoding.UTF8.GetBytes("Show \"existing profile\"\r\n"));
    byte[] existingBeforeUpgrade = File.ReadAllBytes(Path.Combine(root, "valeloot-profiles", "Existing.txt"));
    var upgradedStore = new ProfileStore(live);
    _ = upgradedStore.List();
    Must(File.ReadAllBytes(Path.Combine(root, "valeloot-profiles", "Default.txt")).AsSpan()
        .SequenceEqual(defaultBeforeUpgrade), "upgrade never rewrites an existing Default profile");
    Must(File.ReadAllBytes(Path.Combine(root, "valeloot-profiles", "Existing.txt")).AsSpan()
        .SequenceEqual(existingBeforeUpgrade), "upgrade never rewrites another existing profile");
    bool rejectedExisting = false;
    try { store.Create("Existing", Encoding.UTF8.GetBytes("replacement")); }
    catch (InvalidOperationException) { rejectedExisting = true; }
    Must(rejectedExisting, "create/import refuses to overwrite an existing profile");
    Must(File.ReadAllBytes(Path.Combine(root, "valeloot-profiles", "Existing.txt")).AsSpan()
        .SequenceEqual(existingBeforeUpgrade), "rejected create leaves the existing profile byte-identical");

    store.Create("Wizard — Frost", Encoding.UTF8.GetBytes("Show \"frost\"\n"));
    store.Activate("Wizard — Frost");
    Must(File.ReadAllText(live) == "Show \"frost\"\n", "activation writes canonical live file");

    store.SaveActive(Encoding.UTF8.GetBytes("Show \"edited frost\"\n"));
    File.WriteAllText(live, "Show \"manual live edit\"\n");
    _ = store.List();
    Must(store.Read("Wizard — Frost") == "Show \"edited frost\"\n", "listing never overwrites the active profile");
    store.Activate("Default");
    Must(store.Read("Wizard — Frost") == "Show \"edited frost\"\n", "switching never overwrites the outgoing profile");
    store.Activate("Wizard — Frost");
    Must(File.ReadAllText(live) == "Show \"edited frost\"\n", "save updates active profile exactly");
    Must(store.Read("Default") == "Show \"original\"\n", "saving one profile never overwrites Default");
    Must(store.Read("Existing") == "Show \"existing profile\"\r\n", "saving one profile never overwrites another profile");

    store.Duplicate("Wizard — Frost", "Artifact Hunting");
    store.Rename("Artifact Hunting", "Artifacts");
    Must(store.Read("Artifacts") == "Show \"edited frost\"\n", "duplicate and rename preserve text");

    Must(!ProfileStore.TryName("../escape", out _), "traversal name rejected");
    Must(!ProfileStore.TryName("bad:name", out _), "unsafe filename rejected");
    Console.WriteLine("profile contracts passed");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static void Must(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
