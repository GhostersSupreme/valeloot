using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ValeLoot;

/// <summary>Named local filters. Profile contents change only through an explicit save or creation.</summary>
internal sealed class ProfileStore
{
    public const string DefaultName = "Default";
    private const string DirectoryName = "valeloot-profiles";
    private const string ActiveFileName = ".active";
    private readonly string _livePath;
    private readonly string _directory;
    private readonly string _activePath;

    public ProfileStore(string livePath)
    {
        _livePath = livePath;
        string root = Path.GetDirectoryName(livePath) ?? "";
        _directory = Path.Combine(root, DirectoryName);
        _activePath = Path.Combine(_directory, ActiveFileName);
    }

    public readonly record struct Entry(string Name, bool Active);

    public Entry[] List()
    {
        EnsureMigrated();
        string active = ActiveName();
        var entries = new List<Entry>();
        foreach (string path in Directory.GetFiles(_directory, "*.txt", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (TryName(name, out string clean)) entries.Add(new Entry(clean, Same(clean, active)));
        }
        entries.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return entries.ToArray();
    }

    public string Read(string name)
    {
        EnsureMigrated();
        string clean = RequireName(name);
        return File.ReadAllText(ProfilePath(clean));
    }

    public void SaveActive(ReadOnlySpan<byte> utf8)
    {
        EnsureMigrated();
        string active = ActiveName();
        AtomicWrite(_livePath, utf8);
        AtomicWrite(ProfilePath(active), utf8);
    }

    public void Create(string name, ReadOnlySpan<byte> utf8)
    {
        EnsureMigrated();
        string path = ProfilePath(RequireName(name));
        if (File.Exists(path)) throw new InvalidOperationException("a profile with that name already exists");
        AtomicWrite(path, utf8);
    }

    public void Duplicate(string source, string name)
    {
        byte[] text = Encoding.UTF8.GetBytes(Read(source));
        Create(name, text);
    }

    public void Rename(string source, string name)
    {
        EnsureMigrated();
        string oldName = RequireName(source);
        string newName = RequireName(name);
        string oldPath = ProfilePath(oldName);
        string newPath = ProfilePath(newName);
        if (!File.Exists(oldPath)) throw new FileNotFoundException("no such profile", oldPath);
        if (File.Exists(newPath)) throw new InvalidOperationException("a profile with that name already exists");
        File.Move(oldPath, newPath);
        if (Same(ActiveName(), oldName)) AtomicWrite(_activePath, Encoding.UTF8.GetBytes(newName));
    }

    public void Activate(string name)
    {
        EnsureMigrated();
        string clean = RequireName(name);
        string next = ProfilePath(clean);
        if (!File.Exists(next)) throw new FileNotFoundException("no such profile", next);
        AtomicWrite(_livePath, File.ReadAllBytes(next));
        AtomicWrite(_activePath, Encoding.UTF8.GetBytes(clean));
    }

    internal static bool TryName(string? name, out string clean)
    {
        clean = (name ?? "").Trim();
        if (clean.Length is < 1 or > 64 || clean is "." or "..") return false;
        foreach (char c in clean)
        {
            if (char.IsControl(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|') return false;
        }
        return true;
    }

    private void EnsureMigrated()
    {
        Directory.CreateDirectory(_directory);
        if (!File.Exists(_livePath)) throw new FileNotFoundException("the live filter does not exist", _livePath);
        if (!File.Exists(_activePath)) AtomicWrite(_activePath, Encoding.UTF8.GetBytes(DefaultName));
        string active = ActiveName();
        string path = ProfilePath(active);
        if (!File.Exists(path)) AtomicWrite(path, File.ReadAllBytes(_livePath));
    }

    private string ActiveName()
    {
        string raw = File.Exists(_activePath) ? File.ReadAllText(_activePath) : DefaultName;
        return TryName(raw, out string clean) ? clean : DefaultName;
    }


    private string ProfilePath(string name) => Path.Combine(_directory, name + ".txt");
    private static string RequireName(string name) => TryName(name, out string clean)
        ? clean : throw new InvalidDataException("profile names must be 1-64 safe filename characters");
    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool SameBytes(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static void AtomicWrite(string path, ReadOnlySpan<byte> bytes)
    {
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) stream.Write(bytes);
        File.Move(temp, path, overwrite: true);
    }
}
