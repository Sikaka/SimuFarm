using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using System.Windows.Forms;


namespace Agent
{
    public class Settings : ISettings
    {
        public ToggleNode Enable { get; set; } = new ToggleNode(false);

        public HotkeyNode StartBot { get; set; } = (HotkeyNode)Keys.Insert;

        [Menu("Movement Key")]
        public HotkeyNode MovementKey { get; set; } = (HotkeyNode)Keys.E;

        [Menu("Blink Key")]
        public HotkeyNode BlinkKey { get; set; } = (HotkeyNode)Keys.W;

        [Menu("Combat Key")]
        public HotkeyNode CombatKey { get; set; } = (HotkeyNode)Keys.Q;

        [Menu("View Distance")]
        public RangeNode<int> ViewDistance { get; set; } = new RangeNode<int>(90, 10, 500);

        [Menu("Combat Distance")]
        public RangeNode<int> CombatDistance { get; set; } = new RangeNode<int>(15, 10, 500);

        [Menu("Map Resolution")]
        public RangeNode<int> ChunkResolution { get; set; } = new RangeNode<int>(10, 1, 100);

        [Menu("Clamp Size")]
        public RangeNode<int> ClampSize { get; set; } = new RangeNode<int>(400, 100, 1000);

        [Menu("Pathfinding Node Size")]
        public RangeNode<int> NodeSize { get; set; } = new RangeNode<int>(20, 10, 100);
        [Menu("Store Inventory Count")]
        public RangeNode<int> StoreInventoryCount { get; set; } = new RangeNode<int>(30, 10, 60);

        [Menu("Wave End Delay")]
        public RangeNode<int> WaveEndDelay { get; set; } = new RangeNode<int>(5, 1, 45);

        [Menu("Support Mercenary (Uses T Key)")]
        public ToggleNode IsMercenarySupport { get; set; } = new ToggleNode(false);

        [Menu("Merc Link Frequency")]
        public RangeNode<int> MercLinkFrequency { get; set; } = new RangeNode<int>(5, 1, 45);
    }
}
