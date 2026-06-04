using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;

namespace OptiPather;

public class OptiPatherSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    [Menu("Map Finder Panel Hotkey", "Opens the window to build searches and locate the closest unvisited map by name or content/mods - e.g. Powerful Map Boss, Corrupted Nexus - measured in steps from your completed nodes. Default: PageDown")]
    public HotkeyNode MapFinderPanelHotkey { get; set; } = new HotkeyNode(Keys.PageDown);

    [Menu("Show Route on Atlas", "Draw a highlight, path and arrow pointing to the closest matching map for each active search.")]
    public ToggleNode ShowOnAtlas { get; set; } = new ToggleNode(true);

    [ConditionalDisplay(nameof(ShowOnAtlas), true)]
    [Menu("Show Path", "Draw the step-by-step path from the nearest completed node to the target map.")]
    public ToggleNode ShowPath { get; set; } = new ToggleNode(true);

    [ConditionalDisplay(nameof(ShowOnAtlas), true)]
    [Menu("Show Off-Screen Arrow", "Draw an arrow pointing toward the target map when it is off-screen.")]
    public ToggleNode ShowArrow { get; set; } = new ToggleNode(true);

    [ConditionalDisplay(nameof(ShowOnAtlas), true)]
    [Menu("Path Line Width", "Width of the path line drawn to the target map.")]
    public RangeNode<float> LineWidth { get; set; } = new RangeNode<float>(3.0f, 0, 10);

    [ConditionalDisplay(nameof(ShowOnAtlas), true)]
    [Menu("Target Ring Width", "Line width of the highlight ring drawn on the target map.")]
    public RangeNode<float> RingWidth { get; set; } = new RangeNode<float>(5.0f, 0, 10);

    [ConditionalDisplay(nameof(ShowOnAtlas), true)]
    [Menu("Target Ring Radius", "Scales the radius of the highlight ring drawn on the target map.")]
    public RangeNode<float> RingRadius { get; set; } = new RangeNode<float>(1f, 0, 10);

    [Menu("Label Font Color", "Color of the text label drawn on the target map / arrow.")]
    public ColorNode FontColor { get; set; } = new ColorNode(Color.White);

    [Menu("Label Background Color", "Background color behind the target/arrow text labels.")]
    public ColorNode BackgroundColor { get; set; } = new ColorNode(Color.FromArgb(177, 0, 0, 0));

    [Menu("Max Results", "Maximum number of matching maps listed per search in the panel.")]
    public RangeNode<int> MaxResults { get; set; } = new RangeNode<int>(15, 1, 50);

    [Menu("Optional Corridor Radius", "How many atlas hops an optional may sit from a route and still boost it. An optional on the route gives full bonus, one this many hops away gives none. Higher = optionals count from farther away.")]
    public RangeNode<int> CorridorRadius { get; set; } = new RangeNode<int>(4, 1, 15);

    [Menu("Optional Max Extra Hops", "Anti-teleport cap: the most extra hops an optional bonus may pull the recommendation past the genuinely closest map. Also caps the total bonus.")]
    public RangeNode<int> MaxExtraHops { get; set; } = new RangeNode<int>(5, 0, 30);

    [Menu("Optional Stickiness", "A challenger must beat the current pick by more than this many hops to replace it, stopping the recommendation flickering between near-ties as the atlas reveals.")]
    public RangeNode<float> OptionalHysteresis { get; set; } = new RangeNode<float>(0.5f, 0, 5);

    [Menu("Max Routes On Screen", "Soft cap on how many active searches draw their route/ring/arrow at once, to limit clutter.")]
    public RangeNode<int> MaxRoutesOnScreen { get; set; } = new RangeNode<int>(4, 1, 12);

    // Saved searches, edited through the Map Finder panel. Initialised empty on purpose: the settings
    // serializer appends to an existing list rather than replacing it, so a seeded default would
    // duplicate itself on every launch.
    public List<SearchPreset> Presets { get; set; } = new();

    // Legacy single-search fields from 1.x, migrated into a preset on first load; kept for back-compat.
    public string SearchQuery { get; set; } = "";
    public string SelectedContent { get; set; } = "";
}

// One saved search: a mandatory map/content to locate, plus optionals whose proximity boosts the route.
public class SearchPreset
{
    public string Name { get; set; } = "New Search";
    public string MandatoryMap { get; set; } = "";
    public string MandatoryContent { get; set; } = "";
    public List<OptionalEntry> Optionals { get; set; } = new();
    // Drawn on the atlas while ticked; ticking several at once runs them in parallel.
    public bool Active { get; set; } = false;
    // System.Drawing ARGB packed int; 0 means "auto-assign a distinct palette color by position".
    public int ColorArgb { get; set; } = 0;
}

// An optional target: a map name and/or content/mod, with how many hops of detour-saving it is worth
// when it sits right on the route (the bonus fades to zero at the corridor radius).
public class OptionalEntry
{
    public string Map { get; set; } = "";
    public string Content { get; set; } = "";
    public float Bonus { get; set; } = 2.0f;
}
