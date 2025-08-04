using AStar;
using AStar.Options;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using ExileCore.Shared.Interfaces;
using GameOffsets;
using GameOffsets.Native;
using System.Collections.Concurrent;
using System.Numerics;
using System.Xml.Linq;


namespace Agent.Navigation
{
    public class Map
    {
        // Use ConcurrentDictionary for thread-safe access from multiple Map instances if needed,
        // otherwise a regular Dictionary is fine if access is always sequential or locked.
        // For a singleton map per area, a regular Dictionary might suffice for Maps.
        public static ConcurrentDictionary<uint, Map> Maps { get; } = new ConcurrentDictionary<uint, Map>();

        private Random _random = new Random();
        private readonly GameController _gameController;
        private readonly Settings _settings;
        private readonly WorldGrid _worldGrid;
        private readonly PathFinder _pathFinder;
        private readonly ConcurrentDictionary<string, ConcurrentQueue<Vector2i>> _tiles;
        private Chunk[,] _chunks;
        private readonly uint _mapHash;
        private readonly DateTime _initializedAt;

        public DateTime InitializedAt => _initializedAt;

        public IReadOnlyList<Chunk> Chunks { get; private set; }

        public Map(GameController controller, Settings settings)
        {
            _gameController = controller ?? throw new ArgumentNullException(nameof(controller));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _initializedAt = DateTime.Now;
            _mapHash = _gameController.IngameState.Data.CurrentAreaHash;

            TerrainData terrain = controller.IngameState.Data.Terrain;

            // Use constants for magic numbers
            int gridWidth = ((int)terrain.NumCols - 1) * MapConstants.TileWorldUnit;
            int gridHeight = ((int)terrain.NumRows - 1) * MapConstants.TileWorldUnit;

            // Ensure gridWidth is even if that's the requirement.
            // The original logic `if ((num1 & 1) > 1)` appears incorrect for checking odd.
            // This ensures it's an even width.
            if (gridWidth % 2 != 0)
            {
                gridWidth++;
            }

            _worldGrid = new WorldGrid(gridHeight, gridWidth + 1); // +1 as per original logic

            _pathFinder = new PathFinder(_worldGrid, new PathFinderOptions()
            {
                PunishChangeDirection = false,
                UseDiagonals = true,
                SearchLimit = gridWidth * gridHeight // Use calculated grid dimensions
            });

            PopulateWorldGrid(terrain, _worldGrid, controller.Memory);
            ProcessTileData(terrain, _tiles = new ConcurrentDictionary<string, ConcurrentQueue<Vector2i>>(), controller.Memory);
            InitializeChunks(settings.ChunkResolution.Value, _worldGrid.Width, _worldGrid.Height);

            // Initialize the read-only Chunks list once after _chunks array is populated.
            Chunks = _chunks.Cast<Chunk>().ToList().AsReadOnly();
        }

        /// <summary>
        /// Populates the WorldGrid based on terrain melee layer data.
        /// </summary>
        /// <param name="terrain">The terrain data.</param>
        /// <param name="worldGrid">The world grid to populate.</param>
        /// <param name="memory">The memory accessor.</param>
        private static void PopulateWorldGrid(TerrainData terrain, WorldGrid worldGrid, IMemory memory)
        {
            byte[] layerMeleeBytes = memory.ReadBytes(terrain.LayerMelee.First, terrain.LayerMelee.Size);
            int currentByteOffset = 0;

            for (int row = 0; row < worldGrid.Height; ++row)
            {
                for (int column = 0; column < worldGrid.Width; column += 2) // Process two columns at a time
                {
                    // Ensure we don't go out of bounds for the layerMeleeBytes array
                    if (currentByteOffset + (column >> 1) >= layerMeleeBytes.Length) break;

                    byte tileValue = layerMeleeBytes[currentByteOffset + (column >> 1)];

                    // Original logic: `((int)num3 & 15) > 0 ? (short)1 : (short)0;`
                    // This extracts the lower 4 bits.
                    worldGrid[row, column] = (short)((tileValue & 0xF) > 0 ? 1 : 0);

                    // Original logic: `(int)num3 >> 4 > 0 ? (short)1 : (short)0;`
                    // This extracts the upper 4 bits.
                    if (column + 1 < worldGrid.Width) // Ensure next column is within bounds
                    {
                        worldGrid[row, column + 1] = (short)((tileValue >> 4) > 0 ? 1 : 0);
                    }
                }
                currentByteOffset += terrain.BytesPerRow;
            }
        }

        /// <summary>
        /// Processes tile data and populates the concurrent dictionary of tiles.
        /// </summary>
        /// <param name="terrain">The terrain data.</param>
        /// <param name="tiles">The concurrent dictionary to populate.</param>
        /// <param name="memory">The memory accessor.</param>
        private static void ProcessTileData(TerrainData terrain, ConcurrentDictionary<string, ConcurrentQueue<Vector2i>> tiles, IMemory memory)
        {
            TileStructure[] tileData = memory.ReadStdVector<TileStructure>(terrain.TgtArray);

            // Using Partitioner.Create for better load balancing in Parallel.ForEach,
            // although Parallel.For is often sufficient for simple ranges.
            Parallel.ForEach(Partitioner.Create(0, tileData.Length), (range, loopState) =>
            {
                for (int i = range.Item1; i < range.Item2; i++)
                {
                    var tgtTileStruct = memory.Read<TgtTileStruct>(tileData[i].TgtFilePtr);                    
                    string detailName = memory.Read<TgtDetailStruct>(tgtTileStruct.TgtDetailPtr).name.ToString(memory);
                    string tilePath = tgtTileStruct.TgtPath.ToString(memory);
                    Vector2i tileGridPosition = new Vector2i(
                        i % terrain.NumCols * MapConstants.TileWorldUnit,
                        i / terrain.NumCols * MapConstants.TileWorldUnit
                    );

                    if (!string.IsNullOrEmpty(tilePath))
                    {
                        tiles.GetOrAdd(tilePath, _ => new ConcurrentQueue<Vector2i>()).Enqueue(tileGridPosition);
                    }
                    if (!string.IsNullOrEmpty(detailName))
                    {
                        tiles.GetOrAdd(detailName, _ => new ConcurrentQueue<Vector2i>()).Enqueue(tileGridPosition);
                    }
                }
            });
        }

        /// <summary>
        /// Initializes the chunk grid for map exploration.
        /// </summary>
        /// <param name="chunkResolution">The resolution of each chunk.</param>
        /// <param name="worldGridWidth">The width of the world grid.</param>
        /// <param name="worldGridHeight">The height of the world grid.</param>
        private void InitializeChunks(int chunkResolution, int worldGridWidth, int worldGridHeight)
        {
            int chunksX = (int)Math.Ceiling((double)worldGridWidth / chunkResolution);
            int chunksY = (int)Math.Ceiling((double)worldGridHeight / chunkResolution);
            _chunks = new Chunk[chunksX, chunksY];

            for (int x = 0; x < chunksX; ++x)
            {
                for (int y = 0; y < chunksY; ++y)
                {
                    int chunkStartX = x * chunkResolution;
                    int chunkStartY = y * chunkResolution;
                    int chunkEndX = Math.Min(chunkStartX + chunkResolution, worldGridWidth);
                    int chunkEndY = Math.Min(chunkStartY + chunkResolution, worldGridHeight);

                    int totalWeight = 0;
                    for (int col = chunkStartX; col < chunkEndX; ++col)
                    {
                        for (int row = chunkStartY; row < chunkEndY; ++row)
                        {
                            totalWeight += _worldGrid[row, col];
                        }
                    }

                    _chunks[x, y] = new Chunk()
                    {
                        // Center the chunk position for clearer representation
                        Position = new Vector2(
                            (float)chunkStartX + (chunkResolution / 2f),
                            (float)chunkStartY + (chunkResolution / 2f)
                        ),
                        Weight = totalWeight
                    };
                }
            }
        }

        public Vector2? FindTilePositionByName(string searchString)
        {
            // Use TryGetValue or LINQ for direct key lookup if searchString is a full key.
            // If it's always a partial match, Contains is necessary but consider performance for very large dictionaries.
            // Assuming searchString might be a partial name.
            if (_tiles.TryGetValue(searchString, out var queue) && queue.Any())
            {
                return (Vector2)queue.FirstOrDefault();
            }

            // If direct lookup fails, try partial match.
            // Consider if a ConcurrentDictionary is truly needed if this is the only lookup pattern.
            // For partial matches, a List<(string key, ConcurrentQueue<Vector2i> queue)> might perform better initially,
            // then convert to dictionary for exact lookups.
            var matchingPair = _tiles.FirstOrDefault(kvp => kvp.Key.Contains(searchString));

            return matchingPair.Key != null && matchingPair.Value.Any()
                ? (Vector2?)matchingPair.Value.FirstOrDefault()
                : null;
        }

        public Path? FindPath(Vector2 start, Vector2 end)
        {
            Point[] pathPoints = _pathFinder.FindPath(new Point((int)start.X, (int)start.Y), new Point((int)end.X, (int)end.Y));

            if (pathPoints == null || pathPoints.Length == 0)
            {
                return null;
            }

            // Convert Point[] to List<Vector2> directly
            List<Vector2> pathVectors = new List<Vector2>(pathPoints.Length);
            foreach (Point p in pathPoints)
            {
                pathVectors.Add(new Vector2((float)p.X, (float)p.Y));
            }

            var cleanedNodes = new List<Vector2> { pathVectors[0] }; // Always keep the starting node
            var lastKeptNode = pathVectors[0];

            // Iterate through the path, but exclude the very last node for now
            for (int i = 1; i < pathVectors.Count - 1; i++)
            {
                var currentNode = pathVectors[i];
                if (Vector2.Distance(currentNode, lastKeptNode) >= _settings.NodeSize)
                {
                    cleanedNodes.Add(currentNode);
                    lastKeptNode = currentNode;
                }
            }
            // Always add the original destination to ensure we reach the target
            cleanedNodes.Add(pathVectors.Last());
            pathVectors = cleanedNodes;
            return new Path(pathVectors);
        }

        public double GetPositionExploreWeight(Vector2 position)
        {
            // Access the Chunks property directly, which now returns IReadOnlyList.
            return Chunks.Where(chunk =>
                !chunk.IsRevealed &&
                position.Distance(chunk.Position) < _settings.ViewDistance.Value
            ).Sum(chunk => chunk.Weight);
        }

        public double GetPositionFightWeight(Vector2 position)
        {
            return _gameController.EntityListWrapper.OnlyValidEntities
                .Where(entity =>
                    entity.Type == EntityType.Monster &&
                    entity.IsAlive && !entity.IsDead && entity.IsTargetable && entity.IsHostile&&
                    entity.GridPosNum.Distance(position) < _settings.CombatDistance.Value
                )
                .Sum(entity => GetMonsterRarityWeight(entity.Rarity));
        }

        /// <summary>
        /// Helper method to get the weight for a monster rarity.
        /// </summary>
        /// <param name="rarity">The monster rarity.</param>
        /// <returns>The weight associated with the rarity.</returns>
        public static int GetMonsterRarityWeight(MonsterRarity rarity)
        {
            return rarity switch
            {
                MonsterRarity.Magic => 3,
                MonsterRarity.Rare => 15,
                MonsterRarity.Unique => 50,
                _ => 1, // Default for Normal or unknown rarities
            };
        }

        // Generic helper for finding closest entities/labels
        private T? FindClosestItem<T>(IEnumerable<T> source, Func<T, bool> predicate, Func<T, float> distanceSelector) where T : class
        {
            return source.Where(predicate).MinBy(distanceSelector);
        }

        public LabelOnGround? ClosestValidEssence =>
            FindClosestItem(_gameController.IngameState.IngameUi.ItemsOnGroundLabelsVisible,
                            item => item.ItemOnGround.Metadata == MapConstants.EssenceMonolithMetadata,
                            item => item.ItemOnGround.DistancePlayer);

        public Entity? ClosestValidStrongbox =>
            FindClosestItem(_gameController.EntityListWrapper.OnlyValidEntities,
                            entity => entity.Type == EntityType.Chest &&
                                      entity.GetComponent<Chest>() is Chest chest &&
                                      chest.IsStrongbox && !chest.IsLocked && !chest.IsOpened,
                            entity => entity.DistancePlayer);

        public LabelOnGround? ClosestValidAltar =>
            FindClosestItem(_gameController.IngameState.IngameUi.ItemsOnGroundLabelsVisible,
                            item => item.ItemOnGround.Metadata == MapConstants.TangleAltarMetadata ||
                                    item.ItemOnGround.Metadata == MapConstants.CleansingFireAltarMetadata,
                            item => item.ItemOnGround.DistancePlayer);
        

        public Entity? ClosestValidShrine =>
            FindClosestItem(_gameController.EntityListWrapper.OnlyValidEntities,
                            entity => entity.Type == EntityType.Shrine && entity.GetComponent<Shrine>().IsAvailable,
                            entity => entity.DistancePlayer);

        public Entity? ClosestValidKingsmarchNode =>
            FindClosestItem(_gameController.EntityListWrapper.OnlyValidEntities,
                            entity => entity.Type == EntityType.IngameIcon &&
                                      (entity.Metadata.Contains(MapConstants.CrimsonIronNodeMetadata) ||
                                       entity.Metadata.Contains(MapConstants.BismuthNodeMetadata)) &&
                                      entity.GetComponent<StateMachine>() is StateMachine sm &&
                                      sm.States.FirstOrDefault(state => state.Name == MapConstants.ActivatedStateMachineState)?.Value == 0L,
                            entity => entity.DistancePlayer);


        public Vector2 GetRandomNearbyPosition(Vector2 position)
        {
            var newPos = new Vector2(_random.Next(-15, 15), _random.Next(-15, 15)) + position;
            for (var i = 0; i < 15; i++)
            {
                if (_worldGrid[(int)newPos.X, (int)newPos.Y] > 0) return newPos;
                newPos = new Vector2(_random.Next(-15, 15), _random.Next(-15, 15)) + position;
            }
            return position;
        }


        public double ExplorePercentage
        {
            get
            {
                int totalWeightGreaterThanZero = 0;
                int revealedWeightGreaterThanZero = 0;

                // Iterate directly over the 2D array for efficiency
                for (int x = 0; x < _chunks.GetLength(0); x++)
                {
                    for (int y = 0; y < _chunks.GetLength(1); y++)
                    {
                        Chunk chunk = _chunks[x, y];
                        if (chunk != null && chunk.Weight > 0)
                        {
                            totalWeightGreaterThanZero++;
                            if (chunk.IsRevealed)
                            {
                                revealedWeightGreaterThanZero++;
                            }
                        }
                    }
                }
                return totalWeightGreaterThanZero > 0 ? (double)revealedWeightGreaterThanZero / totalWeightGreaterThanZero : 1.0;
            }
        }
    }
}
