using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
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

// Atlas overlay that finds the closest unvisited map of a given type and points you to it with a
// highlight ring, a path line and an off-screen arrow. The graph is read straight from the atlas and
// searched on a background thread; the render loop only resolves live node positions for drawing.
public class OptiPather : BaseSettingsPlugin<OptiPatherSettings>
{
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
    private bool mapFinderFocusInput = false;
    private bool mapFinderDirty = false;
    private string mapFinderPinnedKey = null;
    private DateTime lastFinderRecompute = DateTime.Now;

    // Only one background search runs at a time.
    private volatile bool finderBusy = false;

    // Latest computed route, swapped in atomically by the worker and read by Render.
    private volatile FinderResult mapFinder = new();

    // Immutable copy of an atlas node, so the background search never touches live game objects.
    private sealed class GraphNode
    {
        public Vector2i Coord;
        public string Name;
        public bool Visited;
        public bool Unlocked;
        public bool Active;
    }

    private sealed class FinderMatch
    {
        public GraphNode Node { get; init; }
        public int Steps { get; init; }
    }

    private sealed class FinderResult
    {
        public List<FinderMatch> Matches { get; init; } = [];
        public GraphNode Target { get; init; }
        public List<GraphNode> Path { get; init; }
        public int Steps { get; init; } = -1;
    }

    public override bool Initialise()
    {
        Input.RegisterKey(Settings.MapFinderPanelHotkey);
        Settings.MapFinderPanelHotkey.OnValueChanged += () => Input.RegisterKey(Settings.MapFinderPanelHotkey);

        // Unique key so the arrow doesn't collide with another plugin's texture.
        Graphics.InitImage(ArrowTextureKey, Path.Combine(DirectoryFullName, "textures", "arrow.png"));
        arrowId = Graphics.GetTextureId(ArrowTextureKey);

        CanUseMultiThreading = true;
        return true;
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

        // Refresh the route every couple of seconds while a search is active so step counts track progress.
        bool hasQuery = !string.IsNullOrWhiteSpace(Settings.SearchQuery);
        if (hasQuery && !finderBusy && DateTime.Now.Subtract(lastFinderRecompute).TotalSeconds > 2)
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

        try { DrawMapFinderRoute(); }
        catch (Exception e) { LogError("Error drawing map finder route: " + e.Message + "\n" + e.StackTrace); }
    }

    private void CheckKeybinds()
    {
        if (AtlasPanel is not { IsVisible: true })
            return;

        if (Settings.MapFinderPanelHotkey.PressedOnce()) {
            MapFinderPanelIsOpen = !MapFinderPanelIsOpen;
            if (MapFinderPanelIsOpen) {
                mapFinderFocusInput = true;
                mapFinderDirty = true;   // refresh in case the atlas changed while it was closed
            }
        }
    }

    #region Map Finder

    // Run the scan + search off the render thread; atlas reads here are read-only.
    private void DispatchRecompute()
    {
        string query = Settings.SearchQuery?.Trim() ?? "";
        if (query.Length == 0) {
            mapFinder = new FinderResult();
            lastFinderRecompute = DateTime.Now;
            return;
        }

        var atlas = AtlasPanel;
        string pinned = mapFinderPinnedKey;
        finderBusy = true;
        Task.Run(() => {
            try {
                RecomputeMapFinder(atlas, query, pinned);
            } catch (Exception e) {
                LogError("Error computing map finder route: " + e.Message + "\n" + e.StackTrace);
            } finally {
                lastFinderRecompute = DateTime.Now;
                finderBusy = false;
            }
        });
    }

    // Scans the atlas into a private graph, runs a multi-source BFS out from your completed/unlocked maps,
    // and picks the nearest unvisited match for the query. Matching is independent of reachability, so a
    // map that exists is always listed (with a step count, or "?" when there's no route to it).
    private void RecomputeMapFinder(AtlasPanel atlas, string query, string pinnedKey)
    {
        if (atlas == null) {
            mapFinder = new FinderResult();
            return;
        }

        List<AtlasNodeDescription> descs;
        try {
            descs = atlas.Descriptions?.ToList() ?? [];
        } catch {
            return;   // not readable this pass - keep the previous result
        }

        if (descs.Count == 0) {
            mapFinder = new FinderResult();
            return;
        }

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

        // A node only gets connections once its area has actually been generated. Ungenerated entries
        // (e.g. a unique like Moment of Zen that only appears after a logbook extends the atlas) still sit
        // in Descriptions, often flagged unlocked, but have no links - drop them so they can't show up as
        // phantom 0-step targets out in unrevealed fog. Skip the filter if connections couldn't be read.
        bool haveConnections = adjacency.Count > 0;

        var nodes = new List<GraphNode>(descs.Count);
        var nodeByCoord = new Dictionary<Vector2i, GraphNode>(descs.Count);
        foreach (var d in descs) {
            var coord = d.Coordinate;
            if (haveConnections && !adjacency.ContainsKey(coord))
                continue;

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
                Active = active
            };
            nodes.Add(gn);
            nodeByCoord[coord] = gn;
        }

        if (nodes.Count == 0) {
            mapFinder = new FinderResult();
            return;
        }

        // Steps are measured from everywhere you can already stand (completed or unlocked). If you have no
        // progress at all, fall back to seeding from the active node.
        var dist = new Dictionary<Vector2i, int>(nodes.Count);
        var prev = new Dictionary<Vector2i, Vector2i>(nodes.Count);
        var queue = new Queue<Vector2i>();

        void Seed(Func<GraphNode, bool> isSource) {
            foreach (var n in nodes)
                if (isSource(n) && dist.TryAdd(n.Coord, 0))
                    queue.Enqueue(n.Coord);
        }

        Seed(n => n.Visited || n.Unlocked);
        if (queue.Count == 0)
            Seed(n => n.Active);

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

        // Reachable matches first (closest by steps), then the rest, ties broken by name.
        var matches = nodes
            .Where(n => !n.Visited && MatchesQuery(n, query))
            .Select(n => new FinderMatch { Node = n, Steps = dist.TryGetValue(n.Coord, out int s) ? s : -1 })
            .OrderBy(m => m.Steps < 0 ? int.MaxValue : m.Steps)
            .ThenBy(m => m.Node.Name ?? "", StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matches.Count == 0) {
            mapFinder = new FinderResult();
            return;
        }

        // Keep a pinned target while it still matches, otherwise the closest reachable one.
        FinderMatch chosen = null;
        if (pinnedKey != null)
            chosen = matches.FirstOrDefault(m => m.Node.Coord.ToString() == pinnedKey);
        chosen ??= matches.FirstOrDefault(m => m.Steps >= 0) ?? matches[0];

        mapFinder = new FinderResult {
            Matches = matches,
            Target = chosen.Node,
            Path = chosen.Steps >= 0 ? ReconstructFinderPath(chosen.Node.Coord, prev, nodeByCoord) : null,
            Steps = chosen.Steps
        };
    }

    private static bool MatchesQuery(GraphNode node, string query)
        => node.Name != null && node.Name.Contains(query, StringComparison.OrdinalIgnoreCase);

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

    private void DrawMapFinderRoute()
    {
        var route = mapFinder;
        if (!Settings.ShowOnAtlas || route?.Target == null)
            return;

        var target = route.Target;
        if (target.Visited)
            return;

        // Resolve current screen rects by coordinate; the search ran against a copy, positions are live.
        Dictionary<Vector2i, AtlasNodeDescription> descByCoord;
        try {
            var descs = AtlasPanel.Descriptions;
            descByCoord = new Dictionary<Vector2i, AtlasNodeDescription>(descs.Count);
            foreach (var d in descs)
                descByCoord[d.Coordinate] = d;
        } catch { return; }

        if (!descByCoord.TryGetValue(target.Coord, out var targetDesc))
            return;

        RectangleF targetRect;
        try { targetRect = targetDesc.Element.GetClientRect(); }
        catch { return; }

        Vector2 targetCenter = targetRect.Center;
        string targetName = target.Name ?? "(unknown)";
        string targetLabel = route.Steps >= 0 ? $"{targetName} ({route.Steps} steps)" : targetName;

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

                Graphics.DrawLine(start, end, Settings.LineWidth, Settings.LineColor);
            }
        }

        if (IsOnScreen(targetCenter)) {
            float radius = ((targetRect.Right - targetRect.Left) / 2 * Settings.RingRadius) + 10;
            Graphics.DrawCircle(targetCenter, radius, Settings.HighlightColor, Settings.RingWidth, 32);
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

                Color arrowColor = Color.FromArgb(255, (Color)Settings.HighlightColor);
                DrawRotatedImage(arrowId, arrowPosition, arrowSize, phi, arrowColor);

                Vector2 textPosition = arrowPosition + new Vector2(arrowSize.X / 2, arrowSize.Y / 2);
                textPosition = Vector2.Lerp(textPosition, screenCenter, 0.10f);
                DrawCenteredTextWithBackground(targetLabel, textPosition, arrowColor, Settings.BackgroundColor, true, 10, 4);
            }
        }
    }

    private void DrawMapFinderPanel()
    {
        ImGui.SetNextWindowPos(new Vector2(100, 100), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(420, 380), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(0.85f);

        if (!ImGui.Begin("Map Finder", ref MapFinderPanelIsOpen, ImGuiWindowFlags.NoCollapse)) {
            ImGui.End();
            return;
        }

        // try/finally keeps the ImGui stack balanced if anything below throws.
        try {
            ImGui.TextWrapped("Find the closest unvisited map of a given type, measured in steps from your completed nodes.");
            ImGui.Separator();

            if (mapFinderFocusInput) {
                ImGui.SetKeyboardFocusHere();
                mapFinderFocusInput = false;
            }

            string query = Settings.SearchQuery ?? "";
            bool changed = false;

            ImGui.SetNextItemWidth(-100);
            if (ImGui.InputTextWithHint("##mapfinder_search", "Map name (e.g. Jade Isles)", ref query, 64)) {
                Settings.SearchQuery = query;
                changed = true;
            }

            ImGui.SameLine();
            if (ImGui.Button("Clear", new Vector2(90, 0))) {
                Settings.SearchQuery = "";
                changed = true;
            }

            if (changed) {
                mapFinderPinnedKey = null;
                mapFinderDirty = true;
            }

            ImGui.Spacing();

            if (string.IsNullOrWhiteSpace(Settings.SearchQuery)) {
                ImGui.TextDisabled("Type a map name to search.");
                return;
            }

            var route = mapFinder;

            if (route.Matches.Count == 0) {
                ImGui.TextColored(new Vector4(1f, 0.6f, 0.4f, 1f), "No unvisited maps match that search.");
                return;
            }

            if (route.Target != null && !route.Target.Visited) {
                string targetName = route.Target.Name ?? "(unknown)";
                if (route.Steps >= 0)
                    ImGui.TextColored(new Vector4(0.4f, 0.9f, 1f, 1f),
                        $"Closest: {targetName}  -  {route.Steps} step{(route.Steps == 1 ? "" : "s")}");
                else
                    ImGui.TextColored(new Vector4(1f, 0.78f, 0.4f, 1f),
                        $"Found: {targetName}  -  no path from your completed/unlocked maps");

                if (mapFinderPinnedKey != null) {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Auto")) {
                        mapFinderPinnedKey = null;
                        mapFinderDirty = true;
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Stop pinning a specific map and point to the closest match again.");
                }
            }

            ImGui.Separator();

            var flags = ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY;
            if (ImGui.BeginTable("mapfinder_results", 3, flags, new Vector2(0, 240))) {
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
                                mapFinderPinnedKey = key;
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
        } finally {
            ImGui.End();
        }
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

    #endregion
}
