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
    public const string Version = "2.0.6";

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
    private bool mapFinderDirty = false;
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
        public string MandatoryMap;
        public string MandatoryContent;
        public Color Color;
        public List<OptDef> Optionals = new();
        public string PinnedKey;
        public bool Fresh;
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

        if (AtlasPanel is not { IsVisible: true }) {
            MapFinderPanelIsOpen = false;
            return;
        }

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
                MandatoryMap = p.MandatoryMap?.Trim() ?? "",
                MandatoryContent = p.MandatoryContent?.Trim() ?? "",
                Color = PresetColor(p, i),
                Optionals = opts,
                PinnedKey = pin,
                Fresh = i == mapFinderForceFresh,
            });
        }
        mapFinderForceFresh = -1;
        return list;
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

        finderBusy = true;
        Task.Run(() => {
            try {
                RecomputeMapFinder(atlas, queries, radius, maxExtra, hysteresis, previous);
            } catch (Exception e) {
                LogError("Error computing map finder routes: " + e.Message + "\n" + e.StackTrace);
            } finally {
                lastFinderRecompute = DateTime.Now;
                finderBusy = false;
            }
        });
    }

    // Scans the atlas into a private graph, runs a multi-source BFS out from your completed/unlocked maps
    // (shared by every search), builds a proximity distance field per distinct optional, then scores each
    // search's candidates and publishes one result apiece. Also publishes the content tags for the dropdown.
    private void RecomputeMapFinder(AtlasPanel atlas, List<PresetQuery> queries, int radius, int maxExtra, float hysteresis, List<FinderResult> previous)
    {
        if (atlas == null) {
            mapFinderResults = new List<FinderResult>();
            return;
        }

        List<AtlasNodeDescription> descs;
        try {
            descs = atlas.Descriptions?.ToList() ?? [];
        } catch {
            return;   // not readable this pass - keep the previous result
        }

        if (descs.Count == 0)
            return;   // atlas momentarily unreadable - keep the previous result instead of blanking the panel

        // Build the connection graph first (links are bidirectional) so node creation can drop coordinates
        // that have no connections.
        var adjacency = new Dictionary<Vector2i, List<Vector2i>>(descs.Count);
        void AddEdge(Vector2i a, Vector2i b) {
            if (!adjacency.TryGetValue(a, out var list))
                adjacency[a] = list = [];
            list.Add(b);
        }
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
                    AddEdge(source, target);
                    AddEdge(target, source);
                }
            }
        } catch {
            // Fall back to including every node if connections can't be read.
        }

        // atlas.Points can read partially (e.g. right after a reload, before the atlas finishes populating),
        // so a node with no links may just be one whose connections haven't loaded yet - NOT necessarily an
        // ungenerated phantom (like a unique that only appears once a logbook extends the atlas). Keep every
        // node matchable regardless, so a search never goes blank just because connections lag; phantoms are
        // stopped from becoming bogus 0-step targets by gating the BFS seeds on having connections (below),
        // which leaves a link-less map simply unreachable ("?") until its connections appear on a later scan.
        bool haveConnections = adjacency.Count > 0;

        var nodes = new List<GraphNode>(descs.Count);
        var nodeByCoord = new Dictionary<Vector2i, GraphNode>(descs.Count);
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

            var gn = new GraphNode {
                Coord = coord,
                Name = name,
                Visited = visited,
                Unlocked = unlocked,
                Active = active,
                ContentTags = CollectContentTags(el)
            };
            nodes.Add(gn);
            nodeByCoord[coord] = gn;
        }

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
        Seed(n => (n.Visited || n.Unlocked) && Connected(n));
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
                if (dist.ContainsKey(n.Coord) && MatchesFilters(n, kv.Value.Map, kv.Value.Content, out _))
                    seeds.Add(n.Coord);
            fields[kv.Key] = BuildDistanceField(adjacency, nodeByCoord, seeds);
        }

        var results = new List<FinderResult>(queries.Count);
        foreach (var q in queries) {
            FinderResult prevResult = previous?.FirstOrDefault(r => r.PresetIndex == q.Index);
            results.Add(ComputePresetResult(q, nodes, dist, prev, nodeByCoord, fields, radius, maxExtra, hysteresis, prevResult));
        }

        mapFinderResults = results;
    }

    // Multi-source BFS out from a set of optional instances, capped at the corridor radius. Each reached
    // node records the distance to, and identity of, its nearest seeding instance.
    private static DistanceField BuildDistanceField(Dictionary<Vector2i, List<Vector2i>> adjacency, Dictionary<Vector2i, GraphNode> nodeByCoord, IEnumerable<Vector2i> seeds)
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

    // Picks which instance of a search's mandatory type to recommend. Among matches within the extra-hop
    // budget, the chosen one minimizes EffectiveSteps = steps - optionalBonus, where each optional's bonus
    // fades linearly with how far it sits from the route and the total is capped by the budget. A pinned
    // map always wins; near-ties are held steady by hysteresis so the recommendation doesn't flicker.
    private static FinderResult ComputePresetResult(PresetQuery q, List<GraphNode> nodes, Dictionary<Vector2i, int> dist, Dictionary<Vector2i, Vector2i> prev, Dictionary<Vector2i, GraphNode> nodeByCoord, Dictionary<string, DistanceField> fields, int radius, int maxExtra, float hysteresis, FinderResult prevResult)
    {
        var empty = new FinderResult { PresetIndex = q.Index, PresetName = q.Name, Color = q.Color };

        var matches = new List<FinderMatch>();
        foreach (var n in nodes) {
            if (n.Visited || !MatchesFilters(n, q.MandatoryMap, q.MandatoryContent, out string matchedContent))
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

    // Scores one candidate against the optionals and, for every optional, records an outcome (match count,
    // gap, in/out of range, nearest instance, detour) so the result can be both drawn and explained. The
    // bonus is the sum of each in-range optional's linearly-decaying reward, capped by the extra-hop budget.
    private static void ScoreCandidate(FinderMatch m, List<OptDef> opts, Dictionary<string, DistanceField> fields, Dictionary<Vector2i, Vector2i> prev, Dictionary<Vector2i, GraphNode> nodeByCoord, int radius, int maxExtra, out float bonus, out List<NearOptional> near)
    {
        bonus = 0f;
        near = new List<NearOptional>();
        if (opts == null || opts.Count == 0)
            return;

        var path = ReconstructFinderPath(m.Node.Coord, prev, nodeByCoord);

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
            bool active = presets != null && r.PresetIndex >= 0 && r.PresetIndex < presets.Count && presets[r.PresetIndex].Active;
            if (active)
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
        } catch { return; }

        bool labelWithName = drawList.Count > 1;
        foreach (var route in drawList)
            DrawSingleRoute(route, descByCoord, labelWithName);
    }

    private void DrawSingleRoute(FinderResult route, Dictionary<Vector2i, AtlasNodeDescription> descByCoord, bool labelWithName)
    {
        var target = route.Target;
        if (target == null || target.Visited)
            return;
        if (!descByCoord.TryGetValue(target.Coord, out var targetDesc))
            return;

        RectangleF targetRect;
        try { targetRect = targetDesc.Element.GetClientRect(); }
        catch { return; }

        Color color = route.Color;
        Vector2 targetCenter = targetRect.Center;
        string prefix = labelWithName && !string.IsNullOrEmpty(route.PresetName) ? $"[{route.PresetName}] " : "";
        string targetName = target.Name ?? "(unknown)";
        string targetDescription = route.TargetContent != null ? $"{targetName} - {route.TargetContent}" : targetName;
        string targetLabel = route.Steps >= 0 ? $"{prefix}{targetDescription} ({route.Steps} steps)" : $"{prefix}{targetDescription}";

        if (Settings.ShowPath && route.Path is { Count: > 1 }) {
            for (int i = 0; i < route.Path.Count - 1; i++) {
                var a = route.Path[i];
                var b = route.Path[i + 1];
                if (a == null || b == null)
                    continue;
                if (!descByCoord.TryGetValue(a.Coord, out var da) || !descByCoord.TryGetValue(b.Coord, out var db))
                    continue;

                Vector2 start, end;
                try {
                    start = da.Element.GetClientRect().Center;
                    end = db.Element.GetClientRect().Center;
                } catch { continue; }

                if (!IsOnScreen(start) && !IsOnScreen(end))
                    continue;

                Graphics.DrawLine(start, end, Settings.LineWidth, color);
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
                if (near?.Node == null || !descByCoord.TryGetValue(near.Node.Coord, out var od))
                    continue;
                RectangleF orect;
                try { orect = od.Element.GetClientRect(); }
                catch { continue; }
                Vector2 oc = orect.Center;
                Color drawColor = near.InRange ? Color.FromArgb(165, color) : Color.FromArgb(110, 150, 150, 150);

                if (near.InRange && near.Detour is { Count: > 1 }) {
                    for (int i = 0; i < near.Detour.Count - 1; i++) {
                        if (!descByCoord.TryGetValue(near.Detour[i].Coord, out var dn) || !descByCoord.TryGetValue(near.Detour[i + 1].Coord, out var dn2))
                            continue;
                        Vector2 ds, de;
                        try { ds = dn.Element.GetClientRect().Center; de = dn2.Element.GetClientRect().Center; }
                        catch { continue; }
                        if (!IsOnScreen(ds) && !IsOnScreen(de))
                            continue;
                        Graphics.DrawLine(ds, de, detourWidth, drawColor);
                    }

                    // Marker on the branch point - the optimal spot to leave the route for this optional.
                    if (descByCoord.TryGetValue(near.Detour[0].Coord, out var bn)) {
                        try {
                            var brect = bn.Element.GetClientRect();
                            Vector2 bc = brect.Center;
                            if (IsOnScreen(bc))
                                Graphics.DrawCircle(bc, ((brect.Right - brect.Left) / 2 * Settings.RingRadius) * 0.55f + 4, drawColor, optWidth, 16);
                        } catch { }
                    }
                }

                if (!IsOnScreen(oc))
                    continue;
                float orad = ((orect.Right - orect.Left) / 2 * Settings.RingRadius) + 6;
                Graphics.DrawCircle(oc, orad, drawColor, optWidth, 24);
                string optLabel = near.InRange
                    ? $"{near.Label}  ({near.Gap} hop{(near.Gap == 1 ? "" : "s")} off route)"
                    : $"{near.Label}  ({near.Gap} hops - beyond radius)";
                DrawCenteredTextWithBackground(optLabel, oc + new Vector2(0, orad + 10), Settings.FontColor, Settings.BackgroundColor, true, 8, 3);
            }
        }

        if (IsOnScreen(targetCenter)) {
            float radius = ((targetRect.Right - targetRect.Left) / 2 * Settings.RingRadius) + 10;
            Graphics.DrawCircle(targetCenter, radius, color, Settings.RingWidth, 32);
            DrawCenteredTextWithBackground(targetLabel, targetCenter - new Vector2(0, radius + 12), Settings.FontColor, Settings.BackgroundColor, true, 10, 4);
        }

        if (Settings.ShowArrow) {
            float distance = Vector2.Distance(screenCenter, targetCenter);
            if (distance >= 400) {
                Vector2 arrowSize = new(64, 64);
                var windowSize = GameController.Window.GetWindowRectangleTimeCache.Size;
                Vector2 arrowPosition = targetCenter;
                arrowPosition.X = Math.Clamp(arrowPosition.X, 0, windowSize.X);
                arrowPosition.Y = Math.Clamp(arrowPosition.Y, 0, windowSize.Y);
                arrowPosition = Vector2.Lerp(screenCenter, arrowPosition, 0.80f);
                arrowPosition -= new Vector2(arrowSize.X / 2, arrowSize.Y / 2);

                Vector2 direction = targetCenter - screenCenter;
                float phi = (float)Math.Atan2(direction.Y, direction.X) + (float)(Math.PI / 2);

                Color arrowColor = Color.FromArgb(255, color);
                DrawRotatedImage(arrowId, arrowPosition, arrowSize, phi, arrowColor);

                Vector2 textPosition = arrowPosition + new Vector2(arrowSize.X / 2, arrowSize.Y / 2);
                textPosition = Vector2.Lerp(textPosition, screenCenter, 0.10f);
                DrawCenteredTextWithBackground(targetLabel, textPosition, arrowColor, Settings.BackgroundColor, true, 10, 4);
            }
        }
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
            ImGui.TextDisabled("Mandatory  (the map this search locates)");

            ImGui.SetNextItemWidth(-1);
            if (DrawMapNameCombo("##mand_map", p.MandatoryMap ?? "", out string newMap)) {
                p.MandatoryMap = newMap;
                changed = true;
            }

            ImGui.SetNextItemWidth(-1);
            if (DrawContentCombo("##mand_content", p.MandatoryContent ?? "", out string newContent)) {
                p.MandatoryContent = newContent;
                changed = true;
            }

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
        bool hasFilter = !string.IsNullOrWhiteSpace(p.MandatoryMap) || !string.IsNullOrWhiteSpace(p.MandatoryContent);
        if (!hasFilter) {
            ImGui.TextDisabled("Set a mandatory map or content/mod to search.");
            return;
        }

        var results = mapFinderResults;
        var route = results?.FirstOrDefault(r => r.PresetIndex == selectedPresetIndex);
        if (route == null) {
            ImGui.TextDisabled("Scanning the atlas...");
            return;
        }
        if (route.Matches.Count == 0) {
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

        // Per-optional diagnostics for the chosen route, so it's obvious why optionals did or didn't help.
        if (route.NearOptionals != null && route.NearOptionals.Count > 0) {
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

    private void AddPreset()
    {
        var presets = Settings.Presets;
        presets.Add(new SearchPreset {
            Name = $"Search {presets.Count + 1}",
            ColorArgb = PresetPalette[presets.Count % PresetPalette.Length].ToArgb(),
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

    #endregion
}
