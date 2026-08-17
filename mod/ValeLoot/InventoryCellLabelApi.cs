using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

namespace ValeLoot;

/// <summary>
/// Cooperative bottom-right inventory-cell text extension point for soft-dependent plugins.
///
/// ValeLoot already owns the inventory repaint hooks, so another plugin must not detour the same
/// UIInventoryTab RenderPage/Redraw bodies. This API instead watches UIInventoryItem.Draw (a different
/// native surface) and lets one provider supply a short label for the cell after the game has bound it.
///
/// The existing Count/quantity TMP label is reused as the anchor because it is already laid out in the
/// bottom-right corner by SpiritVale. Its original count text is preserved; the external label is appended
/// as a second, smaller line. Pooled cells are safe because every Draw reapplies or removes the extension.
/// </summary>
public static class ValeLootInventoryCellApi
{
    private static readonly object Gate = new();
    private static Func<IntPtr, string?>? _provider;

    public static bool Register(Func<IntPtr, string?> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (Gate)
        {
            if (_provider is not null && !ReferenceEquals(_provider, provider)) return false;
            if (!InventoryCellLabel.EnsureInstalled()) return false;
            _provider = provider;
            return true;
        }
    }

    public static void Unregister(Func<IntPtr, string?> provider)
    {
        lock (Gate)
        {
            if (ReferenceEquals(_provider, provider)) _provider = null;
        }
        InventoryCellLabel.RefreshVisible();
    }

    /// <summary>Re-evaluate recently visible cells, e.g. after an asynchronous market lookup completes.</summary>
    public static bool Refresh() => InventoryCellLabel.RefreshVisible();

    /// <summary>Human-readable install state for diagnostics.</summary>
    public static string Status() => InventoryCellLabel.Status;

    internal static string? BuildLabel(IntPtr cell)
    {
        // Grimoires deliberately do not expose trustworthy UIInventoryItem.Data in the current client.
        if (cell == IntPtr.Zero || InventoryPaint.IsPresentationOnly(cell)) return null;

        Func<IntPtr, string?>? provider;
        lock (Gate) provider = _provider;
        if (provider is null) return null;
        try { return provider(cell); }
        catch { return null; }
    }
}

internal static class InventoryCellLabel
{
    private delegate void Draw0Fn(IntPtr self, IntPtr methodInfo);
    private delegate void Draw1Fn(IntPtr self, IntPtr arg0, IntPtr methodInfo);
    private delegate void Draw2Fn(IntPtr self, IntPtr arg0, IntPtr arg1, IntPtr methodInfo);
    private delegate void Draw3Fn(IntPtr self, IntPtr arg0, IntPtr arg1, IntPtr arg2, IntPtr methodInfo);
    private delegate IntPtr PtrFn(IntPtr self, IntPtr methodInfo);
    private delegate int IntFn(IntPtr self, IntPtr methodInfo);
    private delegate IntPtr ChildFn(IntPtr self, int index, IntPtr methodInfo);
    private delegate IntPtr GetComponentFn(IntPtr self, IntPtr type, IntPtr methodInfo);
    private delegate void SetTextFn(IntPtr self, IntPtr value, IntPtr methodInfo);

    private const string Marker = "\u200b<color=#facc15>";
    private static readonly string[] GameAssemblies = { "Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll" };
    private static readonly List<object> DetourHandles = new();
    private static readonly List<Delegate> Hooks = new();
    private static readonly List<Delegate?> Originals = new();
    private static readonly Dictionary<IntPtr, DateTime> RecentlyDrawn = new();
    private static readonly Dictionary<IntPtr, IntPtr> CountTextByCell = new();
    private static readonly Dictionary<IntPtr, string> BaseCountByCell = new();
    private static readonly List<IntPtr> Sweep = new();

    private static PtrFn? _getTransform;
    private static IntFn? _getChildCount;
    private static ChildFn? _getChild;
    private static PtrFn? _getName;
    private static GetComponentFn? _getComponent;
    private static PtrFn? _getText;
    private static SetTextFn? _setText;
    private static IntPtr _tmpType;
    private static bool _installed;
    private static bool _installAttempted;
    private static bool _reportedFirstLabel;
    private static int _errors;

    public static string Status { get; private set; } = "not installed";

    public static bool EnsureInstalled()
    {
        if (_installed) return true;
        if (_installAttempted) return false;
        _installAttempted = true;

        IntPtr cellClass = Il2CppMeta.FindClass("", "UIInventoryItem", GameAssemblies);
        IntPtr component = Il2CppMeta.FindClass("UnityEngine", "Component", "UnityEngine.CoreModule.dll");
        IntPtr transform = Il2CppMeta.FindClass("UnityEngine", "Transform", "UnityEngine.CoreModule.dll");
        IntPtr unityObject = Il2CppMeta.FindClass("UnityEngine", "Object", "UnityEngine.CoreModule.dll");
        IntPtr tmp = Il2CppMeta.FindClass("TMPro", "TMP_Text", "Unity.TextMeshPro.dll", "TextMeshPro.dll");

        Il2CppMeta.MethodInfo? getTransform = Il2CppMeta.FindOverload(component, "get_transform");
        Il2CppMeta.MethodInfo? getChildCount = Il2CppMeta.FindOverload(transform, "get_childCount");
        Il2CppMeta.MethodInfo? getChild = Il2CppMeta.FindOverload(transform, "GetChild", "System.Int32");
        Il2CppMeta.MethodInfo? getName = Il2CppMeta.FindOverload(unityObject, "get_name");
        Il2CppMeta.MethodInfo? getComponent = Il2CppMeta.FindOverload(component, "GetComponent", "System.Type");
        Il2CppMeta.MethodInfo? getText = Il2CppMeta.FindOverload(tmp, "get_text");
        Il2CppMeta.MethodInfo? setText = Il2CppMeta.FindOverload(tmp, "set_text", "System.String");

        if (cellClass == IntPtr.Zero || tmp == IntPtr.Zero || getTransform is null || getChildCount is null
            || getChild is null || getName is null || getComponent is null || getText is null || setText is null)
        {
            Status = "inventory-cell label unavailable: UI/TMP accessors did not resolve";
            return false;
        }

        _getTransform = Marshal.GetDelegateForFunctionPointer<PtrFn>(getTransform.NativePtr);
        _getChildCount = Marshal.GetDelegateForFunctionPointer<IntFn>(getChildCount.NativePtr);
        _getChild = Marshal.GetDelegateForFunctionPointer<ChildFn>(getChild.NativePtr);
        _getName = Marshal.GetDelegateForFunctionPointer<PtrFn>(getName.NativePtr);
        _getComponent = Marshal.GetDelegateForFunctionPointer<GetComponentFn>(getComponent.NativePtr);
        _getText = Marshal.GetDelegateForFunctionPointer<PtrFn>(getText.NativePtr);
        _setText = Marshal.GetDelegateForFunctionPointer<SetTextFn>(setText.NativePtr);
        _tmpType = IL2CPP.il2cpp_type_get_object(IL2CPP.il2cpp_class_get_type(tmp));

        var seenBodies = new HashSet<IntPtr>();
        int hooked = 0;
        foreach (Il2CppMeta.MethodInfo draw in Il2CppMeta.Methods(cellClass))
        {
            if (draw.Name != "Draw" || draw.NativePtr == IntPtr.Zero || !seenBodies.Add(draw.NativePtr)) continue;
            if (!PointerAbiOnly(draw.ParamTypeNames)) continue;

            try
            {
                int index = Originals.Count;
                switch (draw.ParamCount)
                {
                    case 0:
                    {
                        Draw0Fn hook = (self, mi) => Draw0(index, self, mi);
                        Hooks.Add(hook);
                        Originals.Add(null);
                        DetourHandles.Add(ApplyDetour(draw.NativePtr, hook, out Draw0Fn? original));
                        Originals[index] = original;
                        hooked++;
                        break;
                    }
                    case 1:
                    {
                        Draw1Fn hook = (self, a0, mi) => Draw1(index, self, a0, mi);
                        Hooks.Add(hook);
                        Originals.Add(null);
                        DetourHandles.Add(ApplyDetour(draw.NativePtr, hook, out Draw1Fn? original));
                        Originals[index] = original;
                        hooked++;
                        break;
                    }
                    case 2:
                    {
                        Draw2Fn hook = (self, a0, a1, mi) => Draw2(index, self, a0, a1, mi);
                        Hooks.Add(hook);
                        Originals.Add(null);
                        DetourHandles.Add(ApplyDetour(draw.NativePtr, hook, out Draw2Fn? original));
                        Originals[index] = original;
                        hooked++;
                        break;
                    }
                    case 3:
                    {
                        Draw3Fn hook = (self, a0, a1, a2, mi) => Draw3(index, self, a0, a1, a2, mi);
                        Hooks.Add(hook);
                        Originals.Add(null);
                        DetourHandles.Add(ApplyDetour(draw.NativePtr, hook, out Draw3Fn? original));
                        Originals[index] = original;
                        hooked++;
                        break;
                    }
                }
            }
            catch { _errors++; }
        }

        _installed = hooked > 0;
        Status = _installed
            ? $"ready: {hooked} UIInventoryItem.Draw body/bodies; bottom-right Count label"
            : "inventory-cell label unavailable: no pointer-safe UIInventoryItem.Draw overload resolved";
        return _installed;
    }

    private static object ApplyDetour<T>(IntPtr target, T hook, out T? original) where T : Delegate
        => ValeLoot.Detours.Apply(target, hook, out original);

    private static bool PointerAbiOnly(string[] types)
    {
        foreach (string type in types)
        {
            switch (type)
            {
                case "System.Boolean":
                case "System.Byte":
                case "System.SByte":
                case "System.Int16":
                case "System.UInt16":
                case "System.Int32":
                case "System.UInt32":
                case "System.Int64":
                case "System.UInt64":
                case "System.Single":
                case "System.Double":
                    return false;
            }
        }
        return true;
    }

    private static void Draw0(int index, IntPtr self, IntPtr mi)
    {
        (Originals[index] as Draw0Fn)?.Invoke(self, mi);
        Touch(self);
    }

    private static void Draw1(int index, IntPtr self, IntPtr a0, IntPtr mi)
    {
        (Originals[index] as Draw1Fn)?.Invoke(self, a0, mi);
        Touch(self);
    }

    private static void Draw2(int index, IntPtr self, IntPtr a0, IntPtr a1, IntPtr mi)
    {
        (Originals[index] as Draw2Fn)?.Invoke(self, a0, a1, mi);
        Touch(self);
    }

    private static void Draw3(int index, IntPtr self, IntPtr a0, IntPtr a1, IntPtr a2, IntPtr mi)
    {
        (Originals[index] as Draw3Fn)?.Invoke(self, a0, a1, a2, mi);
        Touch(self);
    }

    private static void Touch(IntPtr cell)
    {
        if (cell == IntPtr.Zero) return;
        RecentlyDrawn[cell] = DateTime.UtcNow;
        Apply(cell);
    }

    public static bool RefreshVisible()
    {
        if (!_installed) return false;
        DateTime cutoff = DateTime.UtcNow.AddSeconds(-15);
        Sweep.Clear();
        bool any = false;
        foreach ((IntPtr cell, DateTime last) in RecentlyDrawn)
        {
            if (last < cutoff) { Sweep.Add(cell); continue; }
            Apply(cell);
            any = true;
        }
        foreach (IntPtr cell in Sweep)
        {
            RecentlyDrawn.Remove(cell);
            CountTextByCell.Remove(cell);
            BaseCountByCell.Remove(cell);
        }
        return any;
    }

    private static void Apply(IntPtr cell)
    {
        if (_setText is null || _getText is null) return;
        try
        {
            IntPtr text = ResolveCountText(cell);
            if (text == IntPtr.Zero) return;

            string current = Il2CppMeta.ReadString(_getText(text, IntPtr.Zero)) ?? "";
            string baseText;
            if (current.Contains(Marker, StringComparison.Ordinal)
                && BaseCountByCell.TryGetValue(cell, out string? remembered))
                baseText = remembered;
            else
            {
                baseText = current;
                BaseCountByCell[cell] = baseText;
            }

            string? label = ValeLootInventoryCellApi.BuildLabel(cell);
            string desired = baseText;
            if (!string.IsNullOrWhiteSpace(label))
            {
                string price = $"{Marker}<size=72%><b>{label.Trim()}</b></color></size>";
                desired = string.IsNullOrWhiteSpace(baseText) ? price : baseText + "\n" + price;
            }

            if (!string.Equals(current, desired, StringComparison.Ordinal))
                _setText(text, IL2CPP.ManagedStringToIl2Cpp(desired), IntPtr.Zero);

            if (!_reportedFirstLabel && !string.IsNullOrWhiteSpace(label))
            {
                _reportedFirstLabel = true;
                Status += "; first external label drawn";
            }
        }
        catch { _errors++; }
    }

    private static IntPtr ResolveCountText(IntPtr cell)
    {
        if (CountTextByCell.TryGetValue(cell, out IntPtr cached) && cached != IntPtr.Zero) return cached;
        IntPtr found = FindCountText(cell, 0, 0);
        if (found != IntPtr.Zero) CountTextByCell[cell] = found;
        return found;
    }

    private static IntPtr FindCountText(IntPtr owner, int depth, int visited)
    {
        if (owner == IntPtr.Zero || depth > 4 || visited > 48 || _getTransform is null
            || _getChildCount is null || _getChild is null || _getName is null || _getComponent is null)
            return IntPtr.Zero;

        IntPtr transform = _getTransform(owner, IntPtr.Zero);
        if (transform == IntPtr.Zero) return IntPtr.Zero;
        int count = Math.Min(_getChildCount(transform, IntPtr.Zero), 24);
        for (int i = 0; i < count; i++)
        {
            IntPtr child = _getChild(transform, i, IntPtr.Zero);
            if (child == IntPtr.Zero) continue;

            string name = Il2CppMeta.ReadString(_getName(child, IntPtr.Zero)) ?? "";
            if (IsCountName(name))
            {
                IntPtr text = _getComponent(child, _tmpType, IntPtr.Zero);
                if (text != IntPtr.Zero) return text;
            }

            IntPtr nested = FindCountText(child, depth + 1, visited + i + 1);
            if (nested != IntPtr.Zero) return nested;
        }
        return IntPtr.Zero;
    }

    private static bool IsCountName(string name)
        => name.Contains("count", StringComparison.OrdinalIgnoreCase)
        || name.Contains("amount", StringComparison.OrdinalIgnoreCase)
        || name.Contains("quantity", StringComparison.OrdinalIgnoreCase);
}
