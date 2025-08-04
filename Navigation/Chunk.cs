
using System.Numerics;

namespace Agent.Navigation
{
    public class Chunk
    {
        public Vector2 Position { get; set; }

        public int Weight { get; set; }

        public bool IsRevealed { get; set; } = false;
    }
}
