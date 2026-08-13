using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Il2CppInterop.Runtime;

namespace ValeLoot;

/// <summary>
/// Highlighting the player's own inventory cells — the whole point of doing this in-process.
///
/// An out-of-process screen-space marker grid — the obvious alternative — could only ever be an
/// approximation: it draws a rectangle of cells over where the game's inventory is BELIEVED to be,
/// which costs a per-resolution calibration, a manual row nudge every time the panel scrolls, page
/// arithmetic in the game's own paging unit, and a tab-row click watcher to guess which tab is open.
/// Scroll, sort or search in game and every mark is describing the wrong slot. In here there is
/// nothing to align: we mark the cell the game itself bound to the item, so scrolling, sorting,
/// searching and paging are free and correct by construction.
///
/// ## What it paints, and why that surface
///
/// `UIInventoryItem` carries a `Highlight` CanvasGroup (0x78) and a `SetHighlight(bool)` that fades it
/// in over 0.1s. The disassembly says the game drives that overlay for SELECTION — every caller is a
/// picker (`UICrafter`, `UIRefine`, `UIEssence`, `UIGemRefine`, `UIGemRemoval`, `UICardRemoval`,
/// `UIWaypoint`) and the tab-level `UIInventoryTab&lt;T&gt;.SetHighlight` is called from exactly two
/// places, both in `UIWardrobe`. So in the equipment, artifact, grimoire, card, gem, junk and
/// consumable tabs that overlay is UNUSED: it is already sized and positioned per cell, it fades for
/// free, and driving it there cannot fight the game. The wardrobe tab is deliberately skipped for that
/// reason, by class name.
///
/// ## Where the item comes from
///
/// `UIInventoryTab&lt;T&gt;` holds `InventoryItemsUID`, a live `Dictionary&lt;string, UIInventoryItem&gt;`
/// from item uid to the cell currently showing it. That map is maintained by the game, so this file
/// walks it rather than trying to work out which cell is which. Most tabs expose item data through the
/// cell's `Data` field; Grimoires do not, so their facts come from the current cell text and dictionary
/// key instead. Either path is judged by the player's own rules before the frame ends.
///
/// ## The pooling hazard, from the disassembly rather than from a guess
///
/// Cells are pooled (`PoolingUtils.Spawn/Despawn&lt;UIInventoryItem&gt;`), and `UIInventoryItem.Clear`
/// resets the sprite, name, description, type, weight, count, favourite and lock flags — but it does
/// NOT touch `Highlight` and does not null `Data`. A mark written once would therefore ride a recycled
/// cell onto an unrelated item. The defence is that a paint pass sets EVERY cell in the dictionary
/// explicitly, on or off, rather than only touching matches: absence of a verdict is a value we write,
/// not a case we skip.
///
/// Painting is driven by the tab's own `Redraw`, so ordinary border and solid-fill rules run only
/// when the panel changes. Hue rotation uses the existing main-thread frame hook, throttled to one
/// update per four frames and bounded by the number of visible `holo` cells.
/// </summary>
internal static class InventoryPaint
{
    /**
     * The two repaint entry points, with the signatures the disassembly gives them.
     *
     * `RenderPage()` repaints the whole visible page — opening the panel, scrolling, paging, filtering.
     * `Redraw(string uid)` repaints ONE cell: it looks the uid up in both dictionaries and calls
     * `UIInventoryItem.Draw`, which is how an inventory mutation lands. Getting these arities wrong is
     * not a missing feature but a corrupted call frame, so they are bound exactly as declared —
     * `Redraw/1` was first bound as `Redraw/0`, which simply failed to resolve (the lucky outcome).
     */
    private delegate void RenderPageFn(IntPtr self, IntPtr methodInfo);
    private delegate void RedrawFn(IntPtr self, IntPtr uid, IntPtr methodInfo);

    /**
     * Every engine call from here takes only pointers, ints and floats.
     *
     * The first attempt called `Graphic.set_color(Color)` — a 16-byte struct by value, which is an ABI
     * question the process gets exactly one chance to answer, and it also resolved the wrong overload of
     * `GetComponentsInChildren` (see `Il2CppMeta.FindOverload`). Between them they took the game down
     * with an `AccessViolationException` inside the hook the first time a bag was opened. Writing the
     * `m_Color` FIELD and calling the no-argument `SetAllDirty()` does the same work with nothing to get
     * wrong. Keep it that way: a highlight is not worth a crash.
     */
    private delegate void SetAlphaFn(IntPtr self, float alpha, IntPtr methodInfo);
    private delegate void VoidFn(IntPtr self, IntPtr methodInfo);
    private delegate IntPtr GetComponentFn(IntPtr self, IntPtr type, IntPtr methodInfo);
    private delegate IntPtr TransformFn(IntPtr self, IntPtr methodInfo);
    private delegate int CountFn(IntPtr self, IntPtr methodInfo);
    private delegate IntPtr GetChildFn(IntPtr self, int index, IntPtr methodInfo);
    private delegate IntPtr StringFn(IntPtr self, IntPtr methodInfo);
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool ObjectAliveFn(IntPtr self, IntPtr methodInfo);

    /**
     * One item's verdict: how loudly, in what colour, which rule said so, and what it sounds like.
     *
     * `Level == 0` means "not marked". `Hex` is kept alongside the parsed floats because the tooltip
     * writes TMP rich text, which wants `#rrggbb` back as a string; re-formatting the floats would be a
     * second chance to disagree with the cell about a colour.
     */
    internal readonly struct Mark
    {
        public readonly int Level;
        public readonly int Background;
        public readonly bool Border;
        public readonly float R;
        public readonly float G;
        public readonly float B;
        public readonly string Hex;
        public readonly string Label;
        public readonly string Rule;
        /// <summary>Sound name to play if this uid is arriving, or null for silence.</summary>
        public readonly string? Sound;

        public Mark(
            int level,
            int background,
            bool border,
            (float R, float G, float B) rgb,
            string hex,
            string label,
            string rule,
            string? sound)
        {
            Level = level;
            Background = background;
            Border = border;
            R = rgb.R;
            G = rgb.G;
            B = rgb.B;
            Hex = hex;
            Label = label;
            Rule = rule;
            Sound = sound;
        }
    }

    /// <summary>The verdict drawn on a cell, or null. Read by the tooltip injector on the main thread.</summary>
    public static bool TryGetMark(IntPtr cell, out Mark mark) => _marks.TryGetValue(cell, out mark);

    /// <summary>True when the current cell must not expose its pooled Data pointer to tooltip providers.</summary>
    public static bool IsPresentationOnly(IntPtr cell) => _presentationOnlyCells.Contains(cell);

    /**
     * How visible each level is.
     *
     * Deliberately far apart rather than evenly spaced: the point of three levels is that a glow is
     * unmistakable and a dot is background, so the gap between them matters more than the linearity.
     * `UIInventoryItem.SetHighlight` fades to 1.0, which is why every match used to look the same.
     */
    private const float DotAlpha = 0.28f;
    private const float MarkAlpha = 0.62f;
    private const float GlowAlpha = 1f;

    /// <summary>Offset of `UIInventoryItem.Highlight` (CanvasGroup), resolved by name at install.</summary>
    private static int _highlightFieldOffset = -1;

    /// <summary>
    /// Tabs whose Redraw is hooked. `Cosmetics` is absent on purpose: `UIWardrobe` drives the same
    /// highlight overlay there for its preview selection, and two writers would fight.
    /// </summary>
    private static readonly string[] TabClasses =
    {
        "UIInventoryTab_Equips",
        "UIInventoryTab_Artifacts",
        "UIInventoryTab_Grimoires",
        "UIInventoryTab_Cards",
        "UIInventoryTab_Gems",
        "UIInventoryTab_Junks",
        "UIInventoryTab_Consumables",
    };

    /**
     * cell -> its verdict, rebuilt by every paint pass.
     *
     * Keying the hover note by cell rather than by `Data.UID` is required for Grimoires: their pooled
     * Data pointer belongs to an older item. Everything that writes or reads this table runs on Unity's
     * main thread, so it needs no lock or reference swap.
     */
    private static readonly Dictionary<IntPtr, Mark> _marks = new();

    /// <summary>Current cells whose game Draw path does not own UIInventoryItem.Data.</summary>
    private static readonly HashSet<IntPtr> _presentationOnlyCells = new();

    /// <summary>Reused per cell. One buffer for the whole session — see LootFilter.ItemFacts.</summary>
    private static readonly LootFilter.ItemFacts _facts = new();

    private static readonly List<object> _detours = new();
    private static readonly List<RenderPageFn> _pageHooks = new();
    private static readonly List<RenderPageFn?> _pageOriginals = new();
    private static readonly List<RedrawFn> _itemHooks = new();
    private static readonly List<RedrawFn?> _itemOriginals = new();
    private static SetAlphaFn? _setAlpha;
    private static GetComponentFn? _getComponent;
    private static TransformFn? _getTransform;
    private static CountFn? _getChildCount;
    private static GetChildFn? _getChild;
    private static VoidFn? _setAllDirty;
    private static ObjectAliveFn? _objectAlive;
    private static StringFn? _getName;
    private static bool _backgroundReported;
    /// <summary>A `System.Type` for `UnityEngine.UI.Graphic`, for the non-generic component search.</summary>
    private static IntPtr _graphicType;
    /// <summary>Offset of `Graphic.m_Color`, written directly instead of through `set_color(Color)`.</summary>
    private static int _colorFieldOffset = -1;
    /// <summary>A `System.Type` for the root `UnityEngine.UI.Image` that already draws each card.</summary>
    private static IntPtr _imageType;
    private static int _animationFrame;

    private sealed class FillState
    {
        public IntPtr Handle;
        public IntPtr Cell;
        public float OriginalR;
        public float OriginalG;
        public float OriginalB;
        public float OriginalA;
        public float Strength;
        public int Background;
        public float Hue;
        public float Saturation;
        public float Value;
    }

    /// Root card Images currently recoloured by a full-background rule. A GC handle keeps the
    /// il2cpp wrapper alive while a hue tick owns it; dead Unity objects are swept before any write.
    /// </summary>
    private static readonly Dictionary<IntPtr, FillState> _fills = new();
    private static readonly Dictionary<IntPtr, IntPtr> _backgroundByCell = new();
    private static readonly List<IntPtr> _deadFills = new();
    /// <summary>Logged once, as proof the tint reached a real graphic rather than merely resolving.</summary>
    private static bool _tintReported;

    /**
     * Tuning that a player can reach without a rebuild, read from `BepInEx/config/com.savi.valeloot.cfg`.
     *
     * Five build/deploy/relaunch/login cycles went into getting this tint onto the right object, and the
     * two values that mattered during them are the two that are configurable now: whether to tint at all,
     * and how far below the overlay object to walk. A player on a future game build whose cell layout has
     * moved can turn the tint off, or widen the walk, without waiting for a release.
     */
    public static bool TintEnabled = true;
    /// <summary>How many levels below the overlay object to tint. 0 = the object itself only.</summary>
    public static int TintDepth = 2;
    private static Action<string>? _log;
    private static int _uidFieldOffset = -1;

    public static bool Installed { get; private set; }
    public static long Passes;
    public static long CellsLit;
    public static long CellsCleared;
    public static long Errors;
    public static int MarkCount => _marks.Count;

    /// <summary>Drop everything remembered about what is drawn, so the next pass decides afresh.</summary>
    public static void Forget()
    {
        _marks.Clear();
        _presentationOnlyCells.Clear();
    }

    /// <summary>Apply configured tuning, and say what it is — a silent knob is an unfalsifiable one.</summary>
    public static void Configure(bool tint, int depth)
    {
        TintEnabled = tint;
        TintDepth = depth < 0 ? 0 : depth > 4 ? 4 : depth;
        _tintReported = false;   // report again, so a change proves itself in the log
        _log?.Invoke($"inventory paint: tint {(TintEnabled ? "on" : "off")}, depth {TintDepth}");
    }

    public static bool Install(Action<string> log)
    {
        _log = log;

        /**
         * The cell field the highlight lives on, by NAME.
         *
         * The dump says 0x78 today. Hardcoding that would be the one assumption in this file guaranteed to
         * rot: field offsets move whenever a serialized field is added above them, which is an ordinary
         * content patch, and a stale offset reads a neighbouring pointer as a CanvasGroup.
         */
        IntPtr cellClass = Il2CppMeta.FindClass("", "UIInventoryItem", HookCensus.GameAssemblies);
        _highlightFieldOffset = Il2CppMeta.FieldOffset(cellClass, "Highlight");
        // The background is not this serialized Container field. A runtime hierarchy probe proved
        // the rounded panel is the immediate child named `Background-Container`.
        if (_highlightFieldOffset < 0)
        {
            log("inventory paint NOT ready: UIInventoryItem.Highlight field did not resolve");
            return false;
        }

        /**
         * Engine setters, resolved by name like everything else here.
         *
         * `CanvasGroup.alpha` carries the LEVEL and is the one hard requirement: without it there is no
         * highlight at all. The tint path (`Component.GetComponent(Type)` + `Graphic.color`) is optional —
         * if the overlay turns out to have no Image on it, intensity alone still distinguishes a glow from
         * a dot, and saying so beats refusing to install.
         *
         * `FindClass(ns, name, assemblies)` — the argument order this file already got wrong once. Unity
         * types live in their module assemblies, not Assembly-CSharp.
         */
        IntPtr canvasGroup = Il2CppMeta.FindClass("UnityEngine", "CanvasGroup", "UnityEngine.UIModule.dll", "UnityEngine.CoreModule.dll");
        Il2CppMeta.MethodInfo? setAlpha = Il2CppMeta.FindMethodRuntime(canvasGroup, "set_alpha", 1);
        if (setAlpha is null || setAlpha.NativePtr == IntPtr.Zero)
        {
            log("inventory paint NOT ready: CanvasGroup.set_alpha did not resolve");
            return false;
        }
        _setAlpha = Marshal.GetDelegateForFunctionPointer<SetAlphaFn>(setAlpha.NativePtr);

        /**
         * The tint path, resolved by exact signature.
         *
         * `Graphic`, not `Image`: the overlay may draw with any Graphic subclass, and `m_Color` is declared
         * on the base, so searching for the base cannot miss a shape. Every lookup names its parameter
         * types — the crash that preceded this code came from trusting arity alone.
         *
         * The whole path is OPTIONAL. If any piece is missing, the level still shows as intensity, which is
         * the difference between a degraded highlight and a dead one.
         */
        IntPtr component = Il2CppMeta.FindClass("UnityEngine", "Component", "UnityEngine.CoreModule.dll");
        IntPtr unityObject = Il2CppMeta.FindClass("UnityEngine", "Object", "UnityEngine.CoreModule.dll");
        IntPtr transform = Il2CppMeta.FindClass("UnityEngine", "Transform", "UnityEngine.CoreModule.dll");
        IntPtr graphic = Il2CppMeta.FindClass("UnityEngine.UI", "Graphic", "UnityEngine.UI.dll", "Unity.ugui.dll");
        IntPtr image = Il2CppMeta.FindClass("UnityEngine.UI", "Image", "UnityEngine.UI.dll", "Unity.ugui.dll");
        Il2CppMeta.MethodInfo? getComponent = Il2CppMeta.FindOverload(component, "GetComponent", "System.Type");
        Il2CppMeta.MethodInfo? getTransform = Il2CppMeta.FindOverload(component, "get_transform");
        Il2CppMeta.MethodInfo? childCount = Il2CppMeta.FindOverload(transform, "get_childCount");
        Il2CppMeta.MethodInfo? getChild = Il2CppMeta.FindOverload(transform, "GetChild", "System.Int32");
        Il2CppMeta.MethodInfo? setAllDirty = Il2CppMeta.FindOverload(graphic, "SetAllDirty");
        Il2CppMeta.MethodInfo? objectAlive = Il2CppMeta.FindOverload(unityObject, "op_Implicit", "UnityEngine.Object");
        Il2CppMeta.MethodInfo? getName = Il2CppMeta.FindOverload(unityObject, "get_name");
        _colorFieldOffset = Il2CppMeta.FieldOffsetUp(graphic, "m_Color");

        bool tintReady = getComponent is not null && getTransform is not null && childCount is not null
            && getChild is not null && setAllDirty is not null && _colorFieldOffset >= 0 && graphic != IntPtr.Zero;
        if (tintReady)
        {
            _getComponent = Marshal.GetDelegateForFunctionPointer<GetComponentFn>(getComponent!.NativePtr);
            _getTransform = Marshal.GetDelegateForFunctionPointer<TransformFn>(getTransform!.NativePtr);
            _getChildCount = Marshal.GetDelegateForFunctionPointer<CountFn>(childCount!.NativePtr);
            _getChild = Marshal.GetDelegateForFunctionPointer<GetChildFn>(getChild!.NativePtr);
            _setAllDirty = Marshal.GetDelegateForFunctionPointer<VoidFn>(setAllDirty!.NativePtr);
            _objectAlive = objectAlive is null
                ? null
                : Marshal.GetDelegateForFunctionPointer<ObjectAliveFn>(objectAlive.NativePtr);
            // The non-generic component search wants managed System.Type instances, not class pointers.
            _graphicType = IL2CPP.il2cpp_type_get_object(IL2CPP.il2cpp_class_get_type(graphic));
            bool fillReady = image != IntPtr.Zero && _objectAlive is not null && getName is not null;
            _imageType = fillReady
                ? IL2CPP.il2cpp_type_get_object(IL2CPP.il2cpp_class_get_type(image))
                : IntPtr.Zero;
            _getName = fillReady
                ? Marshal.GetDelegateForFunctionPointer<StringFn>(getName!.NativePtr)
                : null;
            log($"inventory paint: tint path ready (Graphic.m_Color at 0x{_colorFieldOffset:x}, depth {TintDepth}); "
              + (fillReady ? "Background-Container child targeting ready" : "full background unavailable"));
        }
        else
        {
            log("inventory paint: no tint path — levels will show as intensity only");
        }

        /**
         * Both repaint paths are hooked, because they answer different questions:
         * `RenderPage()` is "the panel changed" (opened, scrolled, paged, filtered) and `Redraw(uid)` is
         * "one item changed" (a pickup, a refine, a favourite). Hooking only the first leaves a freshly
         * looted item unpainted until the player scrolls; hooking only the second never paints the panel
         * the player just opened.
         *
         * One native body may back several instantiations of the generic base, so identical targets are
         * detoured once — detouring the same address twice would chain our own hook to itself.
         */
        var seen = new HashSet<IntPtr>();
        int pages = 0;
        int items = 0;
        foreach (string tabClass in TabClasses)
        {
            IntPtr klass = Il2CppMeta.FindClass("", tabClass, HookCensus.GameAssemblies);
            if (klass == IntPtr.Zero) { log($"inventory paint: class {tabClass} not found"); continue; }

            Il2CppMeta.MethodInfo? renderPage = Il2CppMeta.FindMethodRuntime(klass, "RenderPage", 0);
            if (renderPage is not null && renderPage.NativePtr != IntPtr.Zero && seen.Add(renderPage.NativePtr))
            {
                int index = _pageHooks.Count;
                RenderPageFn hook = (self, methodInfo) => RenderPageDetour(index, self, methodInfo);
                _pageHooks.Add(hook);
                _pageOriginals.Add(null);
                try
                {
                    _detours.Add(Detours.Apply(renderPage.NativePtr, hook, out RenderPageFn? original));
                    _pageOriginals[index] = original;
                    pages++;
                }
                catch (Exception e) { log($"inventory paint could not hook {tabClass}.RenderPage — {e.Message}"); }
            }

            Il2CppMeta.MethodInfo? redraw = Il2CppMeta.FindMethodRuntime(klass, "Redraw", 1);
            if (redraw is not null && redraw.NativePtr != IntPtr.Zero && seen.Add(redraw.NativePtr))
            {
                int index = _itemHooks.Count;
                RedrawFn hook = (self, uid, methodInfo) => RedrawDetour(index, self, uid, methodInfo);
                _itemHooks.Add(hook);
                _itemOriginals.Add(null);
                try
                {
                    _detours.Add(Detours.Apply(redraw.NativePtr, hook, out RedrawFn? original));
                    _itemOriginals[index] = original;
                    items++;
                }
                catch (Exception e) { log($"inventory paint could not hook {tabClass}.Redraw — {e.Message}"); }
            }

            if (renderPage is null && redraw is null)
            {
                /**
                 * Say what was actually there.
                 *
                 * A bare "did not resolve" sent this hunt through three game restarts guessing at argument
                 * order, generic initialisation and arity — the answer was `Redraw/1`, visible the moment
                 * the declared methods were printed. The hierarchy dump turns the next failure, a rename
                 * on patch day included, into one line of evidence.
                 */
                log($"inventory paint: no repaint method on {tabClass}; hierarchy follows");
                for (IntPtr current = klass; current != IntPtr.Zero; current = IL2CPP.il2cpp_class_get_parent(current))
                {
                    var names = new List<string>();
                    foreach (Il2CppMeta.MethodInfo m in Il2CppMeta.Methods(current)) names.Add($"{m.Name}/{m.ParamCount}");
                    log($"  {Il2CppMeta.ClassName(current)}: {(names.Count == 0 ? "(no declared methods)" : string.Join(", ", names))}");
                }
            }
        }

        Installed = pages + items > 0;
        log(Installed
            ? $"inventory paint ready: {pages} RenderPage + {items} Redraw body/bodies hooked"
            : "inventory paint NOT ready: no repaint method resolved on any tab");
        return Installed;
    }

    public static void Uninstall()
    {
        RestoreAllFills();
        for (int i = 0; i < _detours.Count; i++)
        {
            object? handle = _detours[i];
            Detours.Undo(ref handle);
        }
        _detours.Clear();
        _pageHooks.Clear();
        _pageOriginals.Clear();
        _itemHooks.Clear();
        _itemOriginals.Clear();
        Installed = false;
    }

    /**
     * Paint AFTER the game has finished its own repaint.
     *
     * Order matters: the original rebinds cells to items and repopulates `InventoryItemsUID`, so painting
     * first would mark the panel's previous contents.
     *
     * Both detours run the same full pass rather than a targeted one. A pass is a dictionary walk and one
     * call per visible cell, and doing the whole panel is what makes the mark state idempotent: there is
     * no way for a cell to be left holding a verdict that belonged to a recycled item.
     */
    private static void RenderPageDetour(int index, IntPtr self, IntPtr methodInfo)
    {
        RenderPageFn? original = index < _pageOriginals.Count ? _pageOriginals[index] : null;
        original?.Invoke(self, methodInfo);
        // A hook body must never let an exception cross back into il2cpp code.
        try { Paint(self); }
        catch { Errors++; }
    }

    private static void RedrawDetour(int index, IntPtr self, IntPtr uid, IntPtr methodInfo)
    {
        RedrawFn? original = index < _itemOriginals.Count ? _itemOriginals[index] : null;
        original?.Invoke(self, uid, methodInfo);
        try { Paint(self); }
        catch { Errors++; }
    }

    private static void Paint(IntPtr tab)
    {
        if (tab == IntPtr.Zero) return;

        IntPtr tabClass = Il2CppMeta.ClassOf(tab);
        // The wardrobe reaches this code only if the class list above ever gains it; refuse by name
        // anyway, because the cost of being wrong is fighting UIWardrobe for the same overlay.
        string name = Il2CppMeta.ClassName(tabClass);
        if (name.IndexOf("Cosmetic", StringComparison.Ordinal) >= 0
            || name.IndexOf("Wardrobe", StringComparison.Ordinal) >= 0) return;

        // Grimoire Draw renders directly from its config record and never writes UIInventoryItem.Data.
        // Pooled cells retain that field, so reading it here would splice a previous equipment item's
        // stats onto the current Grimoire name and type.
        bool presentationOnly = string.Equals(name, "UIInventoryTab_Grimoires", StringComparison.Ordinal);

        if (_uidFieldOffset < 0) _uidFieldOffset = Il2CppMeta.FieldOffsetUp(tabClass, "InventoryItemsUID");
        if (_uidFieldOffset < 0) return;

        IntPtr dictionary = Marshal.ReadIntPtr(tab, _uidFieldOffset);
        if (dictionary == IntPtr.Zero) return;

        /**
         * The filter reload lands at the top of a pass, and the reason is unchanged: the file watcher
         * runs on a thread pool thread and only sets a flag; the actual re-parse happens on the main
         * thread, one pass after the save, which is also the exact moment the result becomes visible.
         * One reload per save, no locks, and an editor that writes three times per save does not cost
         * three parses.
         *
         * It is no longer the ONLY caller: `InventoryWatch` checks the same flag on its throttled tick,
         * because with the bag shut this pass never runs and its rules would be whatever they were at
         * boot. Whichever gets there first parses; the other reads the result.
         *
         * Nothing sound-related happens here any more. Arrivals are diffed out of the inventory DATA by
         * the watcher, whose baseline is what you OWN rather than what a rule happened to claim, so a
         * new rule cannot manufacture an arrival for a bag you have been carrying all evening.
         */
        FilterFile.ReloadIfChanged();
        FilterParser.ParsedFilter filter = FilterFile.Current;

        _marks.Clear();
        _presentationOnlyCells.Clear();
        Passes++;
        BagSnapshot.BeginPass(filter.Threshold);
        foreach ((IntPtr key, IntPtr cell) in Il2CppMeta.DictionaryEntries(dictionary))
        {
            if (cell == IntPtr.Zero) continue;
            string? uid = Il2CppMeta.ReadString(key);
            if (presentationOnly) _presentationOnlyCells.Add(cell);

            // ONE read per cell, feeding both readers of it: the verdict this cell is painted with,
            // and the row the editor's bag snapshot counts against. The snapshot deliberately does
            // not walk the inventory itself — there is no second walk to disagree with this one.
            bool readable = uid is not null
                && (presentationOnly
                    ? ItemReader.ReadPresentation(cell, uid, _facts)
                    : ItemReader.Read(cell, _facts));
            Mark mark = readable ? Judge(_facts, filter) : default;
            if (readable) BagSnapshot.Observe(uid!, _facts);

            // Every cell is written explicitly, hit or miss. Skipping the misses would leave a recycled
            // cell wearing the previous item's mark, which `UIInventoryItem.Clear` does not undo.
            if (mark.Level > 0)
            {
                _marks[cell] = mark;
                Draw(cell, mark);
                CellsLit++;
            }
            else
            {
                Draw(cell, default);
                CellsCleared++;
            }
        }

        // Last, and off this thread: `EndPass` compares the bag's content against what is already on
        // disk and queues a write only when they differ. A preview file for an editor is the least
        // urgent thing in this method and it must not be able to delay a frame.
        BagSnapshot.EndPass();
    }

    /**
     * The facts in <paramref name="facts"/>, judged: overrides first, then the rules in file order.
     *
     * `AlwaysShow`/`AlwaysHide` are checked before any rule because that is the whole reason they exist
     * — a per-item override is how a player escapes rule order without rewriting their filter.
     *
     * Shared with <see cref="InventoryWatch"/> rather than private, and that is the point: the colour
     * on a cell and the noise a pickup makes come out of ONE evaluation of one rule list. Two judges
     * could disagree about which rule claimed an item, and the player would have no way to tell which
     * of them was lying.
     */
    internal static Mark Judge(LootFilter.ItemFacts facts, FilterParser.ParsedFilter filter)
    {
        if (Named(facts, filter.Muted)) return new Mark(
            0, LootFilter.BackgroundBorder, true, (0, 0, 0), "", "", "always hidden", null);
        if (Named(facts, filter.Pinned)) return PinnedMark;

        LootFilter.LootRule? rule = LootFilter.Match(facts, filter.Rules, filter.Threshold);
        if (rule is null) return default;
        return new Mark(
            rule.Mute ? 0 : rule.Level,
            rule.Background,
            rule.Border,
            (rule.R, rule.G, rule.B),
            rule.Color,
            rule.Mute ? "" : rule.Label.Length > 0 ? rule.Label : "",
            $"rule \"{rule.Name}\"",
            rule.Mute ? null : rule.Sound);
    }

    /**
     * How an `AlwaysShow` item is drawn.
     *
     * Deliberately loud and deliberately a fixed colour: an override says "whatever else my filter
     * decides, show me this one", and giving it the palette of whichever rule it skipped would make it
     * indistinguishable from an ordinary match.
     */
    private static readonly Mark PinnedMark =
        new(LootFilter.LevelGlow, LootFilter.BackgroundBorder, true, LootFilter.ParseColor("#facc15"),
            "#facc15", "PINNED", "always shown", null);

    /// <summary>Does the item's displayed name or catalog id equal one of these, case-insensitively?</summary>
    private static bool Named(LootFilter.ItemFacts facts, string[] names)
    {
        for (int i = 0; i < names.Length; i++)
        {
            if (string.Equals(facts.Name, names[i], StringComparison.OrdinalIgnoreCase)
                || string.Equals(facts.Id, names[i], StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /**
     * Write one cell's presentation without covering its content.
     *
     * The full-background modes tint the `Image` on the UIInventoryItem ROOT. That is the game's own
     * dark card panel: it already sits behind the item art and text, and its sprite already owns the
     * rounded corners. The Highlight subtree remains the border. Borrowing the overlay itself was
     * wrong — a sprite-less overlay is a square drawn above the content, which hides the item.
     */
    private static void Draw(IntPtr cell, Mark mark)
    {
        IntPtr group = Marshal.ReadIntPtr(cell, _highlightFieldOffset);
        if (group == IntPtr.Zero) return;

        float borderAlpha = mark.Border
            ? mark.Level switch { 3 => GlowAlpha, 2 => MarkAlpha, 1 => DotAlpha, _ => 0f }
            : 0f;
        _setAlpha?.Invoke(group, borderAlpha, IntPtr.Zero);
        if (_getComponent is null || _graphicType == IntPtr.Zero) return;

        Mark visible = TintEnabled ? mark : default;
        PaintBackground(cell, visible);
        int painted = PaintSubtree(group, visible, 0);
        if (mark.Level > 0 && TintEnabled && !_tintReported)
        {
            _tintReported = true;
            _log?.Invoke($"inventory paint: first marked cell painted {painted} border graphic(s), background "
                       + LootFilter.BackgroundName(mark.Background));
        }
    }

    /// <summary>Paint or restore this object's graphics and descendants', bounded in depth and count.</summary>
    private static int PaintSubtree(IntPtr owner, Mark mark, int depth)
    {
        int painted = PaintGraphic(owner, mark);
        if (depth >= TintDepth || _getTransform is null || _getChildCount is null || _getChild is null) return painted;

        IntPtr root = _getTransform(owner, IntPtr.Zero);
        if (root == IntPtr.Zero) return painted;
        int children = _getChildCount(root, IntPtr.Zero);
        for (int i = 0; i < children && i < 8; i++)
        {
            IntPtr child = _getChild(root, i, IntPtr.Zero);
            if (child != IntPtr.Zero) painted += PaintSubtree(child, mark, depth + 1);
        }
        return painted;
    }

    private static int PaintGraphic(IntPtr owner, Mark mark)
    {
        if (mark.Level == 0) return 0;
        IntPtr graphic = _getComponent!(owner, _graphicType, IntPtr.Zero);
        if (graphic == IntPtr.Zero) return 0;
        WriteColor(graphic, mark.R, mark.G, mark.B, 1f);
        return 1;
    }

    private static void PaintBackground(IntPtr cell, Mark mark)
    {
        if (_imageType == IntPtr.Zero || _getName is null) return;

        if (mark.Level == 0 || mark.Background == LootFilter.BackgroundBorder)
        {
            if (_backgroundByCell.TryGetValue(cell, out IntPtr existing)) RestoreFill(existing);
            return;
        }

        if (!_backgroundByCell.TryGetValue(cell, out IntPtr image))
        {
            image = FindBackgroundImage(cell);
            if (image == IntPtr.Zero) return;
            _backgroundByCell[cell] = image;
        }
        ApplyFill(cell, image, mark);
    }

    private static IntPtr FindBackgroundImage(IntPtr cell)
    {
        if (_getTransform is null || _getChildCount is null || _getChild is null) return IntPtr.Zero;
        IntPtr root = _getTransform(cell, IntPtr.Zero);
        if (root == IntPtr.Zero) return IntPtr.Zero;
        int children = _getChildCount(root, IntPtr.Zero);
        for (int i = 0; i < children && i < 12; i++)
        {
            IntPtr child = _getChild(root, i, IntPtr.Zero);
            if (child == IntPtr.Zero) continue;
            IntPtr image = _getComponent!(child, _imageType, IntPtr.Zero);
            if (image == IntPtr.Zero) continue;
            string? name = Il2CppMeta.ReadString(_getName!(image, IntPtr.Zero));
            if (string.Equals(name, "Background-Container", StringComparison.Ordinal)) return image;
        }
        return IntPtr.Zero;
    }

    private static void ApplyFill(IntPtr cell, IntPtr image, Mark mark)
    {
        if (!_fills.TryGetValue(image, out FillState? state))
        {
            state = new FillState
            {
                Handle = IL2CPP.il2cpp_gchandle_new(image, false),
                Cell = cell,
                OriginalR = ReadFloat(image, _colorFieldOffset),
                OriginalG = ReadFloat(image, _colorFieldOffset + 4),
                OriginalB = ReadFloat(image, _colorFieldOffset + 8),
                OriginalA = ReadFloat(image, _colorFieldOffset + 12),
            };
            _fills.Add(image, state);
            if (!_backgroundReported)
            {
                _backgroundReported = true;
                _log?.Invoke("inventory paint: full background applied to rounded Background-Container");
            }
        }

        state.Background = mark.Background;
        state.Strength = mark.Level switch { 3 => GlowAlpha, 2 => MarkAlpha, _ => DotAlpha };
        RgbToHsv(mark.R, mark.G, mark.B, out state.Hue, out state.Saturation, out state.Value);
        // A grey or nearly-black holo colour still has to rotate visibly.
        if (state.Background == LootFilter.BackgroundHolo)
        {
            state.Saturation = Math.Max(state.Saturation, 0.65f);
            state.Value = Math.Max(state.Value, 0.55f);
        }

        if (state.Background == LootFilter.BackgroundHolo)
        {
            HoloColor(state, out float r, out float g, out float b);
            WriteBackground(image, state, r, g, b);
        }
        else
        {
            WriteBackground(image, state, mark.R, mark.G, mark.B);
        }
    }

    private static void RestoreFill(IntPtr image)
    {
        if (!_fills.TryGetValue(image, out FillState? state)) return;
        ReleaseFill(image, state, restore: true);
    }

    private static void ReleaseFill(IntPtr image, FillState state, bool restore)
    {
        IntPtr target = IL2CPP.il2cpp_gchandle_get_target(state.Handle);
        if (restore && target != IntPtr.Zero && (_objectAlive?.Invoke(target, IntPtr.Zero) ?? false))
        {
            WriteColor(target, state.OriginalR, state.OriginalG, state.OriginalB, state.OriginalA);
        }
        IL2CPP.il2cpp_gchandle_free(state.Handle);
        _fills.Remove(image);
        _backgroundByCell.Remove(state.Cell);
    }

    private static void RestoreAllFills()
    {
        _deadFills.Clear();
        foreach (IntPtr image in _fills.Keys) _deadFills.Add(image);
        for (int i = 0; i < _deadFills.Count; i++)
        {
            IntPtr image = _deadFills[i];
            if (_fills.TryGetValue(image, out FillState? state)) ReleaseFill(image, state, restore: true);
        }
        _deadFills.Clear();
        _backgroundByCell.Clear();
    }

    /**
     * Advance visible hue backgrounds from the one existing PlayerSave.Update hook.
     *
     * Four-frame throttling is 15 Hz at the game's usual 60 fps: smooth enough for a slow hue wheel,
     * while a bag full of ordinary border/fill rules costs only one integer comparison per frame.
     */
    public static void Tick()
    {
        if ((_animationFrame++ & 3) != 0 || _fills.Count == 0) return;

        _deadFills.Clear();
        foreach ((IntPtr image, FillState state) in _fills)
        {
            IntPtr target = IL2CPP.il2cpp_gchandle_get_target(state.Handle);
            if (target == IntPtr.Zero || !(_objectAlive?.Invoke(target, IntPtr.Zero) ?? false))
            {
                _deadFills.Add(image);
                continue;
            }
            if (state.Background != LootFilter.BackgroundHolo) continue;

            HoloColor(state, out float r, out float g, out float b);
            WriteBackground(target, state, r, g, b);
        }

        for (int i = 0; i < _deadFills.Count; i++)
        {
            IntPtr image = _deadFills[i];
            if (_fills.TryGetValue(image, out FillState? state)) ReleaseFill(image, state, restore: false);
        }
    }

    private static void HoloColor(FillState state, out float r, out float g, out float b)
    {
        float phase = (Environment.TickCount64 % 8000L) / 8000f;
        HsvToRgb((state.Hue + phase) % 1f, state.Saturation, state.Value, out r, out g, out b);
    }

    private static void RgbToHsv(float r, float g, float b, out float h, out float s, out float v)
    {
        float max = Math.Max(r, Math.Max(g, b));
        float min = Math.Min(r, Math.Min(g, b));
        float delta = max - min;
        v = max;
        s = max <= 0f ? 0f : delta / max;
        if (delta <= 0f) { h = 0f; return; }
        if (max == r) h = ((g - b) / delta) % 6f;
        else if (max == g) h = ((b - r) / delta) + 2f;
        else h = ((r - g) / delta) + 4f;
        h /= 6f;
        if (h < 0f) h += 1f;
    }

    private static void HsvToRgb(float h, float s, float v, out float r, out float g, out float b)
    {
        float scaled = h * 6f;
        int sector = (int)Math.Floor(scaled);
        float fraction = scaled - sector;
        float p = v * (1f - s);
        float q = v * (1f - s * fraction);
        float t = v * (1f - s * (1f - fraction));
        switch (sector % 6)
        {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
        }
    }

    private static float ReadFloat(IntPtr owner, int offset)
        => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(owner, offset));

    private static void WriteBackground(IntPtr image, FillState state, float r, float g, float b)
    {
        float keep = 1f - state.Strength;
        WriteColor(
            image,
            state.OriginalR * keep + r * state.Strength,
            state.OriginalG * keep + g * state.Strength,
            state.OriginalB * keep + b * state.Strength,
            state.OriginalA);
    }

    private static void WriteColor(IntPtr graphic, float r, float g, float b, float a)
    {
        Marshal.WriteInt32(graphic, _colorFieldOffset, BitConverter.SingleToInt32Bits(r));
        Marshal.WriteInt32(graphic, _colorFieldOffset + 4, BitConverter.SingleToInt32Bits(g));
        Marshal.WriteInt32(graphic, _colorFieldOffset + 8, BitConverter.SingleToInt32Bits(b));
        Marshal.WriteInt32(graphic, _colorFieldOffset + 12, BitConverter.SingleToInt32Bits(a));
        _setAllDirty?.Invoke(graphic, IntPtr.Zero);
    }

    /// <summary>What this is doing, for the log — the counters that separate "installed" from "drawing".</summary>
    public static string Status()
        => $"inventory paint: installed {Installed}, marks {MarkCount}, passes {Passes}, "
         + $"lit {CellsLit}, cleared {CellsCleared}, errors {Errors}";
}
