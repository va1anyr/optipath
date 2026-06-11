using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using ExileCore2;
using ExileCore2.PoEMemory.Elements.AtlasElements;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Helpers;
using ExileCore2.Shared.Nodes;
using ExileCore2.Shared.Enums;

using GameOffsets2.Native;

using ImGuiNET;

using RectangleF = ExileCore2.Shared.RectangleF;

namespace OptiPather;

// Atlas overlay that locates the closest unvisited map matching each active search and points you to it
// with a highlight ring, a path line and an off-screen arrow. Searches can carry "optionals" - maps whose
// proximity to a route makes that route preferable - and several searches can run in parallel, each in its
// own color. The graph is read straight from the atlas and searched on a background thread; the render
// loop only resolves live node positions for drawing.
public class OptiPather : BaseSettingsPlugin<OptiPatherSettings>
{
    public const string Version = "2.2.6";

    // On-disk graph format version. Bump on any schema change to NodeDto/EdgeDto/GraphSnapshot;
    // a file written by a different version is discarded and rebuilt by re-panning the atlas.
    private const int GraphSchemaVersion = 1;
    // Seconds of unsaved changes to tolerate before the worker writes the graph to disk.
    private const double SaveDebounceSeconds = 15;
    // Refuse to load a file claiming more nodes than this - a real atlas is far smaller, so a larger
    // count means a corrupt/garbage file.
    private const int MaxPersistNodes = 200000;
    // The swap detector must see its signal this many scans in a row before wiping the cache, so a
    // single partial atlas read (e.g. just after an instance reload) can't nuke remembered maps.
    private const int SwapConfirmScans = 3;
    // How many ticks a (league, character) reading must repeat before it is trusted as the live key.
    private const int IdentityStableTicks = 3;

    private const string ArrowTextureKey = "optipather_arrow.png";

    private IngameUIElements UI;
    private AtlasPanel AtlasPanel;

    private Vector2 screenCenter;
    private IntPtr arrowId;

    // Screen bounds for IsOnScreen, rebuilt once per render frame.
    private RectangleF cachedScreenRect;
    private readonly List<RectangleF> cachedTooltipRects = [];

    // The map tooltip sits under WorldMap child 13 or 14 depending on state.
    private static readonly int[] TooltipChildIndices = { 13, 14 };

    private bool MapFinderPanelIsOpen = false;
    private string mapFinderContentFilter = "";
    private string mapFinderNameFilter = "";
    private volatile bool mapFinderDirty = false;
    private int selectedPresetIndex = 0;
    // Width of the query list pane; dragged by the splitter between it and the editor.
    private float presetListWidth = 180f;
    // When a search is edited, force its next recompute to ignore hysteresis so the change takes effect now.
    private int mapFinderForceFresh = -1;
    // Per-preset manual target pin (preset index -> node coord key); transient, not persisted.
    private Dictionary<int, string> mapFinderPins = new();
    private DateTime lastFinderRecompute = DateTime.Now;

    // Only one background search runs at a time.
    private volatile bool finderBusy = false;

    // Latest computed routes (one per active/edited search), swapped in atomically by the worker.
    private volatile List<FinderResult> mapFinderResults = new();

    // Persistent atlas graph, accumulated across scans. AtlasPanel.Descriptions/Points only expose the
    // nodes near the current camera (they are UI elements), so a single scan sees just the viewport.
    // Merging every scan into a long-lived cache lets the search cover everywhere the camera has panned
    // over, without it staying there. Touched ONLY by the background worker (one runs at a time), so no
    // lock is needed; the render thread reads the published results + live Descriptions, never this cache.
    // GraphNode is immutable per instance - a re-seen node is REPLACED with a fresh snapshot rather than
    // mutated, so already-published results keep their own copies and the render thread never sees a tear.
    private readonly Dictionary<Vector2i, GraphNode> cachedNodes = new();
    private readonly Dictionary<Vector2i, HashSet<Vector2i>> cachedAdjacency = new();
    // Set from the UI (manual Rescan) to wipe the cache on the next scan. The worker ALSO auto-wipes
    // directly when it detects an atlas/character swap mid-scan; this flag is only the manual path.
    private volatile bool finderClearCacheRequested = false;
    // Published cache size, shown in the panel as a coverage readout.
    private volatile int cachedNodeCount = 0;

    // --- Atlas persistence (remember the graph on disk, per character, across restarts/reloads) ---
    // The (league, character) key the in-memory cache currently belongs to. Only the worker writes it.
    private volatile string loadedIdentityKey = null;
    // Set on the main thread; the worker saves the graph regardless of the debounce when this is set,
    // and clears it only after a successful write (so a dropped flag can't silently skip the flush).
    private volatile bool persistFlushRequested = false;
    // Set with finderClearCacheRequested by Rescan: also forget the on-disk file, not just memory.
    private volatile bool persistForgetRequested = false;
    // Set by the worker when the save directory is unwritable, so it stops retrying every scan.
    private volatile bool persistDisabled = false;
    // SavedUtc of the file the current cache was loaded from, surfaced as a "remembered ago" readout.
    private volatile string cacheRememberedAge = null;
    // Last notable cache lifecycle event (restore, auto-reset), surfaced in the panel so an unexpected
    // loss of remembered maps is attributable instead of silent.
    private volatile string lastCacheEvent = null;
    // Worker-local persistence bookkeeping (touched only inside the scan worker).
    private DateTime lastPersistSave = DateTime.MinValue;
    private bool persistDirty = false;
    private bool persistDirReady = false;
    // Consecutive scans the atlas-swap signal has held; the cache is only wiped once it confirms.
    private int swapSignalStreak = 0;
    // Main-thread identity debounce: a (league, character) read must repeat before it is trusted.
    private string identityCandidate = null;
    private int identityStableCount = 0;
    private string stableIdentityKey = null;
    // Tracks the atlas open/closed edge so the worker can flush once when the atlas is closed.
    private bool atlasWasVisible = false;

    // Distinct content/mod tags seen on the current atlas, published by the worker for the dropdown.
    private volatile List<string> availableContentTags = new();

    // Distinct map names seen on the current atlas, published by the worker for name auto-completion.
    private volatile List<string> availableMapNames = new();

    // Well-known content/mod types, always offered in the dropdown even before the atlas reveals one.
    // Anything else the atlas exposes (e.g. "Azmeri Energisation") is merged in at runtime.
    private static readonly string[] KnownContentTypes = {
        "Abyss", "Anomaly Map Boss", "Breach", "Cleansed", "Corrupted", "Corrupted Nexus",
        "Deadly Map Boss", "Delirium", "Expedition", "Irradiated", "Map Boss", "Powerful Map Boss",
        "Ritual", "Tower", "Unique Map",
    };

    // Distinct hues auto-assigned to new searches so parallel routes stay tellable apart.
    private static readonly Color[] PresetPalette = {
        Color.FromArgb(255, 0, 200, 255),    // cyan
        Color.FromArgb(255, 255, 170, 0),    // orange
        Color.FromArgb(255, 120, 230, 120),  // green
        Color.FromArgb(255, 230, 120, 230),  // magenta
        Color.FromArgb(255, 255, 90, 90),    // red
        Color.FromArgb(255, 240, 230, 120),  // yellow
        Color.FromArgb(255, 150, 150, 255),  // periwinkle
        Color.FromArgb(255, 120, 230, 230),  // teal
    };

    // A few content identities the game names differently from what players call them. Mapped so the
    // dropdown's familiar label (e.g. "Cleansed") matches the node's actual content id (e.g. "Sanctified").
    private static readonly Dictionary<string, string> ContentIdAliases = new(StringComparer.OrdinalIgnoreCase) {
        ["Sanctified"] = "Cleansed",
    };

    // Immutable copy of an atlas node, so the background search never touches live game objects.
    private sealed class GraphNode
    {
        public Vector2i Coord;
        public string Name;
        public bool Visited;
        public bool Unlocked;
        public bool Active;
        // Content / map mods on the node (e.g. "Powerful Map Boss", "Corrupted Nexus", "Tower").
        public List<string> ContentTags;
        // True when this snapshot came from a live atlas scan this session; false for a node rebuilt
        // from the saved file and not yet re-seen. Not persisted - it resets to false on every load.
        public bool SeenLive;
    }

    // On-disk shape of the accumulated atlas graph, one file per character. Plain ints are used instead
    // of Vector2i so coordinates never become JSON dictionary keys (which must be strings).
    private sealed class GraphSnapshot
    {
        public int SchemaVersion { get; set; }
        // The (league, character) key this file belongs to; asserted on load so a hand-copied or
        // mis-resolved file is rejected rather than loaded for the wrong character.
        public string IdentityKey { get; set; }
        public DateTime SavedUtc { get; set; }
        public int NodeCount { get; set; }
        public List<NodeDto> Nodes { get; set; } = new();
        public List<EdgeDto> Edges { get; set; } = new();
    }

    // A persisted node. Active is deliberately not stored: it is transient game state, and a stale
    // Active node would seed a bogus zero-step origin for the route.
    private sealed class NodeDto
    {
        public int X { get; set; }
        public int Y { get; set; }
        public string Name { get; set; }
        public bool V { get; set; }   // Visited
        public bool U { get; set; }   // Unlocked
        public List<string> T { get; set; }   // ContentTags
    }

    // One undirected edge, stored a single direction (the lexicographically smaller endpoint first);
    // both directions are re-added on load.
    private sealed class EdgeDto
    {
        public int AX { get; set; }
        public int AY { get; set; }
        public int BX { get; set; }
        public int BY { get; set; }
    }

    private sealed class FinderMatch
    {
        public GraphNode Node { get; init; }
        public int Steps { get; init; }
        // The content tag that matched the query, or null when the map name matched.
        public string MatchedContent { get; init; }
        // Optional bonus for this candidate, recorded so the results table can show why it ranks where it does.
        public float Bonus { get; set; }
    }

    // Per-optional outcome for the chosen route, used both to draw it and to explain the result: how many
    // maps matched the filter, the gap in hops from the nearest matched instance to the route (-1 if none
    // connect), whether that gap is inside the corridor radius (i.e. earned a bonus), the nearest instance,
    // and the hop-by-hop detour (route branch point first, optional last).
    private sealed class NearOptional
    {
        public string Label { get; init; }
        public int Matches { get; init; }
        public int Gap { get; init; } = -1;
        public bool InRange { get; init; }
        public GraphNode Node { get; init; }
        public List<GraphNode> Detour { get; init; } = [];
    }

    private sealed class FinderResult
    {
        public List<FinderMatch> Matches { get; init; } = [];
        public GraphNode Target { get; init; }
        public List<GraphNode> Path { get; init; }
        public int Steps { get; init; } = -1;
        public string TargetContent { get; init; }
        // Which search produced this result, how to color it, and the optionals that swayed the pick.
        public int PresetIndex { get; init; } = -1;
        public string PresetName { get; init; }
        public Color Color { get; init; } = Color.FromArgb(255, 0, 200, 255);
        public List<NearOptional> NearOptionals { get; init; } = [];
        public float Bonus { get; init; }
        // Ordered mandatory stops for a multi-stop route; empty for a single-target search.
        public List<RouteStop> Stops { get; init; } = [];
        // Notes to surface in the panel (e.g. a mandatory map with no reachable instance that was skipped).
        // Settable so a collapsed multi-stop search can delegate to the single planner yet keep its warnings.
        public List<string> Warnings { get; set; } = [];
    }

    // One stop on a multi-stop route: the map to run, the content that matched, its 1-based order, and the
    // cumulative hops from the frontier to reach it along the planned route.
    private sealed class RouteStop
    {
        public GraphNode Node { get; init; }
        public string MatchedContent { get; init; }
        public int CumulativeSteps { get; init; }
        public int Order { get; init; }
    }

    // A resolved optional filter plus its weight, shared across searches by Key so identical optionals
    // build their distance field only once.
    private sealed class OptDef
    {
        public string Key;
        public string Map;
        public string Content;
        public float Bonus;
        public string Label;
    }

    // Immutable snapshot of a search, captured on the main thread so the worker never reads live settings.
    private sealed class PresetQuery
    {
        public int Index;
        public string Name;
        // One entry = locate the closest; several = plan one route that visits them all.
        public List<MandatoryFilter> Mandatories = new();
        public Color Color;
        public List<OptDef> Optionals = new();
        public string PinnedKey;
        public bool Fresh;
    }

    // A resolved mandatory stop: a map name and/or content/mod the route must reach, plus a display label.
    private sealed class MandatoryFilter
    {
        public string Map;
        public string Content;
        public string Label;
    }

    // Hop distance from the nearest instance of an optional type to every node within the corridor radius,
    // plus which instance each node's distance came from (for placing the marker).
    private sealed class DistanceField
    {
        public Dictionary<Vector2i, int> Dist;
        public Dictionary<Vector2i, Vector2i> Source;
        public Dictionary<Vector2i, Vector2i> Parent;
        public int SeedCount;
    }

    private struct Scored
    {
        public FinderMatch Match;
        public float Effective;
        public float Bonus;
        public List<NearOptional> Near;
    }

    public override bool Initialise()
    {
        Input.RegisterKey(Settings.MapFinderPanelHotkey);
        Settings.MapFinderPanelHotkey.OnValueChanged += () => Input.RegisterKey(Settings.MapFinderPanelHotkey);

        // Unique key so the arrow doesn't collide with another plugin's texture.
        Graphics.InitImage(ArrowTextureKey, Path.Combine(DirectoryFullName, "textures", "arrow.png"));
        arrowId = Graphics.GetTextureId(ArrowTextureKey);

        MigrateLegacySearch();
        selectedPresetIndex = Settings.Presets.Count > 0 ? 0 : -1;

        CanUseMultiThreading = true;
        return true;
    }

    // Carry a 1.x single search forward into a preset so existing users keep their setup.
    private void MigrateLegacySearch()
    {
        Settings.Presets ??= new();
        if (Settings.Presets.Count > 0)
            return;
        if (string.IsNullOrWhiteSpace(Settings.SearchQuery) && string.IsNullOrWhiteSpace(Settings.SelectedContent))
            return;

        Settings.Presets.Add(new SearchPreset {
            Name = "Search 1",
            MandatoryMap = Settings.SearchQuery ?? "",
            MandatoryContent = Settings.SelectedContent ?? "",
            Active = true,
            ColorArgb = PresetPalette[0].ToArgb(),
        });
    }

    public override void DrawSettings()
    {
        ImGui.TextColored(new Vector4(0.45f, 0.78f, 0.95f, 1f), "OptiPather v" + Version);
        ImGui.Separator();
        base.DrawSettings();
    }

    public override void Tick()
    {
        UI = GameController?.Game?.IngameState?.IngameUi;
        AtlasPanel = UI?.WorldMap?.AtlasPanel;

        // Resolve the character/league identity on the main thread; the worker never reads game state.
        UpdateIdentity();

        bool visible = AtlasPanel is { IsVisible: true };

        // The atlas was just closed: flush the remembered graph once. No more scans dispatch while it is
        // hidden, so this is the last chance to save what was panned over before the player runs a map.
        if (atlasWasVisible && !visible) {
            atlasWasVisible = false;
            // The camera at the next reopen is unrelated to the close-time one - a held fit from the
            // old viewport must never bridge the gap. The first refit after reopen rebuilds it.
            heldFitSet = false;
            hardRejectStreak = 0;
            fitCacheAt = DateTime.MinValue;
            if (Settings.PersistAtlas && !persistDisabled && loadedIdentityKey != null) {
                persistFlushRequested = true;
                if (!finderBusy) {
                    mapFinderDirty = false;
                    DispatchRecompute();
                }
            }
        }

        if (!visible) {
            MapFinderPanelIsOpen = false;
            return;
        }
        atlasWasVisible = true;

        screenCenter = GameController.Window.GetWindowRectangle().Center - GameController.Window.GetWindowRectangle().Location;

        // Refresh every couple of seconds while a search is active (so step counts track progress) or
        // while the panel is open (so the content dropdown stays populated as the atlas reveals more).
        bool active = AnyActivePreset() || MapFinderPanelIsOpen;
        if (active && !finderBusy && DateTime.Now.Subtract(lastFinderRecompute).TotalSeconds > 2)
            mapFinderDirty = true;

        if (mapFinderDirty && !finderBusy) {
            mapFinderDirty = false;
            DispatchRecompute();
        }
    }

    // A zone change is a natural checkpoint to persist the graph; signal only (the worker honors it on
    // its next pass), never start file work here.
    public override void AreaChange(AreaInstance area)
    {
        if (Settings.PersistAtlas && !persistDisabled && loadedIdentityKey != null)
            persistFlushRequested = true;
    }

    // Reads the current (league, character) identity and debounces it, so a transient mixed read during
    // a character switch (e.g. new league but the previous name still in memory) does not switch files.
    // Runs on the main thread; the worker only ever sees the debounced stableIdentityKey via DispatchRecompute.
    private void UpdateIdentity()
    {
        string id = ResolveIdentity();
        if (id != null && id == identityCandidate) {
            if (identityStableCount < IdentityStableTicks)
                identityStableCount++;
        } else {
            identityCandidate = id;
            identityStableCount = id == null ? 0 : 1;
        }
        // Only promote a non-null identity once it has held steady; a transient null (loading/hideout)
        // keeps the last stable key so the worker simply doesn't switch this pass.
        if (id != null && identityStableCount >= IdentityStableTicks)
            stableIdentityKey = id;
    }

    // The save key for the active character, or null when not safely in game. League is used raw (only
    // sanitized for the filename) so SSF / HC / trade progressions, which share the coordinate space but
    // not completion, never collide on one file.
    private string ResolveIdentity()
    {
        try {
            var gc = GameController;
            if (gc == null || !gc.InGame || gc.IsLoading)
                return null;
            string league = gc.IngameState?.ServerData?.League;
            string name = gc.Player?.GetComponent<ExileCore2.PoEMemory.Components.Render>()?.Name;
            if (string.IsNullOrWhiteSpace(league) || string.IsNullOrWhiteSpace(name))
                return null;
            return Sanitize(league) + "__" + Sanitize(name);
        } catch {
            return null;
        }
    }

    public override void Render()
    {
        if (AtlasPanel == null)
            return;

        CheckKeybinds();

        if (MapFinderPanelIsOpen) {
            try { DrawMapFinderPanel(); }
            catch (Exception e) { LogError("Error drawing map finder panel: " + e.Message + "\n" + e.StackTrace); }
        }

        if (!AtlasPanel.IsVisible)
            return;

        UpdateScreenBounds();

        try { DrawMapFinderRoutes(); }
        catch (Exception e) { LogError("Error drawing map finder routes: " + e.Message + "\n" + e.StackTrace); }
    }

    private void CheckKeybinds()
    {
        if (AtlasPanel is not { IsVisible: true })
            return;

        if (Settings.MapFinderPanelHotkey.PressedOnce()) {
            MapFinderPanelIsOpen = !MapFinderPanelIsOpen;
            if (MapFinderPanelIsOpen)
                mapFinderDirty = true;   // refresh in case the atlas changed while it was closed
        }
    }

    #region Map Finder

    private bool AnyActivePreset()
        => Settings.Presets != null && Settings.Presets.Any(p => p != null && p.Active);

    private static Color PresetColor(SearchPreset p, int index)
        => p.ColorArgb != 0 ? Color.FromArgb(p.ColorArgb) : PresetPalette[index % PresetPalette.Length];

    // The searches to compute this pass: every active one, plus the one being edited so the panel shows a
    // live preview. Snapshotted into immutable queries so the worker never reads live settings.
    private List<PresetQuery> BuildActiveQueries()
    {
        var list = new List<PresetQuery>();
        var presets = Settings.Presets;
        if (presets == null)
            return list;

        var wanted = new SortedSet<int>();
        for (int i = 0; i < presets.Count; i++)
            if (presets[i] != null && presets[i].Active)
                wanted.Add(i);
        if (MapFinderPanelIsOpen && selectedPresetIndex >= 0 && selectedPresetIndex < presets.Count)
            wanted.Add(selectedPresetIndex);

        foreach (int i in wanted) {
            var p = presets[i];
            var opts = new List<OptDef>();
            if (p.Optionals != null) {
                foreach (var o in p.Optionals) {
                    if (o == null)
                        continue;
                    bool hasMap = !string.IsNullOrWhiteSpace(o.Map);
                    bool hasContent = !string.IsNullOrWhiteSpace(o.Content);
                    if (!hasMap && !hasContent)
                        continue;
                    opts.Add(new OptDef {
                        Key = NormalizeForMatch(o.Map) + "" + NormalizeForMatch(o.Content),
                        Map = o.Map?.Trim() ?? "",
                        Content = o.Content?.Trim() ?? "",
                        Bonus = o.Bonus,
                        Label = hasContent ? o.Content.Trim() : o.Map.Trim(),
                    });
                }
            }

            mapFinderPins.TryGetValue(i, out string pin);
            list.Add(new PresetQuery {
                Index = i,
                Name = string.IsNullOrWhiteSpace(p.Name) ? "(unnamed)" : p.Name.Trim(),
                Mandatories = BuildMandatoryFilters(p),
                Color = PresetColor(p, i),
                Optionals = opts,
                PinnedKey = pin,
                Fresh = i == mapFinderForceFresh,
            });
        }
        mapFinderForceFresh = -1;
        return list;
    }

    // Resolves a preset's mandatory stops into filters, honoring the multi-entry list and falling back to the
    // legacy single MandatoryMap/MandatoryContent. Empty entries are dropped.
    private static List<MandatoryFilter> BuildMandatoryFilters(SearchPreset p)
    {
        var mands = new List<MandatoryFilter>();
        void Add(string map, string content) {
            bool hasMap = !string.IsNullOrWhiteSpace(map);
            bool hasContent = !string.IsNullOrWhiteSpace(content);
            if (!hasMap && !hasContent)
                return;
            string mapT = map?.Trim() ?? "";
            string contentT = content?.Trim() ?? "";
            string label = hasMap && hasContent ? $"{mapT} / {contentT}" : (hasContent ? contentT : mapT);
            mands.Add(new MandatoryFilter { Map = mapT, Content = contentT, Label = label });
        }

        if (p.Mandatories != null)
            foreach (var me in p.Mandatories)
                if (me != null)
                    Add(me.Map, me.Content);
        if (mands.Count == 0)
            Add(p.MandatoryMap, p.MandatoryContent);   // legacy single-mandatory fallback
        return mands;
    }

    // Run the scan + search off the render thread; atlas reads here are read-only. The scan always runs
    // (even with nothing active) so the content dropdown can be populated from the live atlas.
    private void DispatchRecompute()
    {
        var queries = BuildActiveQueries();
        var atlas = AtlasPanel;
        int radius = Math.Max(1, Settings.CorridorRadius.Value);
        int maxExtra = Math.Max(0, Settings.MaxExtraHops.Value);
        float hysteresis = Math.Max(0f, Settings.OptionalHysteresis.Value);
        var previous = mapFinderResults;
        // Captured on the main thread and passed in; the worker never reads game state for identity.
        string identity = stableIdentityKey;

        finderBusy = true;
        Task.Run(() => {
            try {
                RecomputeMapFinder(atlas, queries, radius, maxExtra, hysteresis, previous, identity);
            } catch (Exception e) {
                LogError("Error computing map finder routes: " + e.Message + "\n" + e.StackTrace);
            } finally {
                lastFinderRecompute = DateTime.Now;
                finderBusy = false;
            }
        });
    }

    // Adds one directed edge to the accumulated adjacency, deduped. Returns true if it was new. Worker
    // thread only.
    private bool AddCachedEdge(Vector2i a, Vector2i b)
    {
        if (!cachedAdjacency.TryGetValue(a, out var set))
            cachedAdjacency[a] = set = new HashSet<Vector2i>();
        return set.Add(b);
    }

    // Scans the atlas into a private graph, runs a multi-source BFS out from your completed/unlocked maps
    // (shared by every search), builds a proximity distance field per distinct optional, then scores each
    // search's candidates and publishes one result apiece. Also publishes the content tags for the dropdown.
    private void RecomputeMapFinder(AtlasPanel atlas, List<PresetQuery> queries, int radius, int maxExtra, float hysteresis, List<FinderResult> previous, string currentIdentityKey)
    {
        bool persistOn = Settings.PersistAtlas && !persistDisabled;

        // Honor a manual Rescan first, before any load, so the wipe is never undone by a same-pass
        // reload. Rescan also forgets the saved file for this character (persistForgetRequested).
        if (finderClearCacheRequested) {
            finderClearCacheRequested = false;
            bool forget = persistForgetRequested;
            persistForgetRequested = false;
            cachedNodes.Clear();
            cachedAdjacency.Clear();
            cachedNodeCount = 0;
            persistDirty = false;
            swapSignalStreak = 0;
            if (persistOn && forget && loadedIdentityKey != null)
                ForgetSnapshotFiles(loadedIdentityKey);
        }

        // Switch the remembered graph when the character/league changes (or first resolves). Loads from
        // disk into the cache; on any failure the cache is left empty for that character, not crashed.
        bool justLoaded = false;
        if (persistOn && currentIdentityKey != null && currentIdentityKey != loadedIdentityKey) {
            EnsureCacheForIdentity(currentIdentityKey);
            justLoaded = true;
        }

        // Flush the remembered graph if a checkpoint asked for it. Done before the atlas-readability
        // returns below so the flush still happens when the atlas has already been closed.
        PersistIfDue();

        // A pass dispatched while the atlas is closed (the close-time flush, or a panel torn down by a
        // loading screen) is persistence-only. A hidden panel's elements can read zeroed without throwing,
        // and merging those reads would overwrite good remembered state with garbage - so never scan one,
        // and keep the previously published routes; they are redrawn the moment the atlas reopens.
        bool atlasReadable = false;
        try { atlasReadable = atlas is { IsVisible: true }; } catch { }
        if (!atlasReadable) {
            swapSignalStreak = 0;   // the streak counts consecutive readable scans, not scans-with-gaps
            return;
        }

        List<AtlasNodeDescription> descs;
        try {
            descs = atlas.Descriptions?.ToList() ?? [];
        } catch {
            swapSignalStreak = 0;
            return;   // not readable this pass - keep the previous result
        }

        if (descs.Count == 0) {
            swapSignalStreak = 0;
            return;   // atlas momentarily unreadable - keep the previous result instead of blanking the panel
        }

        // Snapshot the nodes visible this scan and compare against the cache to spot an atlas swap, so a stale
        // cache is dropped before this scan is merged in. Two signals: many renamed coords (a different atlas
        // layout), or completed maps that are suddenly incomplete - a map can never un-complete for the same
        // character, so that means a different character is reusing the coordinate space (map names are stable
        // across characters, so the name signal alone would miss a character swap).
        int reseen = 0, nameMismatches = 0, visitedRegressions = 0;
        // Tracks whether this scan changed anything worth re-saving (the SeenLive flag flipping does not
        // count - it is session state, not persisted).
        bool changed = false;
        var freshThisScan = new List<GraphNode>(descs.Count);
        foreach (var d in descs) {
            var coord = d.Coordinate;

            var el = d.Element;
            if (el == null)
                continue;

            bool visited, unlocked, active;
            try {
                visited = el.IsVisited;
                unlocked = el.IsUnlocked;
                active = el.IsActive;
            } catch {
                continue;
            }

            string name = null;
            try { name = el.Area?.Name; } catch { }

            var tags = CollectContentTags(el);
            bool elementLoaded = !string.IsNullOrEmpty(name);
            bool seenLive = true;

            if (cachedNodes.TryGetValue(coord, out var old) && old != null) {
                reseen++;
                // The swap tally compares RAW reads against the cache, and only for elements that are
                // actually loaded - an unloaded one reads a blank name and all-false flags, which is
                // noise, not evidence. The merge below decides what the cache keeps, so a genuine swap
                // (elements load fine yet keep reading visited=false against a remembered true) re-fires
                // the signal scan after scan until the streak confirms, while a transient bad read never
                // becomes the baseline it would need to hide behind.
                if (elementLoaded) {
                    if (!string.IsNullOrEmpty(old.Name) && !string.Equals(old.Name, name, StringComparison.Ordinal))
                        nameMismatches++;
                    if (old.Visited && !visited)
                        visitedRegressions++;
                }

                // Merge defensively: while the panel repopulates after an instance reload, an element can
                // momentarily read blank/all-false without throwing, and committing such a read used to
                // silently poison the remembered graph (no seeds, no matches) until the user panned back
                // over it. An unloaded element contributes nothing - the remembered node is kept as-is,
                // including whether it was ever seen live, so a garbage read can never pass for live
                // confirmation. A loaded element is trusted, except that Visited stays monotonic for one
                // character (a map never un-completes; a real swap is handled by the confirmed wipe below)
                // and that content tags, which populate later than the name, never collapse to empty.
                if (!elementLoaded) {
                    name = old.Name;
                    visited = old.Visited;
                    unlocked = old.Unlocked;
                    tags = old.ContentTags ?? tags;
                    seenLive = old.SeenLive;
                } else {
                    visited |= old.Visited;
                    if (tags.Count == 0 && old.ContentTags is { Count: > 0 })
                        tags = old.ContentTags;
                }

                if (old.Visited != visited || old.Unlocked != unlocked
                    || !string.Equals(old.Name, name, StringComparison.Ordinal)
                    || (old.ContentTags?.Count ?? 0) != tags.Count)
                    changed = true;
            } else {
                changed = true;   // a coordinate never seen before
            }

            freshThisScan.Add(new GraphNode {
                Coord = coord,
                Name = name,
                Visited = visited,
                Unlocked = unlocked,
                Active = active,
                ContentTags = tags,
                SeenLive = seenLive,
            });
        }

        // The swap signal alone is noisy: right after an instance reload the atlas UI repopulates and a
        // handful of completed maps can momentarily read unvisited, which used to wipe the whole cache.
        // Require the signal to hold for several scans in a row, and never act on the pass that just
        // loaded a file from disk (its nodes have not been confirmed against a live viewport yet). A
        // genuine character/atlas swap is caught deterministically by identity keying instead.
        bool swapSignal = (reseen >= 10 && nameMismatches > reseen / 2) || visitedRegressions >= 3;
        if (justLoaded)
            swapSignal = false;
        swapSignalStreak = swapSignal ? swapSignalStreak + 1 : 0;
        if (swapSignal)
            LogMessage($"OptiPather: live atlas disagrees with remembered graph (regressions {visitedRegressions}, renames {nameMismatches}/{reseen}) - {swapSignalStreak}/{SwapConfirmScans}");
        if (swapSignalStreak >= SwapConfirmScans) {
            swapSignalStreak = 0;
            cachedNodes.Clear();
            cachedAdjacency.Clear();
            cachedNodeCount = 0;
            persistDirty = false;
            // The live atlas persistently disagrees with the remembered graph (a relayout, or a different
            // character reusing this name+league); forget the file so it can't reload the bad data.
            if (persistOn && loadedIdentityKey != null)
                ForgetSnapshotFiles(loadedIdentityKey);
            cacheRememberedAge = null;
            lastCacheEvent = "remembered maps reset (atlas no longer matched)";
            // Routes computed from the dropped graph must not keep drawing on what is now known to be a
            // different atlas; this scan was also tallied and clamped against that graph, so none of its
            // snapshots can be trusted either - rebuild from the next clean scan instead of merging.
            mapFinderResults = new List<FinderResult>();
            return;
        }

        // Merge this scan's connections into the accumulated adjacency. Links are bidirectional and
        // atlas.Points can read partially, so we only ever ADD edges (a HashSet dedups across scans) -
        // which also heals partial reads over time.
        try {
            var points = atlas.Points?.ToList() ?? [];
            foreach (var point in points) {
                var source = point.Source;
                var targets = point.Targets;
                if (targets == null)
                    continue;
                foreach (var target in targets) {
                    if (target == default)
                        continue;
                    if (AddCachedEdge(source, target)) changed = true;
                    if (AddCachedEdge(target, source)) changed = true;
                }
            }
        } catch {
            // Keep whatever connections are already cached if this read fails.
        }

        // Merge the node snapshots in. A re-seen coord is replaced with its fresh snapshot (so its
        // visited/unlocked/content stay current); coords not seen this scan are kept, which is what
        // extends coverage past the current viewport.
        foreach (var gn in freshThisScan)
            cachedNodes[gn.Coord] = gn;
        cachedNodeCount = cachedNodes.Count;
        if (changed)
            persistDirty = true;

        // The accumulated cache IS the working graph for this scan.
        var nodes = cachedNodes.Values.ToList();
        var nodeByCoord = cachedNodes;
        var adjacency = cachedAdjacency;

        // A node with no links may be a not-yet-loaded connection (atlas.Points reads partially) rather
        // than an ungenerated phantom (a unique that only appears once a logbook extends the atlas), so
        // every node stays matchable. Phantoms are kept from seeding bogus 0-step targets by gating the
        // BFS seeds on having connections (below), leaving a link-less map simply unreachable ("?") until
        // its connections appear on a later scan.
        bool haveConnections = adjacency.Count > 0;

        // Publish the distinct content/mod tags and map names on this atlas so the pickers can offer them.
        var distinctTags = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var distinctNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in nodes) {
            if (n.ContentTags != null)
                foreach (var t in n.ContentTags)
                    distinctTags.Add(t);
            if (!string.IsNullOrWhiteSpace(n.Name))
                distinctNames.Add(n.Name);
        }
        availableContentTags = distinctTags.ToList();
        availableMapNames = distinctNames.ToList();

        if (nodes.Count == 0)
            return;   // nothing usable this pass (transient) - keep the previous result

        // Steps are measured from everywhere you can already stand (completed or unlocked). If you have no
        // progress at all, fall back to seeding from the active node. Done once and shared by all searches.
        var dist = new Dictionary<Vector2i, int>(nodes.Count);
        var prev = new Dictionary<Vector2i, Vector2i>(nodes.Count);
        var queue = new Queue<Vector2i>();

        void Seed(Func<GraphNode, bool> isSource) {
            foreach (var n in nodes)
                if (isSource(n) && dist.TryAdd(n.Coord, 0))
                    queue.Enqueue(n.Coord);
        }

        // Only seed from nodes that actually have connections, so an ungenerated phantom flagged unlocked
        // can't seed itself as a 0-step target. If connections couldn't be read at all, seed normally.
        bool Connected(GraphNode n) => !haveConnections || adjacency.ContainsKey(n.Coord);
        // Visited is monotonic (a map cannot un-complete for the same character), so a remembered Visited
        // node always seeds the frontier. Unlocked is decayable, so a node restored from disk seeds only
        // once it has been reconfirmed live this session - otherwise a stale region's old frontier could
        // pose as "where you can already stand" and understate distances or hijack the route.
        Seed(n => (n.Visited || (n.Unlocked && n.SeenLive)) && Connected(n));
        if (queue.Count == 0)
            Seed(n => n.Active && Connected(n));

        while (queue.Count > 0) {
            var coord = queue.Dequeue();
            int d = dist[coord];
            if (!adjacency.TryGetValue(coord, out var neighbors))
                continue;
            foreach (var next in neighbors) {
                if (next == default || !nodeByCoord.ContainsKey(next))
                    continue;
                if (!dist.TryAdd(next, d + 1))
                    continue;
                prev[next] = coord;
                queue.Enqueue(next);
            }
        }

        // Build one proximity field per distinct optional across all searches (deduped by key), so two
        // searches that share an optional only pay for it once.
        var fields = new Dictionary<string, DistanceField>();
        var optByKey = new Dictionary<string, OptDef>();
        foreach (var q in queries)
            foreach (var o in q.Optionals)
                optByKey.TryAdd(o.Key, o);
        foreach (var kv in optByKey) {
            var seeds = new List<Vector2i>();
            foreach (var n in nodes)
                // Skip completed maps: an optional you have already run is worth nothing to the route, so it
                // must not seed the proximity field (otherwise a done map draws an optional ring/label and
                // can even pull the route toward itself). Mirrors the mandatory candidate filter.
                if (!n.Visited && dist.ContainsKey(n.Coord) && MatchesFilters(n, kv.Value.Map, kv.Value.Content, out _))
                    seeds.Add(n.Coord);
            fields[kv.Key] = BuildDistanceField(adjacency, nodeByCoord, seeds);
        }

        var results = new List<FinderResult>(queries.Count);
        foreach (var q in queries) {
            FinderResult prevResult = previous?.FirstOrDefault(r => r.PresetIndex == q.Index);
            results.Add(ComputePresetResult(q, nodes, dist, prev, nodeByCoord, adjacency, fields, radius, maxExtra, hysteresis, prevResult));
        }

        // If a cache wipe was requested while this scan was already running, these results were computed from
        // the stale graph - don't publish them; the next dispatch will clear and rebuild.
        if (finderClearCacheRequested)
            return;

        mapFinderResults = results;

        // Persist the freshly-merged graph (debounced, worker-side). PersistIfDue re-checks the wipe flag.
        PersistIfDue();
    }

    // Multi-source BFS out from a set of optional instances, capped at the corridor radius. Each reached
    // node records the distance to, and identity of, its nearest seeding instance.
    private static DistanceField BuildDistanceField(Dictionary<Vector2i, HashSet<Vector2i>> adjacency, Dictionary<Vector2i, GraphNode> nodeByCoord, IEnumerable<Vector2i> seeds)
    {
        var fieldDist = new Dictionary<Vector2i, int>();
        var source = new Dictionary<Vector2i, Vector2i>();
        var parent = new Dictionary<Vector2i, Vector2i>();
        var queue = new Queue<Vector2i>();
        int seedCount = 0;

        foreach (var s in seeds) {
            if (nodeByCoord.ContainsKey(s) && fieldDist.TryAdd(s, 0)) {
                source[s] = s;
                seedCount++;
                queue.Enqueue(s);
            }
        }

        // Full BFS (not capped at the corridor radius): the radius only governs the bonus, but the true hop
        // distance to every reachable node is needed to explain "how far" an out-of-range optional is.
        while (queue.Count > 0) {
            var coord = queue.Dequeue();
            int d = fieldDist[coord];
            if (!adjacency.TryGetValue(coord, out var neighbors))
                continue;
            foreach (var next in neighbors) {
                if (next == default || !nodeByCoord.ContainsKey(next))
                    continue;
                if (!fieldDist.TryAdd(next, d + 1))
                    continue;
                source[next] = source[coord];
                parent[next] = coord;
                queue.Enqueue(next);
            }
        }

        return new DistanceField { Dist = fieldDist, Source = source, Parent = parent, SeedCount = seedCount };
    }

    // Dispatches a search to the right planner: no mandatory -> empty; one -> point to the closest matching
    // instance; several -> plan a single route that visits them all (best instance + order).
    private static FinderResult ComputePresetResult(PresetQuery q, List<GraphNode> nodes, Dictionary<Vector2i, int> dist, Dictionary<Vector2i, Vector2i> prev, Dictionary<Vector2i, GraphNode> nodeByCoord, Dictionary<Vector2i, HashSet<Vector2i>> adjacency, Dictionary<string, DistanceField> fields, int radius, int maxExtra, float hysteresis, FinderResult prevResult)
    {
        if (q.Mandatories == null || q.Mandatories.Count == 0)
            return new FinderResult { PresetIndex = q.Index, PresetName = q.Name, Color = q.Color };
        if (q.Mandatories.Count >= 2)
            return ComputeMultiStopRoute(q, nodes, dist, prev, nodeByCoord, adjacency, fields, radius, maxExtra, hysteresis, prevResult);
        return ComputeSingleResult(q, nodes, dist, prev, nodeByCoord, fields, radius, maxExtra, hysteresis, prevResult);
    }

    // Picks which instance of the search's single mandatory type to recommend. Among matches within the
    // extra-hop budget, the chosen one minimizes EffectiveSteps = steps - optionalBonus, where each
    // optional's bonus fades linearly with how far it sits from the route and the total is capped by the
    // budget. A pinned map always wins; near-ties are held by hysteresis so the recommendation doesn't flicker.
    private static FinderResult ComputeSingleResult(PresetQuery q, List<GraphNode> nodes, Dictionary<Vector2i, int> dist, Dictionary<Vector2i, Vector2i> prev, Dictionary<Vector2i, GraphNode> nodeByCoord, Dictionary<string, DistanceField> fields, int radius, int maxExtra, float hysteresis, FinderResult prevResult)
    {
        var empty = new FinderResult { PresetIndex = q.Index, PresetName = q.Name, Color = q.Color };

        var mand = q.Mandatories[0];
        var matches = new List<FinderMatch>();
        foreach (var n in nodes) {
            if (n.Visited || !MatchesFilters(n, mand.Map, mand.Content, out string matchedContent))
                continue;
            matches.Add(new FinderMatch {
                Node = n,
                Steps = dist.TryGetValue(n.Coord, out int s) ? s : -1,
                MatchedContent = matchedContent
            });
        }
        // Keep a steps-ordered view for the selection fallbacks (closest first).
        matches = matches
            .OrderBy(m => m.Steps < 0 ? int.MaxValue : m.Steps)
            .ThenBy(m => m.Node.Name ?? "", StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matches.Count == 0)
            return empty;

        // Score every reachable candidate up front - its optional bonus and per-optional detail - so the
        // results table can rank by effective steps and a pinned pick still shows everyone's bonus.
        var reachable = matches.Where(m => m.Steps >= 0).ToList();
        var nearByMatch = new Dictionary<FinderMatch, List<NearOptional>>();
        foreach (var m in reachable) {
            ScoreCandidate(m, q.Optionals, fields, prev, nodeByCoord, radius, maxExtra, out float bonus, out var near);
            m.Bonus = bonus;
            nearByMatch[m] = near;
        }

        FinderMatch chosen = null;
        float chosenBonus = 0f;
        List<NearOptional> chosenNear = new();

        // A pinned target keeps priority while it still matches.
        if (q.PinnedKey != null) {
            chosen = matches.FirstOrDefault(m => m.Node.Coord.ToString() == q.PinnedKey);
            if (chosen != null && chosen.Steps >= 0) {
                chosenBonus = chosen.Bonus;
                chosenNear = nearByMatch.TryGetValue(chosen, out var cn) ? cn : new();
            }
        }

        if (chosen == null) {
            if (reachable.Count == 0) {
                chosen = matches[0];   // nothing reachable - surface it anyway, no bonus
            } else {
                int closest = reachable.Min(m => m.Steps);
                Scored best = default;
                bool haveBest = false;
                foreach (var m in reachable) {
                    if (m.Steps - closest > maxExtra)   // anti-teleport prefilter for the actual pick
                        continue;
                    var sc = new Scored { Match = m, Effective = m.Steps - m.Bonus, Bonus = m.Bonus, Near = nearByMatch[m] };
                    if (!haveBest || CompareScored(sc, best) < 0) {
                        best = sc;
                        haveBest = true;
                    }
                }

                if (!haveBest) {
                    chosen = reachable[0];
                } else {
                    chosen = best.Match;
                    chosenBonus = best.Bonus;
                    chosenNear = best.Near;

                    // Hold the previous pick unless a challenger beats it by more than the hysteresis margin -
                    // but never right after the search was edited (q.Fresh), so changes take effect at once.
                    if (!q.Fresh && hysteresis > 0f && prevResult?.Target != null && prevResult.Target.Coord.ToString() != chosen.Node.Coord.ToString()) {
                        var prevMatch = reachable.FirstOrDefault(m => m.Node.Coord.ToString() == prevResult.Target.Coord.ToString());
                        if (prevMatch != null && prevMatch.Steps - closest <= maxExtra) {
                            float prevEffective = prevMatch.Steps - prevMatch.Bonus;
                            if (prevEffective - best.Effective <= hysteresis) {
                                chosen = prevMatch;
                                chosenBonus = prevMatch.Bonus;
                                chosenNear = nearByMatch[prevMatch];
                            }
                        }
                    }
                }
            }
        }

        // Order the table by effective steps (raw steps minus the optional bonus) so the maps the optionals
        // make most worthwhile rank highest - the same measure the recommendation itself is chosen by.
        // Unreachable maps sink to the bottom.
        var ordered = matches
            .OrderBy(m => m.Steps < 0 ? float.MaxValue : m.Steps - m.Bonus)
            .ThenBy(m => m.Steps < 0 ? int.MaxValue : m.Steps)
            .ThenBy(m => m.Node.Name ?? "", StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new FinderResult {
            Matches = ordered,
            Target = chosen.Node,
            Path = chosen.Steps >= 0 ? ReconstructFinderPath(chosen.Node.Coord, prev, nodeByCoord) : null,
            Steps = chosen.Steps,
            TargetContent = chosen.MatchedContent,
            PresetIndex = q.Index,
            PresetName = q.Name,
            Color = q.Color,
            NearOptionals = chosenNear ?? new(),
            Bonus = chosenBonus,
        };
    }

    // Plans a single route from your completed/unlocked frontier that visits one instance of EVERY mandatory
    // map/content, choosing which instance of each and the visiting order to minimize total hops. Optionals
    // nudge the chosen instances (a local-search swap toward more optional-proximity). Groups with no
    // reachable instance are skipped with a warning. The accumulated atlas graph can hold more than one
    // disconnected region, so the plan is confined to the single connected component that satisfies the most
    // mandatory groups; any group whose only instances sit in another region is reported, not silently dropped.
    private static FinderResult ComputeMultiStopRoute(PresetQuery q, List<GraphNode> nodes, Dictionary<Vector2i, int> dist, Dictionary<Vector2i, Vector2i> prev, Dictionary<Vector2i, GraphNode> nodeByCoord, Dictionary<Vector2i, HashSet<Vector2i>> adjacency, Dictionary<string, DistanceField> fields, int radius, int maxExtra, float hysteresis, FinderResult prevResult)
    {
        const int MaxCandidatesPerGroup = 4;   // keep the nearest few instances of each mandatory type
        const int MaxDpGroups = 8;             // exact Held-Karp up to this many groups, greedy beyond
        const int INF = int.MaxValue / 4;

        var warnings = new List<string>();

        // Candidate instances per mandatory group: unvisited, matching, and reachable from the frontier.
        var groupCands = new List<List<FinderMatch>>();
        var groupFilters = new List<MandatoryFilter>();
        foreach (var mf in q.Mandatories) {
            var cands = new List<FinderMatch>();
            foreach (var n in nodes) {
                if (n.Visited)
                    continue;
                if (!MatchesFilters(n, mf.Map, mf.Content, out string mc))
                    continue;
                if (!dist.TryGetValue(n.Coord, out int s))
                    continue;
                cands.Add(new FinderMatch { Node = n, Steps = s, MatchedContent = mc });
            }
            cands = cands
                .OrderBy(c => c.Steps)
                .ThenBy(c => c.Node.Name ?? "", StringComparer.OrdinalIgnoreCase)
                .Take(MaxCandidatesPerGroup)
                .ToList();
            if (cands.Count == 0)
                warnings.Add($"{mf.Label}: no reachable map matches - skipped");
            else { groupCands.Add(cands); groupFilters.Add(mf); }
        }

        if (groupCands.Count == 0)
            return new FinderResult { PresetIndex = q.Index, PresetName = q.Name, Color = q.Color, Warnings = warnings };

        // Flatten candidates, remembering each one's group.
        var cand = new List<FinderMatch>();
        var candGroup = new List<int>();
        for (int g = 0; g < groupCands.Count; g++)
            foreach (var c in groupCands[g]) { cand.Add(c); candGroup.Add(g); }
        int C = cand.Count;
        int m = groupCands.Count;

        // BFS from each candidate, so we know candidate-candidate hop distances and can rebuild each leg.
        var candDist = new Dictionary<Vector2i, int>[C];
        var candPrev = new Dictionary<Vector2i, Vector2i>[C];
        for (int i = 0; i < C; i++) {
            var (cd, cp) = BfsFrom(cand[i].Node.Coord, adjacency, nodeByCoord);
            candDist[i] = cd;
            candPrev[i] = cp;
        }

        int Start(int j) => cand[j].Steps;                                              // frontier -> j
        int Hop(int i, int j) => candDist[i].TryGetValue(cand[j].Node.Coord, out int d) ? d : INF;  // i -> j

        // Group candidates into connected components (undirected reachability) and plan within the single
        // component that covers the most mandatory groups - so a stop in a different atlas region is never
        // silently dropped or stitched in with a nonsense hop. Ties break toward the nearer component.
        var uf = new int[C];
        for (int i = 0; i < C; i++) uf[i] = i;
        int Find(int x) { while (uf[x] != x) { uf[x] = uf[uf[x]]; x = uf[x]; } return x; }
        for (int i = 0; i < C; i++)
            for (int j = i + 1; j < C; j++)
                if (Hop(i, j) < INF) uf[Find(i)] = Find(j);

        var compCands = new Dictionary<int, List<int>>();
        var compGroups = new Dictionary<int, HashSet<int>>();
        for (int i = 0; i < C; i++) {
            int r = Find(i);
            if (!compCands.TryGetValue(r, out var cl)) { compCands[r] = cl = new(); compGroups[r] = new(); }
            cl.Add(i);
            compGroups[r].Add(candGroup[i]);
        }

        // Prefer the component covering the most groups; then, among ties, the one with the most candidates
        // confirmed live this session (so a stale region restored from disk doesn't out-rank the region the
        // player is actually in); then the nearer one.
        int bestRoot = -1, bestGroups = -1, bestLive = -1, bestNearest = INF;
        foreach (var kv in compCands) {
            int groups = compGroups[kv.Key].Count;
            int nearest = kv.Value.Min(Start);
            int live = kv.Value.Count(ci => cand[ci].Node.SeenLive);
            if (groups > bestGroups
                || (groups == bestGroups && live > bestLive)
                || (groups == bestGroups && live == bestLive && nearest < bestNearest)) {
                bestGroups = groups; bestLive = live; bestNearest = nearest; bestRoot = kv.Key;
            }
        }

        var chosenCands = compCands[bestRoot];
        var coveredGroups = compGroups[bestRoot];
        for (int g = 0; g < m; g++)
            if (!coveredGroups.Contains(g))
                warnings.Add($"{groupFilters[g].Label}: reachable, but in a separate atlas region - not on this route");

        // A search that collapses to a single reachable, connected mandatory is really a single-target search;
        // hand it to that planner so the panel keeps its ranked table, pinning and hysteresis.
        if (coveredGroups.Count == 1) {
            int onlyGroup = coveredGroups.First();
            var singleQ = new PresetQuery {
                Index = q.Index, Name = q.Name, Color = q.Color,
                Mandatories = new() { groupFilters[onlyGroup] },
                Optionals = q.Optionals, PinnedKey = q.PinnedKey, Fresh = q.Fresh,
            };
            var single = ComputeSingleResult(singleQ, nodes, dist, prev, nodeByCoord, fields, radius, maxExtra, hysteresis, prevResult);
            if (warnings.Count > 0)
                single.Warnings = warnings;
            return single;
        }

        // Solve over the chosen component only: remap its candidates + groups to a compact sub-problem.
        var gmap = new Dictionary<int, int>();
        foreach (int g in coveredGroups.OrderBy(x => x))
            gmap[g] = gmap.Count;
        var sub = chosenCands;                              // sub index -> original candidate index
        int subC = sub.Count;
        int subM = gmap.Count;
        var subGroup = new List<int>(subC);
        foreach (int oi in sub) subGroup.Add(gmap[candGroup[oi]]);
        int SubStart(int si) => Start(sub[si]);
        int SubHop(int si, int sj) => Hop(sub[si], sub[sj]);

        List<int> subOrder = (subM <= MaxDpGroups)
            ? SolveHeldKarp(subC, subM, subGroup, SubStart, SubHop, INF)
            : SolveGreedyRoute(subC, subM, subGroup, SubStart, SubHop, INF);
        if (subOrder == null || subOrder.Count == 0)
            subOrder = SolveGreedyRoute(subC, subM, subGroup, SubStart, SubHop, INF);

        var order = new List<int>(subOrder.Count);
        foreach (int si in subOrder) order.Add(sub[si]);

        // Optionals nudge: swap a stop's instance for another of the same group when it lowers the effective
        // total (raw hops minus optional bonus). The visiting order is left as planned.
        order = ApplyOptionalNudge(order, candGroup, cand, candPrev, prev, nodeByCoord, fields, q.Optionals, radius, maxExtra, Start, Hop, INF);

        // Build the full node path: frontier -> stop1 -> stop2 -> ... concatenating each leg.
        var fullPath = BuildRoutePath(order, cand, candPrev, prev, nodeByCoord);

        // Score optionals over the whole route, and build the ordered stop list with cumulative hops. A single
        // map that satisfies two mandatories (e.g. a Crypt that also carries Breach) is merged into one stop.
        ScoreOptionalsForPath(fullPath, q.Optionals, fields, nodeByCoord, radius, maxExtra, out float bonus, out var near);

        var stops = new List<RouteStop>();
        var stopMatches = new List<FinderMatch>();
        var stopByCoord = new Dictionary<Vector2i, int>();
        int cum = Start(order[0]);
        for (int p = 0; p < order.Count; p++) {
            if (p > 0)
                cum += Hop(order[p - 1], order[p]);
            var c = cand[order[p]];
            if (stopByCoord.TryGetValue(c.Node.Coord, out int idx)) {
                var ex = stops[idx];
                stops[idx] = new RouteStop {
                    Node = ex.Node,
                    MatchedContent = MergeContent(ex.MatchedContent, c.MatchedContent),
                    CumulativeSteps = ex.CumulativeSteps,
                    Order = ex.Order,
                };
                continue;
            }
            stopByCoord[c.Node.Coord] = stops.Count;
            stops.Add(new RouteStop { Node = c.Node, MatchedContent = c.MatchedContent, CumulativeSteps = cum, Order = stops.Count + 1 });
            stopMatches.Add(c);
        }

        return new FinderResult {
            Matches = stopMatches,
            Target = stops[0].Node,
            Path = fullPath,
            Steps = cum,
            TargetContent = stops[0].MatchedContent,
            PresetIndex = q.Index,
            PresetName = q.Name,
            Color = q.Color,
            NearOptionals = near ?? new(),
            Bonus = bonus,
            Stops = stops,
            Warnings = warnings,
        };
    }

    // Combines two matched-content labels for a map that satisfies more than one mandatory requirement.
    private static string MergeContent(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b;
        if (string.IsNullOrEmpty(b)) return a;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return a;
        return $"{a} + {b}";
    }

    // Exact shortest route visiting one candidate from each group, via a Held-Karp DP over the group set.
    // State dp[mask, j] = least hops from the frontier visiting exactly the groups in mask and ending at
    // candidate j (whose group is mask's most-recently-added bit). Returns the candidate indices in order.
    private static List<int> SolveHeldKarp(int C, int m, List<int> candGroup, Func<int, int> start, Func<int, int, int> hop, int INF)
    {
        int full = (1 << m) - 1;
        var dp = new int[1 << m, C];
        var par = new int[1 << m, C];
        for (int mask = 0; mask <= full; mask++)
            for (int j = 0; j < C; j++) { dp[mask, j] = INF; par[mask, j] = -1; }

        for (int j = 0; j < C; j++) {
            int gm = 1 << candGroup[j];
            int s = start(j);
            if (s < dp[gm, j]) { dp[gm, j] = s; par[gm, j] = -1; }
        }

        for (int mask = 1; mask <= full; mask++) {
            for (int j = 0; j < C; j++) {
                int dcur = dp[mask, j];
                if (dcur >= INF)
                    continue;
                if ((mask & (1 << candGroup[j])) == 0)
                    continue;
                for (int j2 = 0; j2 < C; j2++) {
                    int g2 = candGroup[j2];
                    if ((mask & (1 << g2)) != 0)
                        continue;
                    int h = hop(j, j2);
                    if (h >= INF)
                        continue;
                    int nmask = mask | (1 << g2);
                    int nd = dcur + h;
                    if (nd < dp[nmask, j2]) { dp[nmask, j2] = nd; par[nmask, j2] = j; }
                }
            }
        }

        int bestJ = -1, best = INF;
        for (int j = 0; j < C; j++)
            if (dp[full, j] < best) { best = dp[full, j]; bestJ = j; }
        if (bestJ < 0)
            return null;

        var order = new List<int>();
        int curMask = full, cur = bestJ, guard = 0;
        while (cur != -1) {
            order.Add(cur);
            int p = par[curMask, cur];
            curMask ^= (1 << candGroup[cur]);
            cur = p;
            if (++guard > 1000)
                break;
        }
        order.Reverse();
        return order;
    }

    // Greedy fallback for many groups: from the frontier, repeatedly hop to the nearest candidate of a group
    // not yet visited. Not optimal but always finite (all reachable candidates share one component).
    private static List<int> SolveGreedyRoute(int C, int m, List<int> candGroup, Func<int, int> start, Func<int, int, int> hop, int INF)
    {
        var order = new List<int>();
        var usedGroups = new HashSet<int>();
        int current = -1;

        while (usedGroups.Count < m) {
            int bestJ = -1, best = INF;
            for (int j = 0; j < C; j++) {
                if (usedGroups.Contains(candGroup[j]))
                    continue;
                int d = current < 0 ? start(j) : hop(current, j);
                if (d < best) { best = d; bestJ = j; }
            }
            if (bestJ < 0)
                break;
            order.Add(bestJ);
            usedGroups.Add(candGroup[bestJ]);
            current = bestJ;
        }
        return order;
    }

    // Local search nudge: for each stop, try replacing its instance with another candidate of the same group
    // and keep the swap when the route's effective cost (raw hops minus optional bonus) drops. Order fixed.
    private static List<int> ApplyOptionalNudge(List<int> order, List<int> candGroup, List<FinderMatch> cand, Dictionary<Vector2i, Vector2i>[] candPrev, Dictionary<Vector2i, Vector2i> frontierPrev, Dictionary<Vector2i, GraphNode> nodeByCoord, Dictionary<string, DistanceField> fields, List<OptDef> opts, int radius, int maxExtra, Func<int, int> start, Func<int, int, int> hop, int INF)
    {
        if (opts == null || opts.Count == 0 || order.Count == 0)
            return order;

        var byGroup = new Dictionary<int, List<int>>();
        for (int j = 0; j < cand.Count; j++) {
            if (!byGroup.TryGetValue(candGroup[j], out var l))
                byGroup[candGroup[j]] = l = new List<int>();
            l.Add(j);
        }

        float Effective(List<int> seq) {
            int raw = RouteRawHops(seq, start, hop, INF);
            if (raw >= INF)
                return INF;
            var path = BuildRoutePath(seq, cand, candPrev, frontierPrev, nodeByCoord);
            ScoreOptionalsForPath(path, opts, fields, nodeByCoord, radius, maxExtra, out float bonus, out _);
            return raw - bonus;
        }

        var best = new List<int>(order);
        float bestEff = Effective(best);

        for (int pass = 0; pass < 3; pass++) {
            bool improved = false;
            for (int p = 0; p < best.Count; p++) {
                if (!byGroup.TryGetValue(candGroup[best[p]], out var alts))
                    continue;
                foreach (int alt in alts) {
                    if (alt == best[p])
                        continue;
                    var trial = new List<int>(best);
                    trial[p] = alt;
                    float eff = Effective(trial);
                    if (eff < bestEff - 0.001f) {
                        best = trial;
                        bestEff = eff;
                        improved = true;
                    }
                }
            }
            if (!improved)
                break;
        }
        return best;
    }

    // Total raw hops of an ordered candidate sequence: frontier -> first, then stop to stop. INF if a leg is
    // unreachable (should not happen for same-component candidates, but guarded).
    private static int RouteRawHops(List<int> seq, Func<int, int> start, Func<int, int, int> hop, int INF)
    {
        if (seq.Count == 0)
            return 0;
        int total = start(seq[0]);
        for (int p = 0; p < seq.Count - 1; p++) {
            int h = hop(seq[p], seq[p + 1]);
            if (h >= INF)
                return INF;
            total += h;
        }
        return total;
    }

    // Concatenates the route's legs into one node path: frontier -> first stop (via the shared frontier BFS),
    // then each stop -> the next (via the FROM stop's own BFS). The duplicated junction node is dropped.
    private static List<GraphNode> BuildRoutePath(List<int> order, List<FinderMatch> cand, Dictionary<Vector2i, Vector2i>[] candPrev, Dictionary<Vector2i, Vector2i> frontierPrev, Dictionary<Vector2i, GraphNode> nodeByCoord)
    {
        var path = new List<GraphNode>();
        if (order.Count == 0)
            return path;

        // Drops a node that repeats the previous one - the duplicated junction between two legs, or a 0-hop
        // leg between two co-located stops (one map satisfying two mandatories).
        void AddNode(GraphNode n) {
            if (n == null)
                return;
            if (path.Count > 0 && path[^1] != null && path[^1].Coord == n.Coord)
                return;
            path.Add(n);
        }

        foreach (var n in ReconstructFinderPath(cand[order[0]].Node.Coord, frontierPrev, nodeByCoord))
            AddNode(n);

        for (int p = 0; p < order.Count - 1; p++) {
            var leg = ReconstructFinderPath(cand[order[p + 1]].Node.Coord, candPrev[order[p]], nodeByCoord);
            foreach (var n in leg)
                AddNode(n);
        }
        return path;
    }

    // Single-source BFS over the accumulated graph, returning hop distances and parent pointers from src.
    private static (Dictionary<Vector2i, int> dist, Dictionary<Vector2i, Vector2i> prev) BfsFrom(Vector2i src, Dictionary<Vector2i, HashSet<Vector2i>> adjacency, Dictionary<Vector2i, GraphNode> nodeByCoord)
    {
        var dist = new Dictionary<Vector2i, int>();
        var prev = new Dictionary<Vector2i, Vector2i>();
        if (!nodeByCoord.ContainsKey(src))
            return (dist, prev);

        var queue = new Queue<Vector2i>();
        dist[src] = 0;
        queue.Enqueue(src);
        while (queue.Count > 0) {
            var coord = queue.Dequeue();
            int d = dist[coord];
            if (!adjacency.TryGetValue(coord, out var neighbors))
                continue;
            foreach (var next in neighbors) {
                if (next == default || !nodeByCoord.ContainsKey(next))
                    continue;
                if (!dist.TryAdd(next, d + 1))
                    continue;
                prev[next] = coord;
                queue.Enqueue(next);
            }
        }
        return (dist, prev);
    }

    // Scores a single candidate: reconstructs its route from the frontier, then scores the optionals along it.
    private static void ScoreCandidate(FinderMatch m, List<OptDef> opts, Dictionary<string, DistanceField> fields, Dictionary<Vector2i, Vector2i> prev, Dictionary<Vector2i, GraphNode> nodeByCoord, int radius, int maxExtra, out float bonus, out List<NearOptional> near)
    {
        var path = ReconstructFinderPath(m.Node.Coord, prev, nodeByCoord);
        ScoreOptionalsForPath(path, opts, fields, nodeByCoord, radius, maxExtra, out bonus, out near);
    }

    // Scores a concrete route path against the optionals and, for every optional, records an outcome (match
    // count, gap, in/out of range, nearest instance, detour) so the result can be both drawn and explained.
    // The bonus sums each in-range optional's linearly-decaying reward, capped by the extra-hop budget.
    // Shared by the single-target and multi-stop planners.
    private static void ScoreOptionalsForPath(List<GraphNode> path, List<OptDef> opts, Dictionary<string, DistanceField> fields, Dictionary<Vector2i, GraphNode> nodeByCoord, int radius, int maxExtra, out float bonus, out List<NearOptional> near)
    {
        bonus = 0f;
        near = new List<NearOptional>();
        if (opts == null || opts.Count == 0)
            return;

        foreach (var opt in opts) {
            fields.TryGetValue(opt.Key, out var field);
            int matches = field?.SeedCount ?? 0;

            int bestGap = int.MaxValue;
            Vector2i bestNode = default;
            bool found = false;
            if (field != null && path != null) {
                foreach (var pn in path) {
                    if (pn != null && field.Dist.TryGetValue(pn.Coord, out int g) && g < bestGap) {
                        bestGap = g;
                        bestNode = pn.Coord;
                        found = true;
                        if (g == 0)
                            break;
                    }
                }
            }

            int gap = found ? bestGap : -1;
            bool inRange = found && bestGap < radius;
            if (inRange)
                bonus += opt.Bonus * (1f - bestGap / (float)radius);

            GraphNode instNode = null;
            List<GraphNode> detour = new();
            if (found) {
                detour = BuildDetourPath(bestNode, field, nodeByCoord);
                Vector2i inst = field.Source.TryGetValue(bestNode, out var src) ? src : bestNode;
                nodeByCoord.TryGetValue(inst, out instNode);
            }

            near.Add(new NearOptional { Label = opt.Label, Matches = matches, Gap = gap, InRange = inRange, Node = instNode, Detour = detour });
        }

        if (bonus > maxExtra)
            bonus = maxExtra;
    }

    // Walks the optional's distance field from the branch point on the route back to the nearest optional
    // instance, yielding the hop-by-hop detour (branch first, optional last) for the on-atlas preview.
    private static List<GraphNode> BuildDetourPath(Vector2i branch, DistanceField field, Dictionary<Vector2i, GraphNode> nodeByCoord)
    {
        var path = new List<GraphNode>();
        var current = branch;
        int guard = 0;
        while (true) {
            if (nodeByCoord.TryGetValue(current, out var node))
                path.Add(node);
            if (field.Parent == null || !field.Parent.TryGetValue(current, out var next))
                break;   // reached a seed (the optional instance)
            current = next;
            if (++guard > 100000)
                break;
        }
        return path;
    }

    // Strict total order so the recompute never flips between equally-scored candidates: by effective
    // steps, then raw steps, then name, then coordinate.
    private static int CompareScored(Scored a, Scored b)
    {
        int c = a.Effective.CompareTo(b.Effective);
        if (c != 0) return c;
        c = a.Match.Steps.CompareTo(b.Match.Steps);
        if (c != 0) return c;
        c = string.Compare(a.Match.Node.Name ?? "", b.Match.Node.Name ?? "", StringComparison.Ordinal);
        if (c != 0) return c;
        c = a.Match.Node.Coord.X.CompareTo(b.Match.Node.Coord.X);
        if (c != 0) return c;
        return a.Match.Node.Coord.Y.CompareTo(b.Match.Node.Coord.Y);
    }

    // A node matches when every active filter passes: the map name (substring) and/or the chosen
    // content/mod (exact match, ignoring case and spacing so "corruptednexus" == "Corrupted Nexus" but
    // never "Corrupted"). With no filter active nothing matches.
    private static bool MatchesFilters(GraphNode node, string nameQuery, string contentFilter, out string matchedContent)
    {
        matchedContent = null;

        bool hasName = !string.IsNullOrEmpty(nameQuery);
        bool hasContent = !string.IsNullOrEmpty(contentFilter);
        if (!hasName && !hasContent)
            return false;

        if (hasName && (node.Name == null || !node.Name.Contains(nameQuery, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (hasContent) {
            string want = NormalizeForMatch(contentFilter);
            string hit = node.ContentTags?.FirstOrDefault(t => NormalizeForMatch(t) == want);
            if (hit == null)
                return false;
            matchedContent = hit;
        }

        return true;
    }

    // Dropdown options: a leading "" (Any), then the well-known content types merged with whatever the
    // current atlas exposes, de-duplicated by normalized form and sorted for a stable list.
    private List<string> BuildContentOptions()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var union = new List<string>();

        void Add(string value) {
            if (string.IsNullOrWhiteSpace(value))
                return;
            string key = NormalizeForMatch(value);
            if (key.Length == 0 || !seen.Add(key))
                return;
            union.Add(value);
        }

        foreach (var known in KnownContentTypes)
            Add(known);
        foreach (var tag in availableContentTags)
            Add(tag);

        union.Sort(StringComparer.OrdinalIgnoreCase);

        var options = new List<string> { "" };   // "" represents "Any content / mod"
        options.AddRange(union);
        return options;
    }

    // Reads the content / map mods shown on a node: structured content identities (the icons the game
    // draws, e.g. "PowerfulMapBoss"), a few content types only told apart by their overlay texture, and
    // the tower marker. Every read is guarded so a node simply contributes fewer tags on failure.
    private static List<string> CollectContentTags(AtlasPanelNode el)
    {
        var tags = new List<string>();

        void Add(string tag) {
            if (!string.IsNullOrWhiteSpace(tag) && !tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                tags.Add(tag);
        }

        try {
            var identities = el.ContentIdentity;
            if (identities != null) {
                foreach (var identity in identities) {
                    string id = null;
                    try { id = identity?.Id; } catch { }
                    Add(ResolveContentId(id));
                }
            }
        } catch { }

        // Granular per-map content variant - the specific modifier name the game assigns (e.g. "Zealous
        // Reverence", "Spirit Migration"), so the finder can search an exact mod, not just the broad type.
        try {
            string variant = el.AtlasEntry?.MapContent?.Name;
            if (IsPlayerFacingContent(variant))
                Add(variant.Trim());
        } catch { }

        try {
            var overlay = el.GetChildAtIndex(0)?.GetChildAtIndex(0)?.Children;
            if (overlay != null) {
                bool HasTexture(string fragment) =>
                    overlay.Any(c => c?.TextureName?.Contains(fragment, StringComparison.OrdinalIgnoreCase) == true);

                if (HasTexture("CorruptionNexus")) Add("Corrupted Nexus");
                else if (HasTexture("Corrupt")) Add("Corrupted");
                if (HasTexture("Sanctification")) Add("Cleansed");
                if (HasTexture("UniqueMap")) Add("Unique Map");
                if (HasTexture("MapBossSpecial")) Add("Anomaly Map Boss");
                if (HasTexture("ContentMapBoss.dds")) Add("Map Boss");
            }
        } catch { }

        try {
            if (el.Height == 110)   // tower nodes are taller than map nodes
                Add("Tower");
        } catch { }

        return tags;
    }

    // Humanizes a content identity id, first applying any alias so an internal name maps to the label
    // players (and the dropdown) actually use - e.g. the "Sanctified" id becomes "Cleansed".
    private static string ResolveContentId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        return ContentIdAliases.TryGetValue(id.Trim(), out var alias) ? alias : HumanizeContentId(id);
    }

    // True when a map-content name is a real, player-visible variant rather than an internal placeholder
    // (the game tags some content "[DNT] ... Not Shown to Players").
    private static bool IsPlayerFacingContent(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        if (name.StartsWith("[DNT]", StringComparison.OrdinalIgnoreCase))
            return false;
        if (name.Contains("Not Shown", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    // "PowerfulMapBoss" / "AZMERI_Energisation" -> "Powerful Map Boss" / "Azmeri Energisation".
    private static string HumanizeContentId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        string value = id.Replace("_", " ").Replace("-", " ").Trim();
        value = Regex.Replace(value, @"([A-Z]+)([A-Z][a-z])", "$1 $2");
        value = Regex.Replace(value, @"([a-z0-9])([A-Z])", "$1 $2");
        value = Regex.Replace(value, @"\s+", " ").Trim();
        return value.Length == 0 ? null : value;
    }

    // Strips case, spaces and punctuation so queries match content tags regardless of formatting.
    private static string NormalizeForMatch(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static List<GraphNode> ReconstructFinderPath(Vector2i targetCoord, Dictionary<Vector2i, Vector2i> prev, Dictionary<Vector2i, GraphNode> nodeByCoord)
    {
        var path = new List<GraphNode>();
        var current = targetCoord;
        int guard = 0;

        while (true) {
            if (!nodeByCoord.TryGetValue(current, out GraphNode node))
                break;
            path.Add(node);

            if (!prev.TryGetValue(current, out Vector2i parent))
                break;          // reached a seed node
            current = parent;

            if (++guard > 100000) // guard against an unexpected cycle
                break;
        }

        path.Reverse();
        return path;
    }

    #endregion

    #region Persistence

    // Characters illegal in a filename, mapped to '_' so a (league, character) key is always a valid name.
    private static readonly char[] InvalidFileChars = Path.GetInvalidFileNameChars();

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "_";
        var sb = new StringBuilder(value.Length);
        foreach (char c in value.Trim())
            sb.Append(Array.IndexOf(InvalidFileChars, c) >= 0 ? '_' : c);
        string s = sb.ToString();
        return s.Length == 0 ? "_" : s;
    }

    private string AtlasDir => Path.Combine(ConfigDirectory, "atlas");
    private string GetGraphPath(string key) => Path.Combine(AtlasDir, key + ".json");

    // Creates the save folder once and probes that it is writable; on failure disables persistence for
    // the session instead of throwing on every scan. Worker-only.
    private bool EnsurePersistDir()
    {
        if (persistDisabled)
            return false;
        if (persistDirReady)
            return true;
        try {
            Directory.CreateDirectory(AtlasDir);
            persistDirReady = true;
            return true;
        } catch (Exception e) {
            persistDisabled = true;
            LogError("OptiPather: atlas memory disabled - cannot create " + AtlasDir + ": " + e.Message);
            return false;
        }
    }

    // Worker-only. Writes the graph when a checkpoint flush is pending or the debounce has elapsed and
    // something changed. Always resolves a pending flush so the request cannot get stuck set.
    private void PersistIfDue()
    {
        if (!Settings.PersistAtlas || persistDisabled || loadedIdentityKey == null)
            return;
        if (finderClearCacheRequested)
            return;   // a wipe is pending - don't write the graph that is about to be discarded
        bool flush = persistFlushRequested;
        bool due = flush || (persistDirty && (DateTime.Now - lastPersistSave).TotalSeconds > SaveDebounceSeconds);
        if (due && persistDirty)
            SaveSnapshot(loadedIdentityKey);
        if (flush)
            persistFlushRequested = false;
    }

    // Worker-only. Switches the in-memory cache to the given character: saves the outgoing graph if it has
    // unsaved changes, then replaces the cache with that character's saved graph. If an existing file could
    // not be read this pass (a concurrent write from another instance, or transient IO), the switch is NOT
    // committed and nothing is wiped - it retries next scan, so good data is never overwritten with an empty
    // graph after a failed read.
    private void EnsureCacheForIdentity(string currentKey)
    {
        if (loadedIdentityKey != null && persistDirty)
            SaveSnapshot(loadedIdentityKey);

        if (!TryLoadSnapshot(currentKey, out var nodes, out var adjacency, out var age))
            return;   // existing file unreadable this pass - keep current state, retry next scan

        cachedNodes.Clear();
        foreach (var kv in nodes)
            cachedNodes[kv.Key] = kv.Value;
        cachedAdjacency.Clear();
        foreach (var kv in adjacency)
            cachedAdjacency[kv.Key] = kv.Value;

        persistDirty = false;
        swapSignalStreak = 0;
        cacheRememberedAge = age;
        loadedIdentityKey = currentKey;
        cachedNodeCount = cachedNodes.Count;
        lastPersistSave = DateTime.Now;   // freshly in sync with disk - don't immediately re-save
        lastCacheEvent = cachedNodes.Count > 0
            ? $"restored {cachedNodes.Count} maps from disk" + (age != null ? $" ({age})" : "")
            : null;
    }

    // Worker-only. Builds the saved graph into fresh dictionaries (never mutating the live cache) and
    // validates it. Returns false only when an EXISTING file could not be read this pass (lock held by
    // another instance, or an IO/parse exception) - the caller must then keep the current cache and retry,
    // never overwrite. Returns true (with empty maps) when there is simply no file or the file is invalid
    // and should be discarded. Active is never trusted from disk; SeenLive starts false so loaded state is
    // reconfirmed by the live scan before it seeds the frontier.
    private bool TryLoadSnapshot(string key, out Dictionary<Vector2i, GraphNode> nodes, out Dictionary<Vector2i, HashSet<Vector2i>> adjacency, out string age)
    {
        nodes = new Dictionary<Vector2i, GraphNode>();
        adjacency = new Dictionary<Vector2i, HashSet<Vector2i>>();
        age = null;

        if (!EnsurePersistDir())
            return true;   // persistence unavailable - proceed with an empty cache
        string path = GetGraphPath(key);
        if (!File.Exists(path))
            return true;   // no remembered graph yet - start empty, fine to save later

        FileStream lockStream = null;
        try {
            try {
                lockStream = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            } catch {
                return false;   // another instance is writing this file - retry, don't overwrite it
            }

            GraphSnapshot snap;
            using (var sr = new StreamReader(path))
            using (var jr = new Newtonsoft.Json.JsonTextReader(sr)) {
                snap = Newtonsoft.Json.JsonSerializer.CreateDefault().Deserialize<GraphSnapshot>(jr);
            }

            if (snap == null || snap.SchemaVersion != GraphSchemaVersion
                || !string.Equals(snap.IdentityKey, key, StringComparison.Ordinal)
                || snap.Nodes == null || snap.Nodes.Count == 0 || snap.Nodes.Count > MaxPersistNodes)
                return true;   // missing / different format / foreign / implausible - discard and rebuild

            var loadedNodes = new Dictionary<Vector2i, GraphNode>(snap.Nodes.Count);
            foreach (var nd in snap.Nodes) {
                if (nd == null)
                    continue;
                var coord = new Vector2i(nd.X, nd.Y);
                loadedNodes[coord] = new GraphNode {
                    Coord = coord,
                    Name = nd.Name,
                    Visited = nd.V,
                    Unlocked = nd.U,
                    Active = false,
                    ContentTags = nd.T ?? new List<string>(),
                    SeenLive = false,
                };
            }

            var loadedAdj = new Dictionary<Vector2i, HashSet<Vector2i>>(loadedNodes.Count);
            void AddEdge(Vector2i a, Vector2i b) {
                if (!loadedNodes.ContainsKey(a) || !loadedNodes.ContainsKey(b))
                    return;   // drop a dangling edge rather than referencing a missing node
                if (!loadedAdj.TryGetValue(a, out var set))
                    loadedAdj[a] = set = new HashSet<Vector2i>();
                set.Add(b);
            }
            if (snap.Edges != null)
                foreach (var ed in snap.Edges) {
                    if (ed == null)
                        continue;
                    var a = new Vector2i(ed.AX, ed.AY);
                    var b = new Vector2i(ed.BX, ed.BY);
                    AddEdge(a, b);
                    AddEdge(b, a);
                }

            nodes = loadedNodes;
            adjacency = loadedAdj;
            age = DescribeAge(snap.SavedUtc);
            return true;
        } catch (Exception e) {
            LogError("OptiPather: could not read remembered atlas " + path + ": " + e.Message);
            nodes = new Dictionary<Vector2i, GraphNode>();
            adjacency = new Dictionary<Vector2i, HashSet<Vector2i>>();
            return false;   // existed but unreadable this pass - keep current cache, retry next scan
        } finally {
            try { lockStream?.Dispose(); } catch { }
        }
    }

    // Worker-only. Serializes the current cache to a unique temp file and atomically renames it onto the
    // final path, behind a per-file lock so two overlapping instances (hot-reload) never corrupt it.
    private void SaveSnapshot(string key)
    {
        if (string.IsNullOrEmpty(key) || !EnsurePersistDir())
            return;

        string path = GetGraphPath(key);
        string lockPath = path + ".lock";
        string tmpPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        FileStream lockStream = null;
        try {
            try {
                lockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            } catch {
                return;   // another instance owns this file this pass - skip rather than race
            }

            CleanOrphanTemps(path);

            var snap = new GraphSnapshot {
                SchemaVersion = GraphSchemaVersion,
                IdentityKey = key,
                SavedUtc = DateTime.UtcNow,
                NodeCount = cachedNodes.Count,
                Nodes = new List<NodeDto>(cachedNodes.Count),
                Edges = new List<EdgeDto>(cachedNodes.Count),
            };
            foreach (var kv in cachedNodes) {
                var n = kv.Value;
                snap.Nodes.Add(new NodeDto {
                    X = n.Coord.X,
                    Y = n.Coord.Y,
                    Name = n.Name,
                    V = n.Visited,
                    U = n.Unlocked,
                    T = n.ContentTags is { Count: > 0 } ? n.ContentTags : null,
                });
            }
            foreach (var kv in cachedAdjacency) {
                var a = kv.Key;
                foreach (var b in kv.Value)
                    if (a.X < b.X || (a.X == b.X && a.Y < b.Y))   // one direction per undirected pair
                        snap.Edges.Add(new EdgeDto { AX = a.X, AY = a.Y, BX = b.X, BY = b.Y });
            }

            using (var sw = new StreamWriter(tmpPath, false))
            using (var jw = new Newtonsoft.Json.JsonTextWriter(sw)) {
                Newtonsoft.Json.JsonSerializer.CreateDefault().Serialize(jw, snap);
            }

            try {
                File.Move(tmpPath, path, true);
            } catch {
                if (File.Exists(path))
                    File.Replace(tmpPath, path, path + ".bak");
                else
                    File.Move(tmpPath, path);
            }

            lastPersistSave = DateTime.Now;
            persistDirty = false;
            persistFlushRequested = false;
            cacheRememberedAge = DescribeAge(snap.SavedUtc);
        } catch (Exception e) {
            LogError("OptiPather: could not save remembered atlas " + path + ": " + e.Message);
            // leave persistDirty set so the next pass retries
        } finally {
            try { lockStream?.Dispose(); } catch { }
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
        }
    }

    // Worker-only. Removes the saved file for a character (manual Rescan, or a confirmed swap), keeping a
    // single .bak so an accidental forget is recoverable.
    private void ForgetSnapshotFiles(string key)
    {
        if (string.IsNullOrEmpty(key) || !EnsurePersistDir())
            return;
        string path = GetGraphPath(key);
        try {
            if (File.Exists(path))
                File.Move(path, path + ".bak", true);
        } catch {
            try { File.Delete(path); } catch { }
        }
        CleanOrphanTemps(path);
        cacheRememberedAge = null;
    }

    private void CleanOrphanTemps(string finalPath)
    {
        try {
            string dir = Path.GetDirectoryName(finalPath);
            string name = Path.GetFileName(finalPath);
            if (dir == null)
                return;
            foreach (var f in Directory.EnumerateFiles(dir, name + ".*.tmp"))
                try { File.Delete(f); } catch { }
        } catch { }
    }

    private static string DescribeAge(DateTime savedUtc)
    {
        if (savedUtc == default)
            return null;
        var span = DateTime.UtcNow - savedUtc;
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return (int)span.TotalMinutes + "m ago";
        if (span.TotalHours < 24) return (int)span.TotalHours + "h ago";
        return (int)span.TotalDays + "d ago";
    }

    #endregion

    #region Rendering

    private void DrawMapFinderRoutes()
    {
        if (!Settings.ShowOnAtlas)
            return;

        var results = mapFinderResults;
        if (results == null || results.Count == 0)
            return;

        var presets = Settings.Presets;

        // Only checked (active) searches draw on the atlas. The selected-but-unchecked search still shows its
        // results in the panel, but ticking the checkbox is the single control over what appears on the map.
        var drawList = new List<FinderResult>();
        foreach (var r in results) {
            if (r?.Target == null || r.Target.Visited)
                continue;
            if (presets != null && r.PresetIndex >= 0 && r.PresetIndex < presets.Count && presets[r.PresetIndex].Active)
                drawList.Add(r);
        }
        if (drawList.Count == 0)
            return;

        // Soft cap, keeping the edited search and then the nearest targets.
        drawList.Sort((a, b) => {
            bool aSel = a.PresetIndex == selectedPresetIndex, bSel = b.PresetIndex == selectedPresetIndex;
            if (aSel != bSel) return aSel ? -1 : 1;
            int sa = a.Steps < 0 ? int.MaxValue : a.Steps;
            int sb = b.Steps < 0 ? int.MaxValue : b.Steps;
            return sa.CompareTo(sb);
        });
        int cap = Math.Max(1, Settings.MaxRoutesOnScreen.Value);
        if (drawList.Count > cap)
            drawList = drawList.GetRange(0, cap);

        // Resolve current screen rects by coordinate once; the search ran against a copy, positions are live.
        Dictionary<Vector2i, AtlasNodeDescription> descByCoord;
        try {
            var descs = AtlasPanel.Descriptions;
            descByCoord = new Dictionary<Vector2i, AtlasNodeDescription>(descs.Count);
            foreach (var d in descs)
                descByCoord[d.Coordinate] = d;
        } catch {
            return;
        }

        // Fit the atlas coord->screen transform from the on-screen nodes whenever any piece of a drawn
        // route has no live element to anchor to. The game rebuilds the panel's element list around the
        // camera on every instance reload, so right after a map run most of a remembered route has no
        // elements until the player pans back over it - the transform fills that gap by projecting the
        // remembered coordinates instead, keeping the route visible. Deciding whether the fit is needed
        // costs dictionary lookups only (a live element exists iff the coord is in descByCoord); the fit
        // itself stays capped at 3*FitGridDim^2 rect reads. In the common case everything is live and the
        // fit is skipped entirely.
        AtlasTransform transform = default;   // Valid == false
        bool needFit = false;
        foreach (var r in drawList) {
            if (r.Target != null && !descByCoord.ContainsKey(r.Target.Coord))
                needFit = true;
            if (!needFit && Settings.ShowPath && r.Path != null)
                foreach (var pn in r.Path)
                    if (pn != null && !descByCoord.ContainsKey(pn.Coord)) { needFit = true; break; }
            if (!needFit && r.Stops != null)
                foreach (var s in r.Stops)
                    if (s?.Node != null && !descByCoord.ContainsKey(s.Node.Coord)) { needFit = true; break; }
            if (!needFit && r.NearOptionals != null)
                foreach (var no in r.NearOptionals)
                    if (no?.Node != null && !descByCoord.ContainsKey(no.Node.Coord)) { needFit = true; break; }
            if (needFit)
                break;
        }
        if (needFit && (DateTime.UtcNow - fitCacheAt).TotalSeconds >= FitReuseSeconds) {
            // Refits pause whenever every route piece is live, so a gap since the last one starts a
            // new episode - rejections from the previous one are not "consecutive" with this one.
            if ((DateTime.UtcNow - fitCacheAt).TotalSeconds >= 2 * FitReuseSeconds)
                hardRejectStreak = 0;
            var fresh = FitAtlasTransform(descByCoord);
            fitCacheAt = DateTime.UtcNow;
            if (fresh.Valid) {
                heldFit = fresh;
                heldFitSet = true;
                heldAnchorCoord = fitAnchorCoord;
                heldAnchorScreen = fitAnchorScreen;
                hardRejectStreak = 0;
            } else if (fitRejectedMarginally) {
                // Gate noise, not a broken mapping - keep drawing with the held fit.
                hardRejectStreak = 0;
            } else if (heldFitSet && ++hardRejectStreak >= HardRejectsToBlank) {
                heldFitSet = false;
            }
        }
        // The held fit pan-follows every frame, so estimated pieces track the camera between solves.
        // It is offered even when every route coord has an element entry: a repopulating element can
        // read a zeroed rect for a frame or two, and the draw path then needs a projection to keep
        // that piece from blinking off. Outside a needFit episode no refits run to keep the held fit
        // current, so there it is only trusted when the anchor confirms it matches the camera.
        bool panAnchored = false;
        if (heldFitSet) {
            var followed = PanFollow(heldFit, descByCoord, out panAnchored);
            if (needFit || panAnchored)
                transform = followed;
        }

        bool labelWithName = drawList.Count > 1;
        foreach (var route in drawList) {
            try {
                DrawSingleRoute(route, descByCoord, transform, labelWithName);
            } catch (Exception e) {
                LogError($"Error drawing route '{route.PresetName}': {e.Message}\n{e.StackTrace}");
            }
        }
    }

    private void DrawSingleRoute(FinderResult route, Dictionary<Vector2i, AtlasNodeDescription> descByCoord, AtlasTransform transform, bool labelWithName)
    {
        var target = route.Target;
        if (target == null || target.Visited)
            return;

        Color color = route.Color;
        string prefix = labelWithName && !string.IsNullOrEmpty(route.PresetName) ? $"[{route.PresetName}] " : "";
        bool multi = route.Stops != null && route.Stops.Count > 0;

        // Path line - the full itinerary for a multi-stop route. A node without a live element (the game
        // only keeps elements near the camera, and drops the rest on every instance reload) is anchored by
        // projecting its coordinate through the fitted transform; those segments draw slightly faded so
        // live and remembered geometry stay tellable apart. Segments fully off screen are skipped.
        if (Settings.ShowPath && route.Path is { Count: > 1 }) {
            for (int i = 0; i < route.Path.Count - 1; i++) {
                var a = route.Path[i];
                var b = route.Path[i + 1];
                if (a == null || b == null)
                    continue;
                if (!TryNodeScreenPos(a, descByCoord, transform, out Vector2 start, out _, out bool estA)
                    || !TryNodeScreenPos(b, descByCoord, transform, out Vector2 end, out _, out bool estB))
                    continue;

                if (!IsOnScreen(start) && !IsOnScreen(end))
                    continue;

                Graphics.DrawLine(start, end, Settings.LineWidth, estA || estB ? Faded(color, 170) : color);
            }
        }

        // For each optional considered for the chosen route, show where it is and how it scored. In-range
        // optionals (which earned a bonus) draw a branch line in the route color from the best point on the
        // route out to the optional, plus a marker on that branch point; out-of-range ones draw a grey ring
        // so you can still see them and how far they are. Each carries a "gap" label.
        if (route.NearOptionals != null) {
            float detourWidth = Math.Max(1f, Settings.LineWidth - 1.5f);
            float optWidth = Math.Max(1f, Settings.RingWidth - 2f);
            foreach (var near in route.NearOptionals) {
                if (near?.Node == null || !TryNodeScreenPos(near.Node, descByCoord, transform, out Vector2 oc, out float ohalf, out bool estOpt))
                    continue;
                Color drawColor = near.InRange ? Color.FromArgb(165, color) : Color.FromArgb(110, 150, 150, 150);
                if (estOpt)
                    drawColor = Faded(drawColor, 120);

                if (near.InRange && near.Detour is { Count: > 1 }) {
                    for (int i = 0; i < near.Detour.Count - 1; i++) {
                        if (!TryNodeScreenPos(near.Detour[i], descByCoord, transform, out Vector2 ds, out _, out _)
                            || !TryNodeScreenPos(near.Detour[i + 1], descByCoord, transform, out Vector2 de, out _, out _))
                            continue;
                        if (!IsOnScreen(ds) && !IsOnScreen(de))
                            continue;
                        Graphics.DrawLine(ds, de, detourWidth, drawColor);
                    }

                    // Marker on the branch point - the optimal spot to leave the route for this optional.
                    if (TryNodeScreenPos(near.Detour[0], descByCoord, transform, out Vector2 bc, out float bhalf, out _)
                        && IsOnScreen(bc)) {
                        Graphics.DrawCircle(bc, (bhalf * Settings.RingRadius) * 0.55f + 4, drawColor, optWidth, 16);
                    }
                }

                if (!IsOnScreen(oc))
                    continue;
                float orad = ohalf * Settings.RingRadius + 6;
                Graphics.DrawCircle(oc, orad, drawColor, optWidth, 24);
                string optLabel = near.InRange
                    ? $"{near.Label}  ({near.Gap} hop{(near.Gap == 1 ? "" : "s")} off route)"
                    : $"{near.Label}  ({near.Gap} hops - beyond radius)";
                DrawCenteredTextWithBackground(optLabel, oc + new Vector2(0, orad + 10), Settings.FontColor, Settings.BackgroundColor, true, 8, 3);
            }
        }

        // Rings + labels: one per stop for a multi-stop route (numbered in visiting order), otherwise the
        // single target. A node the game is not currently rendering is anchored by its projected coordinate
        // and ringed slightly faded; nodes the projection puts off screen are left to the arrow.
        if (multi) {
            foreach (var stop in route.Stops) {
                if (stop?.Node == null || stop.Node.Visited)
                    continue;
                if (!TryNodeScreenPos(stop.Node, descByCoord, transform, out Vector2 ctr, out float shalf, out bool estStop))
                    continue;
                if (!IsOnScreen(ctr))
                    continue;
                float rad = shalf * Settings.RingRadius + 10;
                bool stopUnconfirmed = !stop.Node.SeenLive;
                Color ringColor = stopUnconfirmed ? Dimmed(color) : estStop ? Faded(color, 185) : color;
                Graphics.DrawCircle(ctr, rad, ringColor, Settings.RingWidth, 32);
                string nm = stop.Node.Name ?? "(unknown)";
                string desc = stop.MatchedContent != null ? $"{nm} - {stop.MatchedContent}" : nm;
                string lbl = $"{prefix}{stop.Order}. {desc} ({stop.CumulativeSteps})";
                if (stopUnconfirmed)
                    lbl += "  (unconfirmed)";
                DrawCenteredTextWithBackground(lbl, ctr - new Vector2(0, rad + 12), Settings.FontColor, Settings.BackgroundColor, true, 10, 4);
            }
        } else if (TryNodeScreenPos(target, descByCoord, transform, out Vector2 targetCenter, out float thalf, out bool estTarget)) {
            if (IsOnScreen(targetCenter)) {
                string tName = target.Name ?? "(unknown)";
                string tDesc = route.TargetContent != null ? $"{tName} - {route.TargetContent}" : tName;
                string tLabel = route.Steps >= 0 ? $"{prefix}{tDesc} ({route.Steps} steps)" : $"{prefix}{tDesc}";
                // A target restored from the saved graph but not yet re-seen this session is drawn faint
                // and tagged, since its completed/unlocked state may be out of date until reconfirmed.
                bool unconfirmed = !target.SeenLive;
                if (unconfirmed)
                    tLabel += "  (unconfirmed)";
                float radius = thalf * Settings.RingRadius + 10;
                Color ringColor = unconfirmed ? Dimmed(color) : estTarget ? Faded(color, 185) : color;
                Graphics.DrawCircle(targetCenter, radius, ringColor, Settings.RingWidth, 32);
                DrawCenteredTextWithBackground(tLabel, targetCenter - new Vector2(0, radius + 12), Settings.FontColor, Settings.BackgroundColor, true, 10, 4);
            }
        }

        // Arrow toward the next thing to reach (the first stop for a multi-stop route). Works for off-screen
        // targets too: a live rect if rendered, else an estimate from the fitted transform, else the nearest
        // on-screen node on the route.
        if (Settings.ShowArrow) {
            if (!TryResolveScreenPos(target, route.Path, descByCoord, transform, out Vector2 tpos, out bool isLive, out bool viaFallback))
                return;
            // A target the game does not currently render still gets its arrow when the position is a
            // projected coordinate (the transform is validity-gated, so the estimate is dependable - and
            // after an instance reload it is the only indicator the route has left). ShowFarArrows now
            // gates only the nearest-on-screen-path-node fallback, which points at the route rather than
            // the target - the imprecise case it was turned off for.
            if (viaFallback && !Settings.ShowFarArrows)
                return;

            // On-screen targets only get an arrow once they are far enough from center to be worth one; an
            // estimated (off-screen) target always gets one, so it is never left with no indicator at all.
            float distance = Vector2.Distance(screenCenter, tpos);
            if (distance >= 400 || !isLive) {
                string tName = target.Name ?? "(unknown)";
                string tDesc = route.TargetContent != null ? $"{tName} - {route.TargetContent}" : tName;
                bool arrowUnconfirmed = !target.SeenLive;
                string arrowLabel = multi
                    ? $"{prefix}Next: {tDesc}  ({route.Steps} total)"
                    : (route.Steps >= 0 ? $"{prefix}{tDesc} ({route.Steps} steps)" : $"{prefix}{tDesc}");
                if (arrowUnconfirmed)
                    arrowLabel += "  (unconfirmed)";

                Vector2 arrowSize = new(64, 64);
                var windowSize = GameController.Window.GetWindowRectangleTimeCache.Size;
                Vector2 arrowPosition = tpos;
                arrowPosition.X = Math.Clamp(arrowPosition.X, 0, windowSize.X);
                arrowPosition.Y = Math.Clamp(arrowPosition.Y, 0, windowSize.Y);
                arrowPosition = Vector2.Lerp(screenCenter, arrowPosition, 0.80f);
                arrowPosition -= new Vector2(arrowSize.X / 2, arrowSize.Y / 2);

                Vector2 direction = tpos - screenCenter;
                float phi = (float)Math.Atan2(direction.Y, direction.X) + (float)(Math.PI / 2);

                Color arrowColor = arrowUnconfirmed ? Color.FromArgb(150, color)
                : isLive ? Color.FromArgb(255, color) : Color.FromArgb(195, color);
                DrawRotatedImage(arrowId, arrowPosition, arrowSize, phi, arrowColor);
                Vector2 textPosition = arrowPosition + new Vector2(arrowSize.X / 2, arrowSize.Y / 2);
                textPosition = Vector2.Lerp(textPosition, screenCenter, 0.10f);
                DrawCenteredTextWithBackground(arrowLabel, textPosition, arrowColor, Settings.BackgroundColor, true, 10, 4);
            }
        }
    }

    // Where to draw a remembered node: the center of its live element if the game currently renders one,
    // else its atlas coordinate projected through the fitted transform (estimated=true). halfWidth sizes
    // rings - half the live rect, or a fraction of the fitted pixels-per-coordinate-step when projected.
    private bool TryNodeScreenPos(GraphNode n, Dictionary<Vector2i, AtlasNodeDescription> descByCoord, AtlasTransform transform, out Vector2 pos, out float halfWidth, out bool estimated)
    {
        pos = default;
        halfWidth = 0f;
        estimated = false;
        if (n == null)
            return false;

        if (descByCoord.TryGetValue(n.Coord, out var d)) {
            try {
                var rect = d.Element.GetClientRect();
                var center = rect.Center;
                // A repopulating element can read a zeroed rect without throwing; treat that as not
                // really live (mirroring the fit's sample filter) and fall through to the projection.
                if (rect.Right > rect.Left && (center.X != 0 || center.Y != 0)) {
                    pos = center;
                    halfWidth = (rect.Right - rect.Left) / 2f;
                    return true;
                }
            } catch { }
        }

        if (!transform.Valid || !transform.TryApply(n.Coord.X, n.Coord.Y, out pos))
            return false;
        float scale = transform.LocalScaleAt(n.Coord.X, n.Coord.Y);
        halfWidth = Math.Clamp(scale * 0.28f, 6f, 40f);
        estimated = true;
        return true;
    }

    // A fitted projective map from atlas coordinates to screen pixels, recovered from the nodes near the
    // screen. The atlas plane renders with a perspective tilt, so an affine model leaves residuals of
    // hundreds of pixels across a single viewport even with clean screen-local samples - a homography
    // models the tilt exactly. Valid only when the fit is trustworthy; TryApply refuses points at or
    // beyond the fitted horizon, where a projective extrapolation stops meaning anything.
    private struct AtlasTransform
    {
        public bool Valid;
        public double H00, H01, H02;
        public double H10, H11, H12;
        public double H20, H21, H22;

        public readonly bool TryApply(double x, double y, out Vector2 pos)
        {
            pos = default;
            double w = H20 * x + H21 * y + H22;
            if (w < 1e-6)
                return false;
            double sx = (H00 * x + H01 * y + H02) / w;
            double sy = (H10 * x + H11 * y + H12) / w;
            if (Math.Abs(sx) > 1e5 || Math.Abs(sy) > 1e5)
                return false;
            pos = new Vector2((float)sx, (float)sy);
            return true;
        }

        // How many pixels one coordinate step moves on screen around (x,y) - under a projective map the
        // scale varies with position, so it is probed locally instead of read off matrix columns.
        public readonly float LocalScaleAt(double x, double y)
        {
            if (!TryApply(x, y, out var p0) || !TryApply(x + 1, y, out var px) || !TryApply(x, y + 1, out var py))
                return 0f;
            return Math.Max(Vector2.Distance(p0, px), Vector2.Distance(p0, py));
        }
    }

    // Where to draw/point for a node: its live on-screen center if the game currently renders it, else an
    // estimate from the fitted transform, else the nearest on-screen node along the route (viaFallback) -
    // reported explicitly because a Valid transform can still refuse a point beyond its fitted horizon.
    private bool TryResolveScreenPos(GraphNode node, List<GraphNode> path, Dictionary<Vector2i, AtlasNodeDescription> descByCoord, AtlasTransform transform, out Vector2 pos, out bool isLive, out bool viaFallback)
    {
        pos = default;
        isLive = false;
        viaFallback = false;
        if (node == null)
            return false;

        if (descByCoord.TryGetValue(node.Coord, out var d)) {
            try {
                var rect = d.Element.GetClientRect();
                var center = rect.Center;
                // Same zeroed-rect guard as TryNodeScreenPos: a repopulating element reads (0,0)
                // without throwing, which would otherwise pass for a live target at the screen origin
                // and point the arrow into the corner.
                if (rect.Right > rect.Left && (center.X != 0 || center.Y != 0)) {
                    pos = center;
                    isLive = true;
                    return true;
                }
            } catch { }
        }

        if (transform.Valid && transform.TryApply(node.Coord.X, node.Coord.Y, out pos))
            return true;

        if (path != null) {
            for (int i = path.Count - 1; i >= 0; i--) {
                var pn = path[i];
                if (pn == null || !descByCoord.TryGetValue(pn.Coord, out var pd))
                    continue;
                try {
                    var rect = pd.Element.GetClientRect();
                    var c = rect.Center;
                    if (rect.Right <= rect.Left || (c.X == 0 && c.Y == 0))
                        continue;
                    if (IsOnScreen(c)) { pos = c; viaFallback = true; return true; }
                } catch { }
            }
        }
        return false;
    }

    // Least-squares fit of coord->screen from the nodes near the screen, using MEAN-CENTERED coordinates:
    // this is far better conditioned than the raw normal equations and turns the singularity test into a
    // meaningful relative one. Rejected when the usable nodes span too few distinct rows/columns
    // (near-collinear, so an off-screen estimate would be unreliable) or when the in-sample residual is
    // large vs the per-coord scale.
    // Side of the square coordinate grid each sampling pass uses: 3*FitGridDim^2 is the hard cap on how
    // many GetClientRect reads one fit costs, no matter how many nodes the panel holds.
    private const int FitGridDim = 8;

    // Refitting costs up to 3*FitGridDim^2 rect reads, so a full solve runs at most this often;
    // between solves the held fit is pan-followed per frame at the cost of a single rect read.
    private const double FitReuseSeconds = 0.25;
    private DateTime fitCacheAt = DateTime.MinValue;

    // Hysteresis on the validity gate. The in-sample rms wanders around the gate second to second
    // even at a static camera (the sample mix shifts as elements repopulate), and blanking the
    // overlay on every crossing reads as flicker. The last gate-passing fit is therefore held and
    // drawn through rejections that are merely marginal; only a run of hard rejections - real
    // garbage measures an order of magnitude past the gate, not a few percent - or closing the
    // atlas drops it.
    private const double MarginalRejectFactor = 1.5;
    private const int HardRejectsToBlank = 3;
    private AtlasTransform heldFit;
    private bool heldFitSet;
    private int hardRejectStreak;
    // Set by FitAtlasTransform when its rejection was marginal (solved, but rms a hair past the gate).
    private bool fitRejectedMarginally;

    // Pan-follow anchor: one sample of the held fit whose live rect is re-read every frame; its
    // screen delta since the solve is pre-composed onto the homography as a translation, which is
    // exact under a pure pan. Estimated pieces then track the camera per frame instead of stepping
    // at the refit cadence; zoom and rotation are still corrected by the next full solve.
    private Vector2i heldAnchorCoord;
    private Vector2 heldAnchorScreen;
    // Anchor candidate from the latest solve, promoted to heldAnchor* when the fit passes the gate.
    private Vector2i fitAnchorCoord;
    private Vector2 fitAnchorScreen;

    // Re-read the held fit's anchor element and shift the homography by the anchor's screen delta
    // since the solve: H' = T(d)*H, i.e. d*row2 added onto rows 0/1. Falls back to the unshifted
    // fit when the anchor no longer resolves (panned out of the element set, or its rect went
    // stale); anchored reports whether the delta was applied, i.e. whether the returned fit is
    // confirmed to match the current camera.
    private AtlasTransform PanFollow(AtlasTransform t, Dictionary<Vector2i, AtlasNodeDescription> descByCoord, out bool anchored)
    {
        anchored = false;
        if (!descByCoord.TryGetValue(heldAnchorCoord, out var d))
            return t;
        Vector2 c;
        try {
            var rc = d.Element.GetClientRect();
            c = rc.Center;
            if (rc.Right <= rc.Left || (c.X == 0 && c.Y == 0))
                return t;
        } catch {
            return t;
        }
        double dx = c.X - heldAnchorScreen.X, dy = c.Y - heldAnchorScreen.Y;
        // A jump past a full screen diagonal is not a pan: rects far from the camera sit off the
        // viewport's projective plane, so a huge delta means the anchor's rect has degraded, not
        // that the camera moved that far between solves.
        if (dx * dx + dy * dy > (double)cachedScreenRect.Width * cachedScreenRect.Width
                              + (double)cachedScreenRect.Height * cachedScreenRect.Height)
            return t;
        anchored = true;
        if (dx == 0 && dy == 0)
            return t;
        t.H00 += dx * t.H20; t.H01 += dx * t.H21; t.H02 += dx * t.H22;
        t.H10 += dy * t.H20; t.H11 += dy * t.H21; t.H12 += dy * t.H22;
        return t;
    }

    private AtlasTransform FitAtlasTransform(Dictionary<Vector2i, AtlasNodeDescription> descByCoord)
    {
        var t = new AtlasTransform { Valid = false };
        fitRejectedMarginally = false;
        if (descByCoord.Count < 10)
            return t;

        // Descriptions spans the whole discovered atlas, and elements far from the camera read rect
        // centers tens of thousands of pixels out - positions that do not sit on the same projective
        // plane as the viewport, so feeding them to the fit drives the residual into the thousands of
        // pixels and it never passes its gate. Only anchors near the screen are trustworthy, so the
        // fit samples in up to three passes: a coarse coordinate-grid probe over the full span to find
        // which window of coordinate space is actually on screen, a dense re-bucket of just that
        // window, and - if that still starves the fit - one more bucket tight around the on-screen
        // hits. Each pass reads at most one rect per grid cell (a GetClientRect is a per-call
        // game-memory read - the reason the fit never simply reads every node).
        var screenZone = cachedScreenRect;
        screenZone.Inflate(screenZone.Width * 0.35f, screenZone.Height * 0.35f);

        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (var c in descByCoord.Keys) {
            if (c.X < minX) minX = c.X;
            if (c.X > maxX) maxX = c.X;
            if (c.Y < minY) minY = c.Y;
            if (c.Y > maxY) maxY = c.Y;
        }
        long spanX = Math.Max(1, maxX - minX), spanY = Math.Max(1, maxY - minY);

        var probedCoords = new HashSet<Vector2i>();
        var samples = new List<(double cx, double cy, double sx, double sy)>(FitGridDim * FitGridDim);
        int sMinX = int.MaxValue, sMinY = int.MaxValue, sMaxX = int.MinValue, sMaxY = int.MinValue;
        Vector2i nearestCoord = default;
        float nearestDist = float.MaxValue;
        Vector2 screenMid = new(cachedScreenRect.Width / 2f, cachedScreenRect.Height / 2f);

        void Probe(Vector2i coord, AtlasNodeDescription desc)
        {
            if (!probedCoords.Add(coord))
                return;
            Vector2 c;
            try {
                var rc = desc.Element.GetClientRect();
                c = rc.Center;
                if (rc.Right <= rc.Left || (c.X == 0 && c.Y == 0))
                    return;
            } catch {
                return;
            }
            float dist = Vector2.Distance(c, screenMid);
            if (dist < nearestDist) {
                nearestDist = dist;
                nearestCoord = coord;
            }
            if (!screenZone.Contains(c))
                return;
            samples.Add((coord.X, coord.Y, c.X, c.Y));
            if (coord.X < sMinX) sMinX = coord.X;
            if (coord.X > sMaxX) sMaxX = coord.X;
            if (coord.Y < sMinY) sMinY = coord.Y;
            if (coord.Y > sMaxY) sMaxY = coord.Y;
        }

        void BucketProbe(long bMinX, long bMaxX, long bMinY, long bMaxY)
        {
            long bSpanX = Math.Max(1, bMaxX - bMinX), bSpanY = Math.Max(1, bMaxY - bMinY);
            var bucket = new Dictionary<int, (Vector2i coord, AtlasNodeDescription desc)>(FitGridDim * FitGridDim);
            foreach (var kv in descByCoord) {
                if (kv.Key.X < bMinX || kv.Key.X > bMaxX || kv.Key.Y < bMinY || kv.Key.Y > bMaxY)
                    continue;
                if (probedCoords.Contains(kv.Key))
                    continue;
                int gx = (int)((kv.Key.X - bMinX) * FitGridDim / (bSpanX + 1));
                int gy = (int)((kv.Key.Y - bMinY) * FitGridDim / (bSpanY + 1));
                bucket.TryAdd(gy * FitGridDim + gx, (kv.Key, kv.Value));
            }
            foreach (var p in bucket.Values)
                Probe(p.coord, p.desc);
        }

        BucketProbe(minX, maxX, minY, maxY);

        // Window for the dense pass: the coordinate bbox of the pass-1 on-screen hits, padded by one
        // coarse cell so the screen edges are covered. If pass 1 found nothing on screen (its picks are
        // one arbitrary node per huge cell), seed the window from the probe that landed closest to the
        // screen center instead - it is almost always inside or beside the viewport.
        long cellX = Math.Max(1, spanX / FitGridDim), cellY = Math.Max(1, spanY / FitGridDim);
        long wMinX, wMaxX, wMinY, wMaxY;
        if (samples.Count > 0) {
            wMinX = sMinX - cellX; wMaxX = sMaxX + cellX;
            wMinY = sMinY - cellY; wMaxY = sMaxY + cellY;
        } else if (nearestDist < float.MaxValue) {
            wMinX = nearestCoord.X - 2 * cellX; wMaxX = nearestCoord.X + 2 * cellX;
            wMinY = nearestCoord.Y - 2 * cellY; wMaxY = nearestCoord.Y + 2 * cellY;
        } else {
            return t;
        }

        BucketProbe(wMinX, wMaxX, wMinY, wMaxY);

        // Both windows above are sized in atlas-span cells, which can dwarf the viewport when zoomed
        // far in - the dense pass then spreads its buckets mostly outside the screen and starves the
        // fit. When that happens, re-bucket once more around the on-screen hits themselves: by now
        // their bbox is viewport-tight, so this final grid is dense exactly where it matters.
        if (samples.Count > 0 && samples.Count < 10) {
            long padX = Math.Max(4, sMaxX - sMinX), padY = Math.Max(4, sMaxY - sMinY);
            BucketProbe(sMinX - padX, sMaxX + padX, sMinY - padY, sMaxY + padY);
        }

        if (samples.Count < 10)
            return t;

        // Self-contained solve over one sample set: conditioning guards, Hartley normalization, the
        // inhomogeneous DLT (H22 pinned to 1 in normalized space), denormalization and the horizon
        // check, plus per-sample residuals so the caller can trim outliers and re-solve. The atlas
        // plane is drawn in perspective, so screen = H*coord for a projective H; an affine fit
        // leaves the tilt itself as residual. Each sample contributes
        //   [X Y 1 0 0 0 -uX -uY] p = u   and   [0 0 0 X Y 1 -vX -vY] p = v.
        bool Solve(List<(double cx, double cy, double sx, double sy)> set,
            out AtlasTransform fit, out double[] resid, out double rms)
        {
            fit = new AtlasTransform { Valid = false };
            resid = null;
            rms = 0;
            int m = set.Count;
            double mcx = 0, mcy = 0, msx = 0, msy = 0;
            foreach (var s in set) { mcx += s.cx; mcy += s.cy; msx += s.sx; msy += s.sy; }
            mcx /= m; mcy /= m; msx /= m; msy /= m;

            // Conditioning guard on the coordinate spread, kept from the affine version: one screen
            // row/column of nodes cannot anchor a 2D map.
            double Sxx = 0, Sxy = 0, Syy = 0;
            foreach (var s in set) {
                double dx = s.cx - mcx, dy = s.cy - mcy;
                Sxx += dx * dx; Sxy += dx * dy; Syy += dy * dy;
            }
            double det2 = Sxx * Syy - Sxy * Sxy;
            if (Sxx <= 0 || Syy <= 0 || det2 <= 1e-3 * Sxx * Syy)
                return false;   // near-collinear visible nodes - extrapolating off-screen would be unreliable

            double dc = 0, dsp = 0;
            foreach (var s in set) {
                dc += Math.Sqrt((s.cx - mcx) * (s.cx - mcx) + (s.cy - mcy) * (s.cy - mcy));
                dsp += Math.Sqrt((s.sx - msx) * (s.sx - msx) + (s.sy - msy) * (s.sy - msy));
            }
            dc /= m; dsp /= m;
            if (dc < 1e-9 || dsp < 1e-9)
                return false;   // degenerate point spread
            double kc = Math.Sqrt(2) / dc, ks = Math.Sqrt(2) / dsp;

            var M = new double[8, 9];   // normal equations, column 8 = right-hand side
            var row = new double[8];
            void Accumulate(double target)
            {
                for (int i = 0; i < 8; i++) {
                    if (row[i] == 0)
                        continue;
                    for (int j = i; j < 8; j++)
                        M[i, j] += row[i] * row[j];
                    M[i, 8] += row[i] * target;
                }
            }
            foreach (var s in set) {
                double X = (s.cx - mcx) * kc, Y = (s.cy - mcy) * kc;
                double u = (s.sx - msx) * ks, v = (s.sy - msy) * ks;
                row[0] = X; row[1] = Y; row[2] = 1; row[3] = 0; row[4] = 0; row[5] = 0; row[6] = -u * X; row[7] = -u * Y;
                Accumulate(u);
                row[0] = 0; row[1] = 0; row[2] = 0; row[3] = X; row[4] = Y; row[5] = 1; row[6] = -v * X; row[7] = -v * Y;
                Accumulate(v);
            }
            for (int i = 1; i < 8; i++)
                for (int j = 0; j < i; j++)
                    M[i, j] = M[j, i];

            for (int col = 0; col < 8; col++) {
                int piv = col;
                for (int r = col + 1; r < 8; r++)
                    if (Math.Abs(M[r, col]) > Math.Abs(M[piv, col]))
                        piv = r;
                if (Math.Abs(M[piv, col]) < 1e-12)
                    return false;   // singular system
                if (piv != col)
                    for (int j = col; j < 9; j++)
                        (M[col, j], M[piv, j]) = (M[piv, j], M[col, j]);
                for (int r = col + 1; r < 8; r++) {
                    double f = M[r, col] / M[col, col];
                    for (int j = col; j < 9; j++)
                        M[r, j] -= f * M[col, j];
                }
            }
            var p = new double[8];
            for (int r = 7; r >= 0; r--) {
                double acc = M[r, 8];
                for (int j = r + 1; j < 8; j++)
                    acc -= M[r, j] * p[j];
                p[r] = acc / M[r, r];
            }

            // Denormalize: H = Tscreen^-1 * Hn * Tcoord, then orient so w is positive over the window.
            double[,] Mul3(double[,] A, double[,] B)
            {
                var R = new double[3, 3];
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                        R[i, j] = A[i, 0] * B[0, j] + A[i, 1] * B[1, j] + A[i, 2] * B[2, j];
                return R;
            }
            double[,] Hn = { { p[0], p[1], p[2] }, { p[3], p[4], p[5] }, { p[6], p[7], 1 } };
            double[,] Tc = { { kc, 0, -kc * mcx }, { 0, kc, -kc * mcy }, { 0, 0, 1 } };
            double[,] Tsi = { { 1 / ks, 0, msx }, { 0, 1 / ks, msy }, { 0, 0, 1 } };
            var H = Mul3(Tsi, Mul3(Hn, Tc));
            if (H[2, 0] * mcx + H[2, 1] * mcy + H[2, 2] < 0)
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                        H[i, j] = -H[i, j];

            fit.H00 = H[0, 0]; fit.H01 = H[0, 1]; fit.H02 = H[0, 2];
            fit.H10 = H[1, 0]; fit.H11 = H[1, 1]; fit.H12 = H[1, 2];
            fit.H20 = H[2, 0]; fit.H21 = H[2, 1]; fit.H22 = H[2, 2];

            // Every sample must sit well on the near side of the fitted horizon: a fit that bends the
            // horizon through the sample window would extrapolate garbage even with tiny in-sample
            // residuals. Depth is measured relative to the window centroid so the test is scale-free.
            double wc = fit.H20 * mcx + fit.H21 * mcy + fit.H22;
            if (wc <= 0)
                return false;   // degenerate orientation
            resid = new double[m];
            double sse = 0;
            for (int i = 0; i < m; i++) {
                var s = set[i];
                double w = fit.H20 * s.cx + fit.H21 * s.cy + fit.H22;
                if (w < 0.15 * wc)
                    return false;   // fitted horizon crosses the sample window
                double ex = (fit.H00 * s.cx + fit.H01 * s.cy + fit.H02) / w - s.sx;
                double ey = (fit.H10 * s.cx + fit.H11 * s.cy + fit.H12) / w - s.sy;
                resid[i] = Math.Sqrt(ex * ex + ey * ey);
                sse += ex * ex + ey * ey;
            }
            rms = Math.Sqrt(sse / m);
            return true;
        }

        int n = samples.Count;
        if (!Solve(samples, out t, out var resid, out var rms))
            return t;

        // A handful of nodes sit far off the lattice (field dumps: individual residuals up to ~110px
        // against a ~25px rms), and they alone can push a marginal sample mix past the gate. One
        // trimmed re-solve drops the worst offenders; the cap keeps a genuinely bad fit from trimming
        // itself respectable, and the sample floor keeps the re-solve as constrained as the first.
        var kept = samples;
        int trimCap = Math.Min(n / 5, n - 10);
        if (trimCap > 0) {
            double cut = 2.5 * rms;
            var dropIdx = new List<int>();
            for (int i = 0; i < n; i++)
                if (resid[i] > cut)
                    dropIdx.Add(i);
            if (dropIdx.Count > trimCap) {
                dropIdx.Sort((a, b) => resid[b].CompareTo(resid[a]));
                dropIdx.RemoveRange(trimCap, dropIdx.Count - trimCap);
            }
            if (dropIdx.Count > 0) {
                var drop = new HashSet<int>(dropIdx);
                var trimmedSet = new List<(double cx, double cy, double sx, double sy)>(n - dropIdx.Count);
                for (int i = 0; i < n; i++)
                    if (!drop.Contains(i))
                        trimmedSet.Add(samples[i]);
                if (Solve(trimmedSet, out var refit, out _, out double rmsTrimmed)) {
                    t = refit;
                    rms = rmsTrimmed;
                    kept = trimmedSet;
                }
            }
        }

        double cmx = 0, cmy = 0;
        foreach (var s in kept) { cmx += s.cx; cmy += s.cy; }
        cmx /= kept.Count; cmy /= kept.Count;

        double scale = t.LocalScaleAt(cmx, cmy);
        // Gate calibrated from field sample dumps: atlas nodes sit organically off their lattice
        // points, so a clean homography fit carries an irreducible 2D rms of ~0.5*scale (measured
        // 0.49-0.53 across dumps, with zero quadrant bias - pure per-node jitter, not model error).
        // 0.7 passes that with margin; the failure modes stay far away (reopen-transient frames with
        // mixed stale/fresh rects measured ~18*scale, affine-grade error 3.9-4.6*scale).
        double gate = Math.Max(12, 0.7 * scale);
        t.Valid = scale > 1 && rms < gate;
        fitRejectedMarginally = !t.Valid && scale > 1 && rms < MarginalRejectFactor * gate;

        if (t.Valid) {
            // Anchor for the per-frame pan-follow: the kept sample nearest the screen center, where
            // zoom/rotation drift since the solve leaks least into the composed translation.
            double bestDist = double.MaxValue;
            foreach (var s in kept) {
                double dxm = s.sx - screenMid.X, dym = s.sy - screenMid.Y;
                double dd = dxm * dxm + dym * dym;
                if (dd < bestDist) {
                    bestDist = dd;
                    fitAnchorCoord = new Vector2i((int)s.cx, (int)s.cy);
                    fitAnchorScreen = new Vector2((float)s.sx, (float)s.sy);
                }
            }
        }

        return t;
    }

    #endregion

    #region Panel

    private void DrawMapFinderPanel()
    {
        ImGui.SetNextWindowPos(new Vector2(100, 100), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(640, 470), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(0.85f);

        if (!ImGui.Begin($"Map Finder  v{Version}###OptiPatherMapFinder", ref MapFinderPanelIsOpen, ImGuiWindowFlags.NoCollapse)) {
            ImGui.End();
            return;
        }

        // try/finally keeps the ImGui stack balanced if anything below throws.
        try {
            var presets = Settings.Presets;
            if (presets.Count == 0)
                selectedPresetIndex = -1;
            else
                selectedPresetIndex = Math.Clamp(selectedPresetIndex, 0, presets.Count - 1);

            DrawActiveLegend(presets);

            DrawPresetList(presets);
            ImGui.SameLine();
            DrawListEditorSplitter();
            ImGui.SameLine();
            DrawPresetEditor(presets);
        } finally {
            ImGui.End();
        }
    }

    // A thin draggable bar between the query list and the editor, letting the list be widened or narrowed.
    private void DrawListEditorSplitter()
    {
        float height = ImGui.GetContentRegionAvail().Y;
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(1f, 1f, 1f, 0.06f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 1f, 1f, 0.20f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1f, 1f, 1f, 0.35f));
        try {
            ImGui.Button("##list_splitter", new Vector2(6f, height));
        } finally {
            ImGui.PopStyleColor(3);
        }

        if (ImGui.IsItemActive())
            presetListWidth += ImGui.GetIO().MouseDelta.X;
        if (ImGui.IsItemHovered() || ImGui.IsItemActive())
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);

        float maxW = Math.Max(140f, ImGui.GetWindowWidth() - 220f);
        presetListWidth = Math.Clamp(presetListWidth, 120f, maxW);
    }

    // A one-line color key for everything currently drawing on the atlas.
    private void DrawActiveLegend(List<SearchPreset> presets)
    {
        if (presets == null || !presets.Any(p => p.Active))
            return;

        var results = mapFinderResults;
        ImGui.TextDisabled("Active:");
        for (int i = 0; i < presets.Count; i++) {
            if (!presets[i].Active)
                continue;
            ImGui.SameLine();
            ImGui.ColorButton($"##leg{i}", ToVector4(PresetColor(presets[i], i)), ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoPicker, new Vector2(12, 12));
            ImGui.SameLine();
            var r = results?.FirstOrDefault(x => x.PresetIndex == i);
            string steps = r?.Target != null ? (r.Steps >= 0 ? r.Steps.ToString() : "?") : "-";
            string name = string.IsNullOrWhiteSpace(presets[i].Name) ? "(unnamed)" : presets[i].Name;
            ImGui.TextUnformatted($"{name} ({steps})");
        }
        ImGui.Separator();
    }

    private void DrawPresetList(List<SearchPreset> presets)
    {
        ImGui.BeginChild("##preset_list", new Vector2(presetListWidth, 0), ImGuiChildFlags.Border);
        try {
            ImGui.TextDisabled("Searches");
            ImGui.Separator();

            for (int i = 0; i < presets.Count; i++) {
                var p = presets[i];
                ImGui.PushID(i);
                try {
                    bool act = p.Active;
                    if (ImGui.Checkbox("##active", ref act)) {
                        p.Active = act;
                        mapFinderDirty = true;
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Show this search on the atlas. Tick several to run them at once.");

                    ImGui.SameLine();
                    ImGui.ColorButton("##swatch", ToVector4(PresetColor(p, i)), ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoPicker, new Vector2(12, 12));

                    ImGui.SameLine();
                    string name = string.IsNullOrWhiteSpace(p.Name) ? "(unnamed)" : p.Name;
                    if (ImGui.Selectable(name, selectedPresetIndex == i))
                        selectedPresetIndex = i;
                } finally {
                    ImGui.PopID();
                }
            }

            ImGui.Separator();
            if (ImGui.Button("Add", new Vector2(56, 0)))
                AddPreset();
            ImGui.SameLine();
            if (ImGui.Button("Delete", new Vector2(64, 0)) && selectedPresetIndex >= 0)
                DeletePreset(selectedPresetIndex);

            ImGui.Separator();
            if (persistDisabled) {
                ImGui.TextColored(new Vector4(1f, 0.6f, 0.4f, 1f), "Atlas memory disabled (cannot write to disk)");
            } else {
                string age = cacheRememberedAge;
                ImGui.TextDisabled(age != null ? $"{cachedNodeCount} maps cached - remembered {age}" : $"{cachedNodeCount} maps cached");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Maps remembered from everywhere you have panned over, saved to disk per character so they survive a game restart or plugin reload. Maps you completed while the atlas was closed may show faint and 'unconfirmed' until you pan over them again.");
                string ev = lastCacheEvent;
                if (ev != null)
                    ImGui.TextDisabled(ev);
            }
            if (ImGui.Button("Rescan", new Vector2(-1, 0))) {
                finderClearCacheRequested = true;
                persistForgetRequested = true;
                mapFinderResults = new List<FinderResult>();   // drop stale results now; the rebuild republishes
                mapFinderDirty = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Forget the remembered maps (including the saved file for this character) and rebuild from what is on screen. Use after a major atlas change if the results look stale.");
        } finally {
            ImGui.EndChild();
        }
    }

    private void DrawPresetEditor(List<SearchPreset> presets)
    {
        ImGui.BeginChild("##preset_editor", new Vector2(0, 0), ImGuiChildFlags.Border);
        try {
            if (selectedPresetIndex < 0 || selectedPresetIndex >= presets.Count) {
                ImGui.TextWrapped("Add a search on the left, then edit it here. Tick a search to draw it on the atlas; tick several to run them in parallel.");
                return;
            }

            var p = presets[selectedPresetIndex];
            bool changed = false;

            string name = p.Name ?? "";
            ImGui.SetNextItemWidth(-90);
            if (ImGui.InputTextWithHint("##preset_name", "Search name", ref name, 64))
                p.Name = name;
            ImGui.SameLine();
            Vector4 col = ToVector4(PresetColor(p, selectedPresetIndex));
            if (ImGui.ColorEdit4("##preset_color", ref col, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar))
                p.ColorArgb = ToColor(col).ToArgb();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Route color on the atlas.");

            ImGui.Separator();
            ImGui.TextDisabled("Mandatory  (one = closest; several = one route through all)");

            // Migrate a legacy single-mandatory preset into the list the first time it is edited here.
            p.Mandatories ??= new();
            if (p.Mandatories.Count == 0 && (!string.IsNullOrWhiteSpace(p.MandatoryMap) || !string.IsNullOrWhiteSpace(p.MandatoryContent))) {
                p.Mandatories.Add(new MandatoryEntry { Map = p.MandatoryMap ?? "", Content = p.MandatoryContent ?? "" });
                p.MandatoryMap = "";
                p.MandatoryContent = "";
            }

            int removeMandAt = -1;
            float mandSpacing = ImGui.GetStyle().ItemSpacing.X;
            for (int mi = 0; mi < p.Mandatories.Count; mi++) {
                var me = p.Mandatories[mi];
                ImGui.PushID(2000 + mi);
                try {
                    // Reserve the actual remove-button width (a square ~one frame high) plus spacing, and use a
                    // small floor so the two combos shrink instead of pushing the X button off a narrow pane.
                    float xReserve = ImGui.GetFrameHeight() + mandSpacing * 2f;
                    float comboW = Math.Max(70f, (ImGui.GetContentRegionAvail().X - xReserve) / 2f);
                    ImGui.SetNextItemWidth(comboW);
                    if (DrawMapNameCombo("##m_map", me.Map ?? "", out string newMMap)) {
                        me.Map = newMMap;
                        changed = true;
                    }
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(comboW);
                    if (DrawContentCombo("##m_content", me.Content ?? "", out string newMContent)) {
                        me.Content = newMContent;
                        changed = true;
                    }
                    ImGui.SameLine();
                    if (ImGui.SmallButton("X"))
                        removeMandAt = mi;
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Remove this mandatory stop.");
                } finally {
                    ImGui.PopID();
                }
            }
            if (removeMandAt >= 0) {
                p.Mandatories.RemoveAt(removeMandAt);
                changed = true;
            }
            if (ImGui.Button("Add mandatory")) {
                p.Mandatories.Add(new MandatoryEntry());
                changed = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Add another required map. With two or more, the route is planned to visit them all.");

            ImGui.Spacing();
            ImGui.TextDisabled("Optionals  (a route near these is preferred)");

            p.Optionals ??= new();
            int removeAt = -1;
            float optSpacing = ImGui.GetStyle().ItemSpacing.X;
            for (int oi = 0; oi < p.Optionals.Count; oi++) {
                var o = p.Optionals[oi];
                ImGui.PushID(1000 + oi);
                try {
                    // Line 1: the two pickers split the editor width, so long map / mod names fit.
                    float comboW = Math.Max(120f, (ImGui.GetContentRegionAvail().X - optSpacing) / 2f);
                    ImGui.SetNextItemWidth(comboW);
                    if (DrawMapNameCombo("##o_map", o.Map ?? "", out string newOMap)) {
                        o.Map = newOMap;
                        changed = true;
                    }

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(comboW);
                    if (DrawContentCombo("##o_content", o.Content ?? "", out string oc)) {
                        o.Content = oc;
                        changed = true;
                    }

                    // Line 2: how strongly this optional pulls the route, plus a remove button.
                    ImGui.Indent(12f);
                    ImGui.TextDisabled("weight");
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(160f);
                    float bonus = o.Bonus;
                    if (ImGui.SliderFloat("##o_bonus", ref bonus, 0f, 10f, "%.1f")) {
                        o.Bonus = bonus;
                        changed = true;
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Hops of detour-saving this optional is worth when it sits right on the route.");

                    ImGui.SameLine();
                    if (ImGui.SmallButton("Remove"))
                        removeAt = oi;
                    ImGui.Unindent(12f);

                    ImGui.Spacing();
                } finally {
                    ImGui.PopID();
                }
            }
            if (removeAt >= 0) {
                p.Optionals.RemoveAt(removeAt);
                changed = true;
            }
            if (ImGui.Button("Add optional")) {
                p.Optionals.Add(new OptionalEntry());
                changed = true;
            }

            if (changed) {
                mapFinderPins.Remove(selectedPresetIndex);
                mapFinderForceFresh = selectedPresetIndex;   // apply edits at once, past the hysteresis hold
                mapFinderDirty = true;
            }

            ImGui.Separator();
            DrawPresetResults(p);
        } finally {
            ImGui.EndChild();
        }
    }

    // The searchable content/mod picker: a combo whose popup opens with a type-to-filter box, narrowing the
    // well-known types plus whatever this atlas exposes like a fuzzy finder. Returns true when the pick changed.
    private bool DrawContentCombo(string id, string current, out string picked)
    {
        picked = current;
        bool changed = false;

        var contentOptions = BuildContentOptions();
        int currentIndex = Math.Max(0, contentOptions.FindIndex(
            o => string.Equals(o, current ?? "", StringComparison.OrdinalIgnoreCase)));
        string Label(int i) => contentOptions[i].Length == 0 ? "Any content / mod" : contentOptions[i];

        if (ImGui.BeginCombo(id, Label(currentIndex))) {
            try {
                // Reset the filter and put the cursor in the box each time the popup opens.
                if (ImGui.IsWindowAppearing()) {
                    mapFinderContentFilter = "";
                    ImGui.SetKeyboardFocusHere();
                }

                ImGui.SetNextItemWidth(-float.Epsilon);
                bool enter = ImGui.InputTextWithHint(id + "_filter", "Type to filter...",
                    ref mapFinderContentFilter, 64, ImGuiInputTextFlags.EnterReturnsTrue);
                ImGui.Separator();

                string filter = NormalizeForMatch(mapFinderContentFilter);
                var visible = new List<int>();
                for (int i = 0; i < contentOptions.Count; i++)
                    if (filter.Length == 0 || NormalizeForMatch(Label(i)).Contains(filter))
                        visible.Add(i);

                // Enter selects the first remaining match, mirroring a fuzzy finder.
                if (enter && visible.Count > 0) {
                    picked = contentOptions[visible[0]];
                    changed = true;
                    ImGui.CloseCurrentPopup();
                }

                if (visible.Count == 0) {
                    ImGui.TextDisabled("No matching content / mod.");
                } else {
                    foreach (int i in visible) {
                        bool selected = i == currentIndex;
                        if (ImGui.Selectable(Label(i), selected)) {
                            picked = contentOptions[i];
                            changed = true;
                        }
                        if (selected)
                            ImGui.SetItemDefaultFocus();
                    }
                }
            } finally {
                ImGui.EndCombo();
            }
        }

        return changed;
    }

    // Searchable map-name picker: the same type-to-filter popup as the content combo, populated from the
    // map names the current atlas exposes. The preview shows whatever is stored (so a free-typed value
    // from an older build still shows), and the list lets you complete to a real name. Returns true on change.
    private bool DrawMapNameCombo(string id, string current, out string picked)
    {
        picked = current;
        bool changed = false;

        var options = new List<string> { "" };
        options.AddRange(availableMapNames);
        int currentIndex = options.FindIndex(o => string.Equals(o, current ?? "", StringComparison.OrdinalIgnoreCase));
        string Label(int i) => options[i].Length == 0 ? "Any map name" : options[i];
        string preview = string.IsNullOrEmpty(current) ? "Any map name" : current;

        if (ImGui.BeginCombo(id, preview)) {
            try {
                if (ImGui.IsWindowAppearing()) {
                    mapFinderNameFilter = "";
                    ImGui.SetKeyboardFocusHere();
                }

                ImGui.SetNextItemWidth(-float.Epsilon);
                bool enter = ImGui.InputTextWithHint(id + "_filter", "Type to filter...",
                    ref mapFinderNameFilter, 64, ImGuiInputTextFlags.EnterReturnsTrue);
                ImGui.Separator();

                string filter = NormalizeForMatch(mapFinderNameFilter);
                var visible = new List<int>();
                for (int i = 0; i < options.Count; i++)
                    if (filter.Length == 0 || NormalizeForMatch(Label(i)).Contains(filter))
                        visible.Add(i);

                if (enter && visible.Count > 0) {
                    picked = options[visible[0]];
                    changed = true;
                    ImGui.CloseCurrentPopup();
                }

                if (visible.Count == 0) {
                    ImGui.TextDisabled("No matching map name.");
                } else {
                    foreach (int i in visible) {
                        bool selected = i == currentIndex;
                        if (ImGui.Selectable(Label(i), selected)) {
                            picked = options[i];
                            changed = true;
                        }
                        if (selected)
                            ImGui.SetItemDefaultFocus();
                    }
                }
            } finally {
                ImGui.EndCombo();
            }
        }

        return changed;
    }

    private void DrawPresetResults(SearchPreset p)
    {
        var mands = BuildMandatoryFilters(p);
        if (mands.Count == 0) {
            ImGui.TextDisabled("Set a mandatory map or content/mod to search.");
            return;
        }

        var results = mapFinderResults;
        var route = results?.FirstOrDefault(r => r.PresetIndex == selectedPresetIndex);
        if (route == null) {
            ImGui.TextDisabled("Scanning the atlas...");
            return;
        }

        // Warnings first (e.g. a mandatory map with no reachable instance that was skipped from the route).
        if (route.Warnings != null)
            foreach (var w in route.Warnings)
                ImGui.TextColored(new Vector4(1f, 0.6f, 0.4f, 1f), w);

        bool multi = route.Stops != null && route.Stops.Count > 0;

        if (multi) {
            DrawItinerary(route);
            DrawOptionalDiagnostics(route);
            return;
        }

        if (route.Matches.Count == 0) {
            if (route.Warnings == null || route.Warnings.Count == 0)
                ImGui.TextColored(new Vector4(1f, 0.6f, 0.4f, 1f), "No unvisited maps match this search.");
            return;
        }

        if (route.Target != null && !route.Target.Visited) {
            string targetName = route.Target.Name ?? "(unknown)";
            string targetLabel = route.TargetContent != null ? $"{targetName} ({route.TargetContent})" : targetName;
            if (route.Steps >= 0) {
                string note;
                if (route.Bonus > 0.05f)
                    note = $"   optionals -{route.Bonus:0.0}  (effective {route.Steps - route.Bonus:0.0})";
                else if (p.Optionals != null && p.Optionals.Any(o => !string.IsNullOrWhiteSpace(o.Map) || !string.IsNullOrWhiteSpace(o.Content)))
                    note = "   optionals: none in range";
                else
                    note = "";
                ImGui.TextColored(new Vector4(0.4f, 0.9f, 1f, 1f),
                    $"Closest: {targetLabel}  -  {route.Steps} step{(route.Steps == 1 ? "" : "s")}{note}");
            } else {
                ImGui.TextColored(new Vector4(1f, 0.78f, 0.4f, 1f),
                    $"Found: {targetLabel}  -  no path from your completed/unlocked maps");
            }

            if (mapFinderPins.ContainsKey(selectedPresetIndex)) {
                ImGui.SameLine();
                if (ImGui.SmallButton("Auto")) {
                    mapFinderPins.Remove(selectedPresetIndex);
                    mapFinderDirty = true;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Stop pinning a specific map and point to the best match again.");
            }
        }

        DrawOptionalDiagnostics(route);

        ImGui.Separator();

        var flags = ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY;
        if (ImGui.BeginTable("mapfinder_results", 3, flags, new Vector2(0, 0))) {
            try {
                ImGui.TableSetupColumn("Map", ImGuiTableColumnFlags.WidthStretch, 200);
                ImGui.TableSetupColumn("Steps", ImGuiTableColumnFlags.WidthFixed, 50);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 50);
                ImGui.TableHeadersRow();

                int shown = 0;
                foreach (var match in route.Matches) {
                    var node = match.Node;
                    if (node.Visited)
                        continue;
                    if (shown >= Settings.MaxResults.Value)
                        break;
                    shown++;

                    string key = node.Coord.ToString();
                    string name = node.Name ?? "(unknown)";
                    bool isTarget = route.Target != null && route.Target.Coord.ToString() == key;

                    ImGui.PushID(key);
                    try {
                        ImGui.TableNextRow();

                        ImGui.TableNextColumn();
                        if (isTarget)
                            ImGui.TextColored(new Vector4(0.4f, 0.9f, 1f, 1f), name);
                        else
                            ImGui.TextUnformatted(name);
                        if (match.MatchedContent != null) {
                            ImGui.SameLine();
                            ImGui.TextDisabled("(" + match.MatchedContent + ")");
                        }
                        if (match.Bonus > 0.05f) {
                            ImGui.SameLine();
                            ImGui.TextColored(new Vector4(0.5f, 0.95f, 0.6f, 1f), $"-{match.Bonus:0.0}");
                        }

                        ImGui.TableNextColumn();
                        if (match.Steps < 0) {
                            ImGui.TextColored(ToVector4(Color.Gray), "?");
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip("No path found from your completed/unlocked maps.");
                        } else {
                            Color stepsColor = match.Steps <= 3 ? Color.LightGreen : (match.Steps <= 7 ? Color.Yellow : Color.OrangeRed);
                            ImGui.TextColored(ToVector4(stepsColor), match.Steps.ToString());
                        }

                        ImGui.TableNextColumn();
                        if (isTarget) {
                            ImGui.TextDisabled("point");
                        } else if (ImGui.SmallButton("Point")) {
                            mapFinderPins[selectedPresetIndex] = key;
                            mapFinderDirty = true;
                        }
                    } finally {
                        ImGui.PopID();
                    }
                }
            } finally {
                ImGui.EndTable();
            }
        }
    }

    // The planned multi-stop route: a summary line, then each stop in visiting order with cumulative steps.
    private void DrawItinerary(FinderResult route)
    {
        int stopCount = route.Stops.Count;
        string bonusNote = route.Bonus > 0.05f ? $"   optionals -{route.Bonus:0.0}" : "";
        ImGui.TextColored(new Vector4(0.4f, 0.9f, 1f, 1f),
            $"Route: {stopCount} stop{(stopCount == 1 ? "" : "s")}, {route.Steps} step{(route.Steps == 1 ? "" : "s")} total{bonusNote}");

        ImGui.Separator();
        var flags = ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY;
        if (ImGui.BeginTable("mapfinder_itinerary", 3, flags, new Vector2(0, 0))) {
            try {
                ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 24);
                ImGui.TableSetupColumn("Stop", ImGuiTableColumnFlags.WidthStretch, 200);
                ImGui.TableSetupColumn("Steps", ImGuiTableColumnFlags.WidthFixed, 50);
                ImGui.TableHeadersRow();

                foreach (var stop in route.Stops) {
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.TextDisabled(stop.Order.ToString());

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(stop.Node?.Name ?? "(unknown)");
                    if (stop.MatchedContent != null) {
                        ImGui.SameLine();
                        ImGui.TextDisabled("(" + stop.MatchedContent + ")");
                    }

                    ImGui.TableNextColumn();
                    Color stepsColor = stop.CumulativeSteps <= 3 ? Color.LightGreen : (stop.CumulativeSteps <= 7 ? Color.Yellow : Color.OrangeRed);
                    ImGui.TextColored(ToVector4(stepsColor), stop.CumulativeSteps.ToString());
                }
            } finally {
                ImGui.EndTable();
            }
        }
    }

    // Per-optional diagnostics for the chosen route, so it's obvious why optionals did or didn't help.
    private void DrawOptionalDiagnostics(FinderResult route)
    {
        if (route.NearOptionals == null || route.NearOptionals.Count == 0)
            return;

        int radius = Settings.CorridorRadius.Value;
        foreach (var near in route.NearOptionals) {
            string label = string.IsNullOrWhiteSpace(near.Label) ? "(optional)" : near.Label;
            Vector4 col;
            string text;
            if (near.Matches == 0) {
                col = new Vector4(1f, 0.5f, 0.4f, 1f);
                text = $"{label}: no maps match this name / mod";
            } else if (near.Gap < 0) {
                col = new Vector4(0.85f, 0.8f, 0.5f, 1f);
                text = $"{label}: {near.Matches} map(s), but none connect to this route";
            } else if (near.InRange) {
                col = new Vector4(0.5f, 0.95f, 0.6f, 1f);
                text = $"{label}: {near.Matches} map(s), nearest {near.Gap} hop(s) off route - bonus applied";
            } else {
                col = new Vector4(0.7f, 0.7f, 0.75f, 1f);
                text = $"{label}: {near.Matches} map(s), nearest {near.Gap} hop(s) (beyond radius {radius})";
            }
            ImGui.TextColored(col, text);
        }
    }

    private void AddPreset()
    {
        var presets = Settings.Presets;
        presets.Add(new SearchPreset {
            Name = $"Search {presets.Count + 1}",
            ColorArgb = PresetPalette[presets.Count % PresetPalette.Length].ToArgb(),
            Mandatories = new() { new MandatoryEntry() },   // start with one empty mandatory row
        });
        selectedPresetIndex = presets.Count - 1;
        mapFinderDirty = true;
    }

    private void DeletePreset(int index)
    {
        var presets = Settings.Presets;
        if (index < 0 || index >= presets.Count)
            return;

        presets.RemoveAt(index);

        // Pins are keyed by preset index; drop the deleted one and shift the rest down.
        var remapped = new Dictionary<int, string>();
        foreach (var kv in mapFinderPins) {
            if (kv.Key == index)
                continue;
            remapped[kv.Key > index ? kv.Key - 1 : kv.Key] = kv.Value;
        }
        mapFinderPins = remapped;

        selectedPresetIndex = presets.Count == 0 ? -1 : Math.Min(selectedPresetIndex, presets.Count - 1);
        mapFinderDirty = true;
    }

    #endregion

    #region Helpers

    private void DrawCenteredTextWithBackground(string text, Vector2 position, Color color, Color backgroundColor, bool center = false, int xPadding = 0, int yPadding = 0)
    {
        if (!IsOnScreen(position))
            return;

        var boxSize = Graphics.MeasureText(text);
        boxSize += new Vector2(xPadding, yPadding);

        if (center)
            position = position - new Vector2(boxSize.X / 2, boxSize.Y / 2);

        Graphics.DrawBox(position, boxSize + position, backgroundColor, 5.0f);

        position += new Vector2(xPadding / 2, yPadding / 2);

        Graphics.DrawText(text, position, color);
    }

    private void DrawRotatedImage(IntPtr textureId, Vector2 position, Vector2 size, float angle, Color color)
    {
        Vector2 center = position + size / 2;

        float cosTheta = (float)Math.Cos(angle);
        float sinTheta = (float)Math.Sin(angle);

        Vector2 RotatePoint(Vector2 point)
        {
            Vector2 translatedPoint = point - center;
            Vector2 rotatedPoint = new Vector2(
                translatedPoint.X * cosTheta - translatedPoint.Y * sinTheta,
                translatedPoint.X * sinTheta + translatedPoint.Y * cosTheta
            );
            return rotatedPoint + center;
        }

        Vector2 topLeft = RotatePoint(position);
        Vector2 topRight = RotatePoint(position + new Vector2(size.X, 0));
        Vector2 bottomRight = RotatePoint(position + size);
        Vector2 bottomLeft = RotatePoint(position + new Vector2(0, size.Y));

        Graphics.DrawQuad(textureId, topLeft, topRight, bottomRight, bottomLeft, color);
    }

    private void UpdateScreenBounds()
    {
        try {
            var size = GameController.Window.GetWindowRectangleTimeCache.Size;
            cachedScreenRect = new RectangleF(0, 0, size.X, size.Y);

            cachedTooltipRects.Clear();
            var worldMap = UI?.WorldMap;
            if (worldMap == null)
                return;

            foreach (var tooltipIndex in TooltipChildIndices) {
                var tooltip = worldMap.GetChildAtIndex(tooltipIndex);
                if (tooltip == null || !tooltip.IsVisible)
                    continue;

                RectangleF mapTooltip = tooltip.GetClientRect();
                mapTooltip.Inflate(mapTooltip.Width * 0.1f, mapTooltip.Height * 0.1f);
                cachedTooltipRects.Add(mapTooltip);
            }
        } catch (Exception e) {
            LogError("Error updating screen bounds: " + e.Message);
        }
    }

    private bool IsOnScreen(Vector2 position)
    {
        foreach (var tooltip in cachedTooltipRects)
            if (tooltip.Contains(position))
                return false;

        return cachedScreenRect.Contains(position);
    }

    private static Vector4 ToVector4(Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

    private static Color ToColor(Vector4 v) => Color.FromArgb(
        (int)(Math.Clamp(v.W, 0f, 1f) * 255),
        (int)(Math.Clamp(v.X, 0f, 1f) * 255),
        (int)(Math.Clamp(v.Y, 0f, 1f) * 255),
        (int)(Math.Clamp(v.Z, 0f, 1f) * 255));

    // A faint version of a route color, used to draw targets restored from the saved graph but not yet
    // reconfirmed live this session.
    private static Color Dimmed(Color c) => Color.FromArgb(110, c.R, c.G, c.B);

    // Caps a color's alpha, fading route pieces anchored to a projected coordinate rather than a live
    // element. Capping (not replacing) keeps an already-translucent user color below its live opacity.
    private static Color Faded(Color c, int alpha) => Color.FromArgb(Math.Min((int)c.A, alpha), c.R, c.G, c.B);

    #endregion
}
