using ExileCore.PoEMemory.Components;
using ExileCore;
using System.Numerics;
using ExileCore.Shared.Helpers;

namespace Agent.Navigation
{
    public class Path
    {
        static DateTime nextDashSkill = DateTime.Now;
        static Random random = new Random();



        private readonly List<Vector2> _nodes;
        public Vector2 Destination => _nodes.LastOrDefault();
        public IReadOnlyList<Vector2> Nodes => _nodes.AsReadOnly();
        public DateTime CreationTime { get; }
        public float InitialDistance { get; }
        public Vector2? Next => _nodes.Count > 0 ? _nodes.First() : (Vector2?)null;

        public Path(List<Vector2> nodes)
        {
            _nodes = nodes ?? new List<Vector2>();
            if (_nodes.Count < 2)
            {
                return;
            }

            CreationTime = DateTime.Now;
            InitialDistance = nodes == null || nodes.Count < 2 ? 0 : nodes.First().Distance(nodes.Last());
            const float Epsilon = 0.001f;

            for (int i = _nodes.Count - 2; i >= 0; i--)
            {
                if (i + 2 < _nodes.Count)
                {
                    Vector2 currentSegmentDirection = Vector2.Normalize(_nodes[i + 1] - _nodes[i]);
                    Vector2 nextSegmentDirection = Vector2.Normalize(_nodes[i + 2] - _nodes[i + 1]);
                    float dotProduct = Vector2.Dot(currentSegmentDirection, nextSegmentDirection);
                    // If the segments are almost collinear (dot product close to 1), remove the intermediate node.
                    if (Math.Abs(dotProduct - 1.0f) < Epsilon)
                        _nodes.RemoveAt(i + 1);
                }
            }
        }

        public bool FollowPath(GameController controller, Settings settings, bool isCombat = false)
        {
            Vector2 playerGridPos = controller.Player.GridPosNum;
            if (!Next.HasValue)
            {
                ClearNearbyNodes(playerGridPos, settings.NodeSize.Value);
                return false;
            }

            Keys movementKey = settings.MovementKey.Value;

            if (DateTime.Now > nextDashSkill && random.NextDouble() < .1)
            {
                movementKey = settings.BlinkKey.Value;
                nextDashSkill = DateTime.Now.AddMilliseconds(750 + random.Next(500, 2000));
            }            

            Controls.UseKeyAtGridPos(Next.Value, movementKey);
            ClearNearbyNodes(playerGridPos, settings.NodeSize.Value);
            return true;
        }

        public void ClearNearbyNodes(Vector2 position, float radius)
        {

            if (_nodes.Count == 0) return;
            if (position.Distance(_nodes[0]) < radius)
                _nodes.RemoveAt(0);
        }
    }
}

