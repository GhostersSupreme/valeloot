using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

namespace ValeLoot;

/// <summary>
/// Injects ValeLoot's rule explanation and optional cooperative-provider details into the game's own
/// inventory tooltip write. ValeLoot remains the sole owner of the native hover/TMP hooks.
/// </summary>
internal static class TooltipInject
{
    private delegate void PointerFn(IntPtr self, IntPtr eventData, IntPtr methodInfo);
    private delegate IntPtr GetComponentFn(IntPtr self, IntPtr type, IntPtr methodInfo);
    private delegate void SetTextFn(IntPtr self, IntPtr value, IntPtr methodInfo);

    private const string Marker = "\u200b";
    private const int MinTooltipChars = 400;
    private const int MinExternalTooltipChars = 60;

    private static object? _enterDetour;
    private static PointerFn? _enterHook;
    private static PointerFn? _enterOriginal;
    private static object? _exitDetour;
    private static PointerFn? _exitHook;
    private static PointerFn? _exitOriginal;
    private static object? _setTextDetour;
    private static SetTextFn? _setTextHook;
    private static SetTextFn? _setTextOriginal;
    private static GetComponentFn? _getComponent;
    private static IntPtr _cellType;
    private static int _dataFieldOffset = -1;
    private static Action<string>? _log;

    private static volatile string? _pending;
    private static volatile int _pendingMinChars = MinTooltipChars;
    private static bool _writing;

    // Pointer-enter capture state. SpiritVale can write several unrelated TMP_Text objects during one hover.
    // Consuming _pending on the first sufficiently long write works on some panels but can target the wrong
    // text object on the character inventory screen. While OnPointerEnter is running, record the longest
    // eligible write and append only after the game's pointer-enter has completely returned.
    private static bool _capturingEnter;
    private static IntPtr _candidateText;
    private static string? _candidateBaseText;

    // Cooperative async refresh state. The base text never contains ValeLoot/provider additions, so a
    // later refresh REPLACES "Checking..." instead of appending a second market block underneath it.
    //
    // Important: SpiritVale may emit OnPointerExit for the inventory cell while leaving the tooltip visible
    // (for example while the tooltip itself becomes the top UI raycast target). Therefore this state is
    // intentionally retained across pointer-exit and replaced only by the next OnPointerEnter. Refreshing a
    // hidden old TMP object is harmless; clearing it too early makes asynchronous providers unreliable.
    private static IntPtr _currentHandler;
    private static IntPtr _currentText;
    private static string? _currentBaseText;
    private static bool _refreshMissReported;

    public static bool Installed { get; private set; }
    public static bool Enabled = true;
    public static long Hovers;
    public static long Injected;
    public static long Refreshed;
    public static long Misses;
    public static long Errors;

    public static bool Install(Action<string> log)
    {
        _log = log;

        IntPtr handler = Il2CppMeta.FindClass("", "HoverInfoHandler", HookCensus.GameAssemblies);
        IntPtr cell = Il2CppMeta.FindClass("", "UIInventoryItem", HookCensus.GameAssemblies);
        IntPtr text = Il2CppMeta.FindClass("TMPro", "TMP_Text", "Unity.TextMeshPro.dll", "TextMeshPro.dll");
        IntPtr component = Il2CppMeta.FindClass("UnityEngine", "Component", "UnityEngine.CoreModule.dll");

        Il2CppMeta.MethodInfo? onEnter = Il2CppMeta.FindMethodRuntime(handler, "OnPointerEnter", 1);
        Il2CppMeta.MethodInfo? onExit = Il2CppMeta.FindMethodRuntime(handler, "OnPointerExit", 1);
        Il2CppMeta.MethodInfo? getComponent = Il2CppMeta.FindOverload(component, "GetComponent", "System.Type");
        Il2CppMeta.MethodInfo? setText = Il2CppMeta.FindOverload(text, "set_text", "System.String");

        if (onEnter is null || onEnter.NativePtr == IntPtr.Zero || getComponent is null
            || setText is null || text == IntPtr.Zero || cell == IntPtr.Zero)
        {
            log("tooltip inject NOT ready: hover/component/TMP_Text path unresolved");
            return false;
        }

        _getComponent = Marshal.GetDelegateForFunctionPointer<GetComponentFn>(getComponent.NativePtr);
        _cellType = IL2CPP.il2cpp_type_get_object(IL2CPP.il2cpp_class_get_type(cell));
        _dataFieldOffset = Il2CppMeta.FieldOffset(cell, "Data");
        if (_dataFieldOffset < 0)
        {
            log($"tooltip inject NOT ready: Data at 0x{_dataFieldOffset:x}");
            return false;
        }

        try
        {
            _enterHook = EnterDetour;
            _enterDetour = Detours.Apply(onEnter.NativePtr, _enterHook, out PointerFn? enterOriginal);
            _enterOriginal = enterOriginal;

            if (onExit is not null && onExit.NativePtr != IntPtr.Zero)
            {
                _exitHook = ExitDetour;
                _exitDetour = Detours.Apply(onExit.NativePtr, _exitHook, out PointerFn? exitOriginal);
                _exitOriginal = exitOriginal;
            }

            _setTextHook = SetTextDetour;
            _setTextDetour = Detours.Apply(setText.NativePtr, _setTextHook, out SetTextFn? setOriginal);
            _setTextOriginal = setOriginal;

            Installed = true;
            log($"tooltip inject ready (OnPointerEnter + TMP_Text.set_text; Data 0x{_dataFieldOffset:x}, "
              + $"floors {MinTooltipChars}/{MinExternalTooltipChars} chars, longest-write targeting, "
              + "async refresh retains last valid target across pointer-exit)");
        }
        catch (Exception e)
        {
            log($"tooltip inject could not install — {e.Message}");
            Uninstall();
        }
        return Installed;
    }

    public static void Uninstall()
    {
        Detours.Undo(ref _setTextDetour);
        Detours.Undo(ref _exitDetour);
        Detours.Undo(ref _enterDetour);
        Installed = false;
        _pending = null;
        _capturingEnter = false;
        _candidateText = IntPtr.Zero;
        _candidateBaseText = null;
        _currentHandler = IntPtr.Zero;
        _currentText = IntPtr.Zero;
        _currentBaseText = null;
    }

    private static void EnterDetour(IntPtr self, IntPtr eventData, IntPtr methodInfo)
    {
        _currentHandler = self;
        _currentText = IntPtr.Zero;
        _currentBaseText = null;
        _candidateText = IntPtr.Zero;
        _candidateBaseText = null;
        _refreshMissReported = false;

        if (Enabled && self != IntPtr.Zero)
        {
            try { BuildPending(self, countHover: true); }
            catch { Errors++; _pending = null; }
        }

        _capturingEnter = true;
        try
        {
            _enterOriginal?.Invoke(self, eventData, methodInfo);
        }
        finally
        {
            _capturingEnter = false;
        }

        // The AH response can occasionally complete during the game's own pointer-enter. Rebuild here so
        // the text we commit is the newest provider state (live result if ready, otherwise Checking...).
        if (Enabled && self != IntPtr.Zero && _candidateText != IntPtr.Zero)
        {
            try { BuildPending(self, countHover: false); }
            catch { Errors++; _pending = null; }
            CommitCandidate();
        }
    }

    private static void ExitDetour(IntPtr self, IntPtr eventData, IntPtr methodInfo)
    {
        // Do NOT clear _currentHandler/_currentText/_currentBaseText here. SpiritVale can raise a pointer-exit
        // as part of normal tooltip presentation while the tooltip remains visible. The next pointer-enter
        // atomically replaces the retained target, which also prevents a late provider response from writing
        // an old item's details onto a newly hovered item.
        _exitOriginal?.Invoke(self, eventData, methodInfo);
    }

    private static void BuildPending(IntPtr handler, bool countHover)
    {
        if (countHover) Hovers++;
        IntPtr cell = _getComponent!(handler, _cellType, IntPtr.Zero);
        if (cell == IntPtr.Zero)
        {
            Misses++;
            _pending = null;
            return;
        }

        string? ownDetails = null;
        if (InventoryPaint.TryGetMark(cell, out InventoryPaint.Mark mark) && mark.Level != 0)
        {
            ownDetails = mark.Label.Length > 0
                ? $"{Marker}<color={mark.Hex}><b>{mark.Label}</b> — {mark.Rule}</color>"
                : $"{Marker}<color={mark.Hex}>{mark.Rule}</color>";
        }

        string? externalDetails = null;
        if (!InventoryPaint.IsPresentationOnly(cell))
        {
            IntPtr data = Marshal.ReadIntPtr(cell, _dataFieldOffset);
            if (data != IntPtr.Zero) externalDetails = ValeLootTooltipApi.BuildDetails(data);
        }

        _pending = ownDetails is null
            ? externalDetails
            : externalDetails is null ? ownDetails : ownDetails + "\n" + externalDetails;
        _pendingMinChars = externalDetails is null ? MinTooltipChars : MinExternalTooltipChars;
        if (_pending is null) Misses++;
    }

    private static void SetTextDetour(IntPtr self, IntPtr value, IntPtr methodInfo)
    {
        if (!Enabled || _writing)
        {
            _setTextOriginal?.Invoke(self, value, methodInfo);
            return;
        }

        string? pending = _pending;
        if (pending is null)
        {
            _setTextOriginal?.Invoke(self, value, methodInfo);
            return;
        }

        string incoming = Il2CppMeta.ReadString(value) ?? "";
        if (incoming.Length < _pendingMinChars || incoming.Contains(Marker, StringComparison.Ordinal))
        {
            _setTextOriginal?.Invoke(self, value, methodInfo);
            return;
        }

        if (_capturingEnter)
        {
            // Keep the game's write untouched for now. We choose among every candidate generated by this
            // hover and append once after OnPointerEnter returns.
            _setTextOriginal?.Invoke(self, value, methodInfo);
            if (_candidateBaseText is null || incoming.Length > _candidateBaseText.Length)
            {
                _candidateText = self;
                _candidateBaseText = incoming;
            }
            return;
        }

        // Compatibility fallback for a client that performs the tooltip write after OnPointerEnter returns.
        CommitDirect(self, incoming, pending, methodInfo);
    }

    private static void CommitCandidate()
    {
        string? pending = _pending;
        if (pending is null || _candidateText == IntPtr.Zero || _candidateBaseText is null)
            return;

        IntPtr text = _candidateText;
        string baseText = _candidateBaseText;
        _candidateText = IntPtr.Zero;
        _candidateBaseText = null;
        CommitDirect(text, baseText, pending, IntPtr.Zero);
    }

    private static void CommitDirect(IntPtr self, string incoming, string pending, IntPtr methodInfo)
    {
        if (_setTextOriginal is null) return;
        _pending = null;
        _writing = true;
        try
        {
            _currentText = self;
            _currentBaseText = incoming;
            _setTextOriginal(self, IL2CPP.ManagedStringToIl2Cpp(incoming + "\n" + pending), methodInfo);
            Injected++;
        }
        catch
        {
            Errors++;
            _setTextOriginal(self, IL2CPP.ManagedStringToIl2Cpp(incoming), methodInfo);
        }
        finally { _writing = false; }
    }

    /// <summary>
    /// Rebuild the cooperative details for the latest inventory tooltip target and replace the body in-place.
    /// The target survives pointer-exit because SpiritVale can emit exit while the tooltip remains visible.
    /// </summary>
    internal static bool RefreshCurrentExternal()
    {
        if (!Installed || !Enabled || _writing || _currentHandler == IntPtr.Zero
            || _currentText == IntPtr.Zero || _currentBaseText is null || _setTextOriginal is null)
        {
            if (!_refreshMissReported)
            {
                _refreshMissReported = true;
                _log?.Invoke($"tooltip async refresh missed current body: handler {_currentHandler != IntPtr.Zero}, "
                           + $"text {_currentText != IntPtr.Zero}, base {_currentBaseText is not null}, writing {_writing}");
            }
            return false;
        }

        try
        {
            BuildPending(_currentHandler, countHover: false);
            string? pending = _pending;
            _pending = null;
            if (pending is null) return false;

            _writing = true;
            _setTextOriginal(_currentText,
                IL2CPP.ManagedStringToIl2Cpp(_currentBaseText + "\n" + pending), IntPtr.Zero);
            Refreshed++;
            return true;
        }
        catch
        {
            Errors++;
            return false;
        }
        finally { _writing = false; }
    }

    public static string Status()
        => $"tooltip inject: installed {Installed}, enabled {Enabled}, hovers {Hovers}, "
         + $"injected {Injected}, refreshed {Refreshed}, misses {Misses}, errors {Errors}";
}

/// <summary>
/// Cooperative item-tooltip extension point for soft-dependent plugins.
/// Providers run on the UI thread while the hovered cell's item data is valid.
/// </summary>
public static class ValeLootTooltipApi
{
    private static readonly object Gate = new();
    private static Func<IntPtr, string?>? _provider;

    public static bool Register(Func<IntPtr, string?> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (Gate)
        {
            if (_provider is not null && !ReferenceEquals(_provider, provider)) return false;
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
    }

    public static bool RefreshCurrent() => TooltipInject.RefreshCurrentExternal();

    internal static string? BuildDetails(IntPtr itemData)
    {
        Func<IntPtr, string?>? provider;
        lock (Gate) provider = _provider;
        if (provider is null) return null;
        try { return provider(itemData); }
        catch { return null; }
    }
}
