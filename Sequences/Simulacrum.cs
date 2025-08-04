using ExileCore.Shared.Enums;
using ExileCore;
using Agent.Navigation;
using System.Numerics;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.PoEMemory.Components;
using ExileCore.Shared.Helpers;
using ExileCore.PoEMemory.Elements;
using System.Windows.Forms;

namespace Agent.Sequences
{
    /// <summary>
    /// Represents the different states the Simulacrum farmer can be in.
    /// </summary>
    public enum SimulacrumState
    {
        Starting,
        FindingMonolith,
        StartingNewMap,
        EnteringMap,
        LeavingMap,
        ReturningToAnchor,
        CombatHold,
        CombatSeek,
        Exploring,
        Looting,
        Stashing,
        Died,
        Finished,
        Error
    }

    /// <summary>
    /// A self-contained module for farming Simulacrum maps in Path of Exile.
    /// </summary>
    public class SimulacrumFarmer
    {
        // --- Fields ---
        private readonly ExileCore.Graphics _graphics;
        private readonly GameController _gameController;
        private readonly Settings _settings;
        private Navigation.Map _map;
        private Navigation.Path? _currentPath;

        private uint _lootingItemId = 0;
        private int _lootAttemptCounter = 0;
        private const int MaxLootAttempts = 5; 

        private readonly Dictionary<uint, DateTime> _itemBlacklist = new Dictionary<uint, DateTime>();
        private const int ItemBlacklistSeconds = 10;

        public SimulacrumState CurrentState { get; private set; }
        public long CurrentWave { get; private set; }
        public bool IsWaveActive { get; private set; }
        public string ErrorMessage { get; private set; } = "";


        bool hasBreached = false;
        Vector2 LastPosition = Vector2.Zero;
        DateTime LastMovedAt = DateTime.Now;

        // Timers and Constants
        private const int MaxWaves = 15;
        private const string SimulacrumMonolithMetadata = "Objects/Afflictionator";
        private const string StashMetadata = "Metadata/MiscellaneousObjects/Stash";
        private const int MaxStashAttempts = 10;
        private const int MaxWaveDeaths = 3;
        private const int InstanceTimeoutMinutes = 20;
        private const int PathingTimeoutSeconds = 15;
        private const int SearchTimeoutSeconds = 10; // Timeout for finding entities after loading


        DateTime waveStartedAt = DateTime.Now;
        DateTime waveEndedAt = DateTime.MinValue;
        // State Management
        private Vector2? _stashPosition;
        private Entity _simulacrumMonolithCache;
        private DateTime _nextActionTime = DateTime.MinValue;
        private readonly Random _random = new Random();
        private readonly Dictionary<Vector2, DateTime> _explorationTargets = new Dictionary<Vector2, DateTime>();
        private const double ExplorationCooldownSeconds = 10.0;
        public Vector2? _anchorPosition;
        private DateTime _lastMonsterInRangeTime = DateTime.MinValue;
        private DateTime _nextSpellCastTime = DateTime.MinValue;
        private DateTime _stopLooting = DateTime.MinValue;
        private const int RepositionDelaySeconds = 2;
        private const int SpellCastIntervalMs = 2200;
        private readonly Dictionary<Vector2, DateTime> _pathingBlacklist = new Dictionary<Vector2, DateTime>();
        private const int PathBlacklistSeconds = 15;
        private int _stashAttemptCounter = 0;
        private int _waveDeathCounter = 0;
        private long _lastWaveWithDeath = -1;
        private uint storingId = 0;
        private DateTime _searchStartTime;

        private DateTime _instanceStartTime;


        Vector2 debugDrawPos = Vector2.Zero;
        string debugDrawText = "";


        public SimulacrumFarmer(GameController gameController, ExileCore.Graphics graphics, Settings settings)
        {
            _gameController = gameController ?? throw new ArgumentNullException(nameof(gameController));
            _graphics = graphics ?? throw new ArgumentNullException(nameof(graphics));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            _map = new Navigation.Map(gameController, settings);
            if (_gameController.Area.CurrentArea.IsHideout)
            {
                SetState(SimulacrumState.StartingNewMap);
            }
            else
            {
                ResetForNewMap();
                SetState(SimulacrumState.FindingMonolith);
            }

            CurrentWave = 0;
            IsWaveActive = false;
            FindStash();
        }

        public void Farm()
        {
            if (_gameController.Player.GridPosNum != LastPosition)
            {
                LastPosition = _gameController.Player.GridPosNum;
                LastMovedAt = DateTime.Now;
            }
            //We have a path, it's existed for 2 seconds and we haven't moved in 2 seconds. Clear the path.
            if (DateTime.Now > LastMovedAt.AddSeconds(2) && _currentPath != null && DateTime.Now > _currentPath.CreationTime.AddSeconds(2))
                _currentPath = null;

            if (CurrentState == SimulacrumState.Error) return;
            if (DateTime.Now < _nextActionTime) return;

            if (DateTime.Now > LastMovedAt.AddSeconds(7))
            {
                LastMovedAt = DateTime.Now;
                Controls.UseKeyAtGridPos(_map.GetRandomNearbyPosition(_gameController.Player.GridPosNum), _settings.BlinkKey);
            }

            if (!_gameController.Area.CurrentArea.IsHideout && CurrentState != SimulacrumState.Error && (DateTime.Now - _instanceStartTime).TotalMinutes > InstanceTimeoutMinutes)
            {
                Console.WriteLine($"Instance has been active for over {InstanceTimeoutMinutes} minutes. Abandoning run.");
                SetState(SimulacrumState.LeavingMap);
                return;
            }

            if (!_gameController.Player.IsAlive && _gameController.Player.IsDead)
            {
                if (CurrentState != SimulacrumState.Died)
                {
                    if (_lastWaveWithDeath != CurrentWave)
                    {
                        _waveDeathCounter = 0;
                        _lastWaveWithDeath = CurrentWave;
                    }
                    _waveDeathCounter++;
                    SetState(SimulacrumState.Died);
                }
            }


            if (DateTime.Now > lastChangedStatedAt.AddSeconds(15))
            {
                _currentPath = null;
                _explorationTargets.Clear();
                _pathingBlacklist.Clear();
                FindStash();

                Controls.UseKeyAtGridPos(_map.GetRandomNearbyPosition(_gameController.Player.GridPosNum), _settings.BlinkKey);

                lastChangedStatedAt = DateTime.Now;
            }

            CheckForStuckConditions();
            UpdateWaveStatus();

            var itemToLoot = ClosestValidGroundItem;
            if (itemToLoot != null && !IsInventoryFull() && CurrentState != SimulacrumState.Looting && CurrentState != SimulacrumState.Stashing)
            {
                CreateNewPath(itemToLoot.Entity.GridPosNum);
                SetState(SimulacrumState.Looting);
            }

            switch (CurrentState)
            {
                case SimulacrumState.FindingMonolith: HandleFindingMonolithState(); break;
                case SimulacrumState.LeavingMap: HandleLeavingMapState(); break;
                case SimulacrumState.Starting: HandleStartingState(); break;
                case SimulacrumState.StartingNewMap: HandleStartingNewMapState(); break;
                case SimulacrumState.EnteringMap: HandleEnteringMapState(); break;
                case SimulacrumState.ReturningToAnchor: HandleReturningToAnchorState(); break;
                case SimulacrumState.CombatHold: HandleCombatHoldState(); break;
                case SimulacrumState.CombatSeek: HandleCombatSeekState(); break;
                case SimulacrumState.Exploring: HandleExploringState(); break;
                case SimulacrumState.Looting: HandleLootingState(); break;
                case SimulacrumState.Stashing: HandleStashingState(); break;
                case SimulacrumState.Died: HandleDeathState(); break;
                case SimulacrumState.Finished:
                    //This is to ensure there's ample time for loot to drop.
                    if(DateTime.Now > waveEndedAt.AddSeconds(_settings.WaveEndDelay.Value))
                        SetState(SimulacrumState.LeavingMap); 
                    break;
            }
        }

        private void CreateNewPath(Vector2 destination)
        {
            _currentPath = _map.FindPath(_gameController.Player.GridPosNum, destination);
        }

        public void OnAreaChanged(Navigation.Map newMap)
        {
            _map = newMap;
            _currentPath = null;

            if (_gameController.Area.CurrentArea.IsHideout)
            {
                if (CurrentState == SimulacrumState.Died)
                {
                    SetState(SimulacrumState.EnteringMap);
                }
                else
                {
                    SetState(SimulacrumState.StartingNewMap);
                }
            }
            else
            {
                ResetForNewMap();
                SetState(SimulacrumState.FindingMonolith);
            }
        }

        public void ResetForNewMap()
        {
            CurrentWave = 0;
            _waveDeathCounter = 0;
            _lastWaveWithDeath = -1;
            _anchorPosition = null;
            _explorationTargets.Clear();
            _pathingBlacklist.Clear();
            _instanceStartTime = DateTime.Now;
            waveEndedAt = DateTime.MinValue;
            FindStash();
        }

        private void SetActionDelay(int delayMs = 150)
        {
            _nextActionTime = DateTime.Now.AddMilliseconds(delayMs + _random.Next(50, 150));
        }

        private void UpdateWaveStatus()
        {
            _simulacrumMonolithCache = _gameController.EntityListWrapper.OnlyValidEntities
                .FirstOrDefault(e => e.Type == EntityType.IngameIcon && e.Metadata.Contains(SimulacrumMonolithMetadata));

            if (_simulacrumMonolithCache != null)
            {
                var stateMachine = _simulacrumMonolithCache.GetComponent<StateMachine>();
                if (stateMachine != null)
                {
                    var waveState = stateMachine.States.FirstOrDefault(s => s.Name == "wave");
                    long newWave = waveState?.Value ?? CurrentWave;

                    if (newWave > CurrentWave)
                    {
                        _waveDeathCounter = 0;
                        waveStartedAt = DateTime.Now;
                        hasBreached = false;
                    }
                    CurrentWave = newWave;

                    var activeState = stateMachine.States.FirstOrDefault(s => s.Name == "active");
                    var goodbyeState = stateMachine.States.FirstOrDefault(s => s.Name == "goodbye");

                    var newWaveIsActive = (activeState?.Value > 0) && (goodbyeState?.Value < 1);

                    if (IsWaveActive && !newWaveIsActive)
                        waveEndedAt = DateTime.Now;

                    IsWaveActive = newWaveIsActive;
                }
            }
        }


        DateTime lastChangedStatedAt = DateTime.Now;
        private void SetState(SimulacrumState newState, string errorMessage = "")
        {
            if (CurrentState == newState) return;

            Console.WriteLine($"Changing state from {CurrentState} to {newState}");
            CurrentState = newState;
            _currentPath = null;
            lastChangedStatedAt = DateTime.Now;
            if (newState == SimulacrumState.Error)
                ErrorMessage = errorMessage;

            _searchStartTime = DateTime.Now;
        }

        private void HandleFindingMonolithState()
        {
            if (_simulacrumMonolithCache != null)
            {
                _anchorPosition = _simulacrumMonolithCache.GridPosNum;
                SetState(SimulacrumState.Starting);
            }
            else
            {
                HandleExploringState();
            }
        }

        private void HandleLeavingMapState()
        {
            if (ClosestValidGroundItem != null)
            {
                SetState(SimulacrumState.Looting);
                return;
            }

            var portal = _gameController.EntityListWrapper.ValidEntitiesByType[EntityType.TownPortal]
                            .OrderBy(p => p.DistancePlayer)
                            .FirstOrDefault();

            SetActionDelay();
            if (portal != null)
            {
                if (portal.DistancePlayer > _settings.NodeSize * 2)
                {
                    if (_currentPath == null) CreateNewPath(portal.GridPosNum);
                    _currentPath?.FollowPath(_gameController, _settings);
                }
                else
                {
                    Controls.ClickScreenPos(Controls.GetScreenByWorldPos(portal.BoundsCenterPosNum));

                    SetActionDelay(1000);
                }
            }
            else
            {
                Controls.ClickScreenPos(new Vector2(200,200), false);
                SetActionDelay(3000);
            }
        }

        private void HandleStartingState()
        {
            if (CurrentWave >= MaxWaves)
            {
                SetState(SimulacrumState.Finished);
                return;
            }
            if (IsWaveActive)
            {
                SetState(SimulacrumState.ReturningToAnchor);
                return;
            }


            var itemToLoot = ClosestValidGroundItem;
            if (itemToLoot != null && !IsInventoryFull())
            {
                CreateNewPath(itemToLoot.Entity.GridPosNum);
                SetState(SimulacrumState.Looting);
            }

            //Don't start wave if delay isn't finished.
            if (waveEndedAt.AddSeconds(_settings.WaveEndDelay.Value) > DateTime.Now) 
                return;

                if (_simulacrumMonolithCache != null && _simulacrumMonolithCache.IsTargetable)
                {
                    var monolithPos = _simulacrumMonolithCache.GridPosNum;
                    if (monolithPos.Distance(_gameController.Player.GridPosNum) > _settings.NodeSize)
                    {
                        if (_currentPath == null) CreateNewPath(monolithPos);
                        _currentPath?.FollowPath(_gameController, _settings);
                    }
                    else
                    {
                        _currentPath = null;
                        var screenPos = Controls.GetScreenByWorldPos(_simulacrumMonolithCache.BoundsCenterPosNum);
                        Controls.ClickScreenPos(screenPos);
                    }
                    SetActionDelay();
                }
                else
                {
                    if (DateTime.Now - _searchStartTime > TimeSpan.FromSeconds(SearchTimeoutSeconds))
                    {
                        SetState(SimulacrumState.Exploring);
                    }
                }
        }

        private void HandleEnteringMapState()
        {
            if (!_gameController.Area.CurrentArea.IsHideout)
            {
                SetState(SimulacrumState.ReturningToAnchor);
                return;
            }

            var portal = _gameController.EntityListWrapper.ValidEntitiesByType[EntityType.TownPortal]
                .OrderBy(e => e.DistancePlayer)
                .FirstOrDefault();

            if (portal == null)
            {
                if (DateTime.Now - _searchStartTime > TimeSpan.FromSeconds(SearchTimeoutSeconds))                
                    SetState(SimulacrumState.StartingNewMap);                
                else
                {
                    SetActionDelay(250); // Wait a bit for portal to load
                }
                return;
            }

            if (portal.DistancePlayer > 15)
            {
                if (_currentPath == null) CreateNewPath(portal.GridPosNum);
                _currentPath?.FollowPath(_gameController, _settings);
            }
            else
            {
                _currentPath = null;
                Controls.ClickScreenPos(Controls.GetScreenByWorldPos(portal.BoundsCenterPosNum));
            }
            SetActionDelay(1000);
        }

        private void HandleStartingNewMapState()
        {
            if (!_gameController.Area.CurrentArea.IsHideout)
            {
                SetState(SimulacrumState.Error, "Attempted to start a new map while not in Hideout.");
                return;
            }

            SetActionDelay();
            var mapDeviceWindow = _gameController.IngameState.IngameUi.MapDeviceWindow;
            if (!mapDeviceWindow.IsVisible)
            {
                var mapDevice = _gameController.EntityListWrapper.OnlyValidEntities.FirstOrDefault(I => I.Type == EntityType.IngameIcon && I.Path.EndsWith("MappingDevice"));
                if (mapDevice == null)
                {
                    if (DateTime.Now - _searchStartTime > TimeSpan.FromSeconds(SearchTimeoutSeconds))                    
                        SetState(SimulacrumState.Error, "Could not find the Map Device in hideout after 10 seconds.");                    
                    return;
                }

                if (_gameController.Player.GridPosNum.Distance(mapDevice.GridPosNum) > _settings.NodeSize * 2)
                {
                    if(_currentPath == null || _currentPath.Nodes.Count <=1)
                        CreateNewPath(mapDevice.GridPosNum);
                }
                else
                {
                    Controls.ClickScreenPos(Controls.GetScreenByWorldPos(mapDevice.BoundsCenterPosNum));
                    SetActionDelay(1000);
                }
                _currentPath?.FollowPath(_gameController, _settings);
                return;
            }

            var activateButton = mapDeviceWindow.ActivateButton;
            if (activateButton.IsActive)
            {
                var center = activateButton.GetClientRect().Center;
                Controls.ClickScreenPos(new Vector2(center.X, center.Y));
                SetActionDelay(5000);
                SetState(SimulacrumState.EnteringMap);
            }
            else
            {
                var mapStashPanel = mapDeviceWindow.GetChildFromIndices(0, 1);
                var anySimulacrum = mapStashPanel?.Children.FirstOrDefault(I => I.TextureName.EndsWith("DeliriumFragment.dds"));

                if (anySimulacrum == null)
                {
                    SetState(SimulacrumState.Error, "No Simulacrums Found in Map Device");
                    return;
                }

                var center = anySimulacrum.GetClientRect().Center;
                Controls.ClickScreenPos(new Vector2(center.X, center.Y), true, false, true);
                SetActionDelay(500);
            }
        }

        private void HandleReturningToAnchorState()
        {
            if (!_anchorPosition.HasValue) { SetState(SimulacrumState.CombatHold); return; }
            if (_gameController.Player.GridPosNum.Distance(_anchorPosition.Value) <= _settings.NodeSize) { SetState(SimulacrumState.CombatHold); return; }

            if (_currentPath == null) CreateNewPath(_anchorPosition.Value);
            _currentPath?.FollowPath(_gameController, _settings, true);
            SetActionDelay();
        }

        private void HandleCombatHoldState()
        {
            if (!IsWaveActive)
            {
                if (CurrentWave >= MaxWaves)
                {
                    SetState(SimulacrumState.Finished);
                }
                else
                {
                    SetState(SimulacrumState.Looting);
                }
                return;
            }

            if (DateTime.Now > waveStartedAt.AddSeconds(40) && !hasBreached)
            {
                Controls.UseKey(Keys.T);
                hasBreached = true;
            }

            var allMonsters = _gameController.EntityListWrapper.OnlyValidEntities.Where(IsMonster).ToList();
            if (!allMonsters.Any()) { SetState(SimulacrumState.Exploring); return; }

            var playerPos = _gameController.Player.GridPosNum;
            var monstersInCombatRange = allMonsters.Where(m => m.GridPosNum.Distance(playerPos) < _settings.CombatDistance).ToList();

            CastCombatSpells(monstersInCombatRange);

            if (monstersInCombatRange.Any())
            {
                _lastMonsterInRangeTime = DateTime.Now;
                _currentPath = null;
            }
            else
            {
                if (DateTime.Now - _lastMonsterInRangeTime > TimeSpan.FromSeconds(RepositionDelaySeconds))
                {
                    SetState(SimulacrumState.CombatSeek);
                    return;
                }
                if (_anchorPosition.HasValue && playerPos.Distance(_anchorPosition.Value) > _settings.NodeSize)
                {
                    if (_currentPath == null) CreateNewPath(_anchorPosition.Value);
                }
            }

            _currentPath?.FollowPath(_gameController, _settings, true);
            SetActionDelay();
        }

        private void HandleCombatSeekState()
        {
            if (!IsWaveActive) { SetState(SimulacrumState.Looting); return; }
            var allMonsters = _gameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster].ToList();
            if (!allMonsters.Any()) { SetState(SimulacrumState.Exploring); return; }

            CastCombatSpells(allMonsters);

            if (_currentPath == null || _currentPath.Next == null)
            {
                var bestTargetToMoveTo = allMonsters
                    .Where(I=>I.IsAlive && !I.IsDead && I.IsValid && I.IsTargetable && I.IsHostile)
                    .Where(m => !_pathingBlacklist.ContainsKey(m.GridPosNum) || _pathingBlacklist[m.GridPosNum] < DateTime.Now)
                    .OrderByDescending(m => _map.GetPositionFightWeight(m.GridPosNum))
                    .FirstOrDefault();

                if (bestTargetToMoveTo != null)
                    CreateNewPath(bestTargetToMoveTo.GridPosNum);
                
                else { SetState(SimulacrumState.Exploring); return; }
            }

            if (_currentPath != null && _gameController.Player.GridPosNum.Distance(_currentPath.Destination) < _settings.CombatDistance)
            {
                SetState(SimulacrumState.CombatHold);
                return;
            }

            _currentPath?.FollowPath(_gameController, _settings, true);
            SetActionDelay();
        }

        private void HandleExploringState()
        {
            if (_gameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster]
                .Where(I => I.IsAlive && !I.IsDead && I.IsValid && I.IsTargetable && I.IsHostile)
                .Any())
            { SetState(SimulacrumState.CombatHold); return; }

            if (!IsWaveActive && CurrentWave > 0) { SetState(SimulacrumState.Looting); return; }

            if (_currentPath == null || _currentPath.Next == null)
            {
                var nextChunk = FindNextExplorationChunk();
                if (nextChunk != null)
                {
                    CreateNewPath(nextChunk.Position);
                    _explorationTargets[nextChunk.Position] = DateTime.Now;
                }
                else { SetState(SimulacrumState.Looting); return; }
            }
            _currentPath?.FollowPath(_gameController, _settings);
            SetActionDelay();
        }

        private Chunk FindNextExplorationChunk()
        {
            DateTime cooldown = DateTime.Now.AddSeconds(-ExplorationCooldownSeconds);
            return _map.Chunks
                .Where(c => !c.IsRevealed && c.Weight > 0 && (!_explorationTargets.ContainsKey(c.Position) || _explorationTargets[c.Position] < cooldown))
                .OrderByDescending(c => c.Position.Distance(_gameController.Player.GridPosNum))
                .ThenByDescending(c => c.Weight)
                .FirstOrDefault();
        }

        private void HandleLootingState()
        {
            if (IsInventoryFull()) { SetState(SimulacrumState.Stashing); return; }

            SetActionDelay();

            var expiredKeys = _itemBlacklist.Where(kvp => kvp.Value < DateTime.Now).Select(I => I.Key).ToList();
            foreach (var key in expiredKeys)
                _itemBlacklist.Remove(key);

            var itemToLoot = ClosestValidGroundItem;
            if (itemToLoot != null)
            {
                var itemPosition = itemToLoot.Entity.GridPosNum;
                if (_gameController.Player.GridPosNum.Distance(itemPosition) > _settings.NodeSize)
                {

                    debugDrawPos = Controls.GetScreenByGridPos(itemPosition);
                    debugDrawText = "pathing to loot";

                    if (_currentPath == null || _currentPath.Nodes.Count < 1)
                        CreateNewPath(itemPosition);
                    _currentPath?.FollowPath(_gameController, _settings);
                }
                else
                {
                    if (_lootingItemId != itemToLoot.Entity.Id)
                    {
                        _lootingItemId = itemToLoot.Entity.Id;
                        _lootAttemptCounter = 0;
                    }


                    debugDrawPos = Controls.GetScreenByGridPos(itemPosition);
                    debugDrawText = "picking up loot";

                    _lootAttemptCounter++;

                    if (_lootAttemptCounter > MaxLootAttempts)
                    {
                        Console.WriteLine($"Blacklisting item {itemToLoot.Entity.Id} after {_lootAttemptCounter} failed attempts.");
                        _itemBlacklist[_lootingItemId] = DateTime.Now.AddSeconds(ItemBlacklistSeconds);

                        _lootingItemId = 0;
                        _lootAttemptCounter = 0;
                        _currentPath = null;
                        return;
                    }

                    // Attempt to loot the item
                    var center = itemToLoot.Label.GetClientRect().Center;
                    Controls.ClickScreenPos(new Vector2(center.X, center.Y));

                    SetActionDelay(500);
                    _stopLooting = DateTime.Now.AddMilliseconds(1000);

                }
            }
            else if (DateTime.Now > _stopLooting)
            {
                debugDrawText = "No loot found";
                debugDrawPos = new Vector2(400, 400);

                _lootingItemId = 0;
                _lootAttemptCounter = 0;

                SetState(CurrentWave >= MaxWaves ? SimulacrumState.LeavingMap : SimulacrumState.Starting);
            }
        }

        private void HandleStashingState()
        {
            if (!_stashPosition.HasValue) FindStash();
            if (!_stashPosition.HasValue) { SetState(SimulacrumState.Error, "Could not find stash."); return; }

            if (_gameController.Player.GridPosNum.Distance(_stashPosition.Value) > _settings.NodeSize)
            {
                if (_currentPath == null || _currentPath.Nodes.Count < 1)
                    CreateNewPath(_stashPosition.Value);

                debugDrawPos = Controls.GetScreenByGridPos(_stashPosition.Value);
                debugDrawText = "navigating to stash";


                _currentPath?.FollowPath(_gameController, _settings);
                SetActionDelay(250);
            }
            else
            {
                var toStore = _gameController.IngameState.Data.ServerData.PlayerInventories[0].Inventory.InventorySlotItems.FirstOrDefault(i => i.PosX < 11);
                if (toStore == null)
                {
                    _stashAttemptCounter = 0;
                    SetStashWindowState(false);
                    SetState(SimulacrumState.Looting);
                    return;
                }

                if (_stashAttemptCounter >= MaxStashAttempts)
                {
                    SetState(SimulacrumState.Error, "Failed to stash item after multiple attempts. Stash may be full.");
                    return;
                }

                SetStashWindowState(true);
                if (!_gameController.IngameState.IngameUi.InventoryPanel.IsVisible)
                {
                    debugDrawPos = Controls.GetScreenByGridPos(_stashPosition.Value);
                    debugDrawText = "waiting for stash to open";

                    return;
                }
                if (storingId != toStore.Item.Id)
                {
                    _stashAttemptCounter = 0;
                    storingId = toStore.Item.Id;
                }

                debugDrawPos = Controls.GetScreenByGridPos(_stashPosition.Value);
                debugDrawText = "storing item in open stash";
                var center = toStore.GetClientRect().Center;
                Controls.ClickScreenPos(new Vector2(center.X, center.Y), true, false, true);
                _stashAttemptCounter++;
                SetActionDelay();
            }
        }

        public ItemsOnGroundLabelElement.VisibleGroundItemDescription? ClosestValidGroundItem =>
            _gameController.IngameState.IngameUi.ItemsOnGroundLabelElement.VisibleGroundItemLabels
                .Where(item => item != null &&
                               item.Label != null &&
                               item.Entity != null &&
                               item.Label.IsVisibleLocal &&
                               item.Label.Text != null &&
                               !item.Label.Text.EndsWith(MapConstants.GoldItemSuffix) &&
                         !_itemBlacklist.ContainsKey(item.Entity.Id))
                .OrderBy(item => item.Entity.DistancePlayer)
                .FirstOrDefault();

        private void SetStashWindowState(bool visible)
        {
            var isVisible = _gameController.IngameState.IngameUi.InventoryPanel.IsVisible;
            if (visible)
            {
                if (isVisible) return;
                var stash = _gameController.EntityListWrapper.OnlyValidEntities.FirstOrDefault(e => e.Metadata.Contains(StashMetadata));
                if (stash == null) return;
                Controls.ClickScreenPos(Controls.GetScreenByWorldPos(stash.BoundsCenterPosNum));
            }
            else
            {
                if (!isVisible) return;
                Controls.UseKey(Keys.Escape);
            }
        }

        private void HandleDeathState()
        {
            if (_waveDeathCounter > MaxWaveDeaths)
            {
                SetState(SimulacrumState.Error, $"Died more than {MaxWaveDeaths} times on wave {CurrentWave}. Abandoning run.");
                return;
            }

            if (!_gameController.Player.IsDead) return;

            var resurrectionButton = _gameController.IngameState.IngameUi.ResurrectPanel;
            if (resurrectionButton != null && resurrectionButton.IsVisible)
            {
                var resurectAtCheckpoint = resurrectionButton.ResurrectAtCheckpoint;
                if (resurectAtCheckpoint != null)
                {
                    var center = resurectAtCheckpoint.GetClientRect().Center;
                    Controls.ClickScreenPos(new Vector2(center.X, center.Y));
                    SetActionDelay(3000);
                }
            }
        }

        private void CheckForStuckConditions()
        {
            if (_currentPath == null) return;

            var timeoutSeconds = Math.Clamp(_currentPath.InitialDistance / 100f, 5, 20);
            if ((DateTime.Now - _currentPath.CreationTime).TotalSeconds > timeoutSeconds)
            {
                if (_currentPath.Destination != null)
                    _pathingBlacklist[_currentPath.Destination] = DateTime.Now.AddSeconds(PathBlacklistSeconds);
                _currentPath = null;
            }
        }

        private void CastCombatSpells(List<Entity> monsters)
        {
            if (_gameController.Player.Buffs.FirstOrDefault(b => b.Name == "righteous_fire") == null) Controls.UseKey(Keys.R);
            if (monsters.Any() && DateTime.Now > _nextSpellCastTime)
            {
                var bestTarget = monsters.OrderByDescending(m => Navigation.Map.GetMonsterRarityWeight(m.Rarity)).FirstOrDefault();
                if (bestTarget != null)
                {
                    Controls.UseKeyAtGridPos(bestTarget.GridPosNum, _settings.CombatKey.Value);
                    _nextSpellCastTime = DateTime.Now.AddMilliseconds(SpellCastIntervalMs + _random.Next(50, 150));
                }
            }
        }

        private void FindStash()
        {
            var stash = _gameController.EntityListWrapper.OnlyValidEntities
                .FirstOrDefault(e => e.Metadata.Contains(StashMetadata));
            if (stash != null)
            {
                _stashPosition = stash.GridPosNum;
            }
        }

        private int GetStorableInventoryCount => _gameController.IngameState.Data.ServerData.PlayerInventories[0].Inventory.InventorySlotItems.Count(I => I.PosX < 11);

        private bool IsInventoryFull()
        {
            var stash = _gameController.EntityListWrapper.OnlyValidEntities
                .FirstOrDefault(e => e.Metadata.Contains(StashMetadata));
            return stash != null &&  GetStorableInventoryCount >= _settings.StoreInventoryCount.Value;
        }

        public void Render()
        {
            var position = new Vector2(200, 250);
            var lineHeight = 20;

            var lines = new List<string>
            {
                $"State: {CurrentState}",
                $"Goal: {GetGoalDescription()}",
                $"Action: {GetActionDescription()}",
                $"Wave: {CurrentWave} / {MaxWaves}",
                $"Wave Active: {IsWaveActive}",
                $"Navigating To: {GetNavigationTarget()}",
                $"Path Nodes: {_currentPath?.Nodes.Count}",
                $"Anchor Point: {_anchorPosition?.ToString() ?? "Not Set"}",
                $"Error: {ErrorMessage}"
            };

            foreach (var line in lines)
            {
                _graphics.DrawText(line, position);
                position.Y += lineHeight;
            }

            if (_anchorPosition.HasValue)
            {
                var screenPos = Controls.GetScreenByGridPos(_anchorPosition.Value);
                _graphics.DrawCircle(screenPos, 15, SharpDX.Color.Cyan, 3);
            }

            if (_currentPath != null)
            {
                foreach (var node in _currentPath.Nodes)
                {
                    var screenPos = Controls.GetScreenByGridPos(node);
                    _graphics.DrawCircle(screenPos, 10, SharpDX.Color.White);
                }
            }

            _graphics.DrawText(debugDrawText, debugDrawPos);
        }

        private bool IsMonster(Entity e) => e != null && e.Type == EntityType.Monster && e.IsAlive && !e.IsDead && e.IsTargetable&& e.IsHostile;

        private string GetGoalDescription()
        {
            switch (CurrentState)
            {
                case SimulacrumState.Starting:
                case SimulacrumState.FindingMonolith:
                    return "Start the next wave.";
                case SimulacrumState.CombatHold:
                case SimulacrumState.CombatSeek:
                    return "Kill all monsters.";
                case SimulacrumState.Exploring:
                    return "Find remaining monsters or monolith.";
                case SimulacrumState.Looting:
                    return "Pick up valuable items.";
                case SimulacrumState.Stashing:
                    return "Deposit items into stash.";
                case SimulacrumState.Finished:
                case SimulacrumState.LeavingMap:
                    return "Simulacrum is complete, leaving map.";
                default:
                    return "Idle.";
            }
        }

        private string GetActionDescription()
        {
            if (_currentPath?.Next != null)
            {
                return "Following path...";
            }

            switch (CurrentState)
            {
                case SimulacrumState.Starting:
                    return _simulacrumMonolithCache != null ? "Interacting with Monolith" : "Searching for Monolith";
                case SimulacrumState.FindingMonolith:
                    return "Exploring to find Monolith for anchor point.";
                case SimulacrumState.LeavingMap:
                    return "Casting portal or leaving map.";
                case SimulacrumState.CombatHold:
                    return "Holding position, fighting nearby enemies.";
                case SimulacrumState.CombatSeek:
                    return "Seeking distant enemies.";
                case SimulacrumState.Exploring:
                    return "Calculating new exploration path";
                case SimulacrumState.Looting:
                    return ClosestValidGroundItem != null ? "Moving to item" : "Searching for items";
                case SimulacrumState.Stashing:
                    return "Interacting with stash";
                default:
                    return "No specific action.";
            }
        }

        private string GetNavigationTarget()
        {
            return _currentPath?.Destination.ToString() ?? "None";
        }
    }
}