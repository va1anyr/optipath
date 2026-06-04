using System.Drawing;
using System.Windows.Forms;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;

namespace OptiPather;

public class OptiPatherSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    [Menu("Map Finder Panel Hotkey", "Opens the window to locate the closest unvisited map by name or by content/mods - e.g. Powerful Map Boss, Corrupted Nexus - measured in steps from your completed nodes. Default: PageDown")]
    public HotkeyNode MapFinderPanelHotkey { get; set; } = new HotkeyNode(Keys.PageDown);

    [Menu("Show Route on Atlas", "Draw a highlight, path and arrow pointing to the closest matching map.")]
    public ToggleNode ShowOnAtlas { get; set; } = new ToggleNode(true);

    [ConditionalDisplay(nameof(ShowOnAtlas), true)]
    [Menu("Show Path", "Draw the step-by-step path from the nearest completed node to the target map.")]
    public ToggleNode ShowPath { get; set; } = new ToggleNode(true);

    [ConditionalDisplay(nameof(ShowOnAtlas), true)]
    [Menu("Show Off-Screen Arrow", "Draw an arrow pointing toward the target map when it is off-screen.")]
    public ToggleNode ShowArrow { get; set; } = new ToggleNode(true);

    [ConditionalDisplay(nameof(ShowOnAtlas), true)]
    [Menu("Route Line Color", "Color of the path line drawn to the target map.")]
    public ColorNode LineColor { get; set; } = new ColorNode(Color.FromArgb(255, 0, 200, 255));

    [ConditionalDisplay(nameof(ShowOnAtlas), true)]
    [Menu("Target Highlight Color", "Color of the ring/arrow on the target map.")]
    public ColorNode HighlightColor { get; set; } = new ColorNode(Color.FromArgb(255, 0, 220, 255));

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

    [Menu("Max Results", "Maximum number of matching maps listed in the panel.")]
    public RangeNode<int> MaxResults { get; set; } = new RangeNode<int>(15, 1, 50);

    // Remembered between sessions; edited through the panel's search box.
    public string SearchQuery { get; set; } = "";

    // Remembered between sessions; selected through the panel's content/mod dropdown ("" = Any).
    public string SelectedContent { get; set; } = "";
}
