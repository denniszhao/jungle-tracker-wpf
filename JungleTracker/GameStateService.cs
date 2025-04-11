using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Timers;
using Timer = System.Timers.Timer;

namespace JungleTracker
{
    /// <summary>
    /// Event arguments class for game state updates.
    /// </summary>
    public class GameStateEventArgs : EventArgs
    {
        public string EnemyJunglerChampionName { get; }
        public string EnemyJunglerTeam { get; }
        public bool IsEnemyJunglerDead { get; }
        public double EnemyJunglerRespawnTimer { get; }
        public Point? EnemyJunglerLastSeenLocation { get; }
        public bool IsEnemyJunglerVisibleOnMinimap { get; }
        public string ActivePlayerTeam { get; }

        public GameStateEventArgs(
            string enemyJunglerChampionName,
            string enemyJunglerTeam,
            bool isEnemyJunglerDead,
            double enemyJunglerRespawnTimer,
            Point? enemyJunglerLastSeenLocation,
            bool isEnemyJunglerVisibleOnMinimap,
            string activePlayerTeam)
        {
            EnemyJunglerChampionName = enemyJunglerChampionName;
            EnemyJunglerTeam = enemyJunglerTeam;
            IsEnemyJunglerDead = isEnemyJunglerDead;
            EnemyJunglerRespawnTimer = enemyJunglerRespawnTimer;
            EnemyJunglerLastSeenLocation = enemyJunglerLastSeenLocation;
            IsEnemyJunglerVisibleOnMinimap = isEnemyJunglerVisibleOnMinimap;
            ActivePlayerTeam = activePlayerTeam;
        }
    }

    /// <summary>
    /// Centralized service to manage and combine game state from multiple sources.
    /// </summary>
    public class GameStateService : IDisposable
    {
        // Event that fires when game state is updated
        public event Action<GameStateEventArgs>? GameStateUpdated;

        // Private fields for dependencies
        private readonly LeagueClientService _leagueClientService;
        private MinimapCaptureService? _minimapCaptureService;
        private MinimapScannerService? _minimapScannerService;
        
        // Private fields for state
        private string _enemyJunglerChampionName = string.Empty;
        private string _enemyJunglerTeam = string.Empty;
        private bool _isEnemyJunglerDead = false;
        private double _enemyJunglerRespawnTimer = 0.0;
        private Point? _enemyJunglerLastSeenLocation = null;
        private bool _isEnemyJunglerVisibleOnMinimap = false;
        private string _activePlayerTeam = string.Empty;
        
        // Private fields for capture parameters
        private double _currentOverlaySize = 0.0;
        private double _currentCaptureSize = 0.0;
        
        // Timer for continuous updates
        private Timer? _updateTimer;
        private bool _isUpdating = false;

        /// <summary>
        /// Public properties to expose the current state
        /// </summary>
        public string EnemyJunglerChampionName => _enemyJunglerChampionName;
        public string EnemyJunglerTeam => _enemyJunglerTeam;
        public bool IsEnemyJunglerDead => _isEnemyJunglerDead;
        public double EnemyJunglerRespawnTimer => _enemyJunglerRespawnTimer;
        public Point? EnemyJunglerLastSeenLocation => _enemyJunglerLastSeenLocation;
        public bool IsEnemyJunglerVisibleOnMinimap => _isEnemyJunglerVisibleOnMinimap;
        public string ActivePlayerTeam => _activePlayerTeam;

        /// <summary>
        /// Initializes a new instance of the GameStateService class.
        /// </summary>
        /// <param name="leagueClientService">Service to interact with League Client API.</param>
        public GameStateService(LeagueClientService leagueClientService)
        {
            _leagueClientService = leagueClientService ?? throw new ArgumentNullException(nameof(leagueClientService));
        }

        /// <summary>
        /// Registers minimap services that will be used for capturing and scanning the minimap.
        /// </summary>
        /// <param name="captureService">Service for capturing minimap screenshots.</param>
        /// <param name="scannerService">Service for scanning minimap images for champions.</param>
        public void RegisterMinimapServices(MinimapCaptureService captureService, MinimapScannerService scannerService)
        {
            _minimapCaptureService = captureService ?? throw new ArgumentNullException(nameof(captureService));
            _minimapScannerService = scannerService ?? throw new ArgumentNullException(nameof(scannerService));
            Debug.WriteLine("[GameStateService] Minimap services registered successfully.");
        }

        /// <summary>
        /// Initializes the enemy jungler data by fetching initial information from the League Client API.
        /// </summary>
        /// <returns>True if initialization was successful, false otherwise.</returns>
        public async Task<bool> InitializeEnemyJunglerDataAsync()
        {
            try
            {
                Debug.WriteLine("[GameStateService] Initializing enemy jungler data from League Client API...");
                bool success = await _leagueClientService.TryGetGameDataAsync();
                
                if (success)
                {
                    _enemyJunglerChampionName = _leagueClientService.EnemyJunglerChampionName;
                    _enemyJunglerTeam = _leagueClientService.ActivePlayerTeam == "ORDER" ? "CHAOS" : "ORDER";
                    _activePlayerTeam = _leagueClientService.ActivePlayerTeam;
                    _isEnemyJunglerDead = _leagueClientService.EnemyJunglerIsDead;
                    _enemyJunglerRespawnTimer = _leagueClientService.EnemyJunglerRespawnTimer;
                    
                    Debug.WriteLine($"[GameStateService] Successfully initialized enemy jungler data: {_enemyJunglerChampionName} ({_enemyJunglerTeam})");
                    Debug.WriteLine($"[GameStateService] Enemy jungler is {(_isEnemyJunglerDead ? "dead" : "alive")}{(_isEnemyJunglerDead ? $", respawning in {_enemyJunglerRespawnTimer:F1} seconds" : "")}");
                    
                    // Raise initial state update event
                    RaiseGameStateUpdated();
                    return true;
                }
                else
                {
                    Debug.WriteLine("[GameStateService] Failed to initialize enemy jungler data from League Client API.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GameStateService] Error during initialization: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Updates the overlay parameters used for minimap capture and scanning.
        /// </summary>
        /// <param name="overlaySize">The size of the overlay window.</param>
        /// <param name="captureSize">The size of the minimap capture area.</param>
        public void UpdateOverlayParameters(double overlaySize, double captureSize)
        {
            _currentOverlaySize = overlaySize;
            _currentCaptureSize = captureSize;
            Debug.WriteLine($"[GameStateService] Updated overlay parameters: Overlay={_currentOverlaySize}, Capture={_currentCaptureSize}");
        }

        /// <summary>
        /// Starts continuous updates of the game state with the specified interval.
        /// </summary>
        /// <param name="interval">The time interval between updates.</param>
        /// <param name="initialOverlaySize">The initial size of the overlay window.</param>
        /// <param name="initialCaptureSize">The initial size of the minimap capture area.</param>
        public void StartContinuousUpdates(TimeSpan interval, double initialOverlaySize, double initialCaptureSize)
        {
            if (_isUpdating)
            {
                Debug.WriteLine("[GameStateService] Continuous updates already running.");
                return;
            }
            
            if (_minimapCaptureService == null || _minimapScannerService == null)
            {
                Debug.WriteLine("[GameStateService] Cannot start continuous updates: Minimap services not registered.");
                return;
            }
            
            _currentOverlaySize = initialOverlaySize;
            _currentCaptureSize = initialCaptureSize;
            
            _updateTimer = new Timer(interval.TotalMilliseconds);
            _updateTimer.Elapsed += async (s, e) => await UpdateGameStateAsync();
            _updateTimer.AutoReset = true;
            _updateTimer.Start();
            _isUpdating = true;
            
            Debug.WriteLine($"[GameStateService] Started continuous updates with interval {interval.TotalMilliseconds}ms");
        }

        /// <summary>
        /// Stops continuous updates of the game state.
        /// </summary>
        public void StopContinuousUpdates()
        {
            if (!_isUpdating) return;
            
            _updateTimer?.Stop();
            _updateTimer?.Dispose();
            _updateTimer = null;
            _isUpdating = false;
            
            Debug.WriteLine("[GameStateService] Stopped continuous updates");
        }

        /// <summary>
        /// Updates the game state by fetching API data and performing minimap scanning.
        /// </summary>
        private async Task UpdateGameStateAsync()
        {
            if (_minimapCaptureService == null || _minimapScannerService == null)
            {
                Debug.WriteLine("[GameStateService] Cannot update game state: Minimap services not registered.");
                return;
            }
            
            try
            {
                // 1. Update API data (always attempt this)
                bool apiSuccess = await _leagueClientService.TryGetGameDataAsync();
                
                if (apiSuccess)
                {
                    // Update basic info in case it changed (rare, but possible)
                    _enemyJunglerChampionName = _leagueClientService.EnemyJunglerChampionName;
                    _enemyJunglerTeam = _leagueClientService.ActivePlayerTeam == "ORDER" ? "CHAOS" : "ORDER";
                    _activePlayerTeam = _leagueClientService.ActivePlayerTeam;
                    
                    // Always update death status and respawn timer
                    bool wasAliveBeforeUpdate = !_isEnemyJunglerDead;
                    _isEnemyJunglerDead = _leagueClientService.EnemyJunglerIsDead;
                    _enemyJunglerRespawnTimer = _leagueClientService.EnemyJunglerRespawnTimer;
                    
                    // If the jungler was alive and is now dead, or if they respawned (was dead, now alive)
                    // reset the last seen location
                    if ((wasAliveBeforeUpdate && _isEnemyJunglerDead) || (!wasAliveBeforeUpdate && !_isEnemyJunglerDead))
                    {
                        _enemyJunglerLastSeenLocation = null;
                        _isEnemyJunglerVisibleOnMinimap = false;
                    }
                    
                    Debug.WriteLine($"[GameStateService] API update: Enemy jungler {_enemyJunglerChampionName} is {(_isEnemyJunglerDead ? "dead" : "alive")}{(_isEnemyJunglerDead ? $", respawning in {_enemyJunglerRespawnTimer:F1} seconds" : "")}");
                }
                else
                {
                    Debug.WriteLine("[GameStateService] Failed to update from League Client API");
                }
                
                // 2. Reset minimap visibility for this update cycle
                _isEnemyJunglerVisibleOnMinimap = false;
                
                // 3. Only perform minimap check if jungler is alive
                if (!_isEnemyJunglerDead && !string.IsNullOrEmpty(_enemyJunglerChampionName) && !string.IsNullOrEmpty(_enemyJunglerTeam))
                {
                    using (Bitmap? minimapImage = _minimapCaptureService.CaptureCurrentFrame(_currentOverlaySize, _currentCaptureSize))
                    {
                        if (minimapImage != null)
                        {
                            // Ensure scanner has the correct template set
                            if (_minimapScannerService.SetChampionTemplate(_enemyJunglerChampionName, _enemyJunglerTeam))
                            {
                                // Scan for the champion
                                Point? location = _minimapScannerService.ScanForChampion(minimapImage);
                                
                                if (location.HasValue)
                                {
                                    _isEnemyJunglerVisibleOnMinimap = true;
                                    _enemyJunglerLastSeenLocation = location;
                                    Debug.WriteLine($"[GameStateService] Minimap scan: Enemy jungler visible at ({location.Value.X}, {location.Value.Y})");
                                }
                                else
                                {
                                    Debug.WriteLine("[GameStateService] Minimap scan: Enemy jungler not visible");
                                }
                            }
                            else
                            {
                                Debug.WriteLine($"[GameStateService] Failed to set champion template for {_enemyJunglerChampionName} ({_enemyJunglerTeam})");
                            }
                        }
                        else
                        {
                            Debug.WriteLine("[GameStateService] Minimap capture failed or skipped");
                        }
                    } // Dispose minimapImage
                }
                else if (_isEnemyJunglerDead)
                {
                    Debug.WriteLine("[GameStateService] Skipping minimap scan because enemy jungler is dead");
                }
                
                // 4. Raise event with updated state
                RaiseGameStateUpdated();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GameStateService] Error during game state update: {ex.Message}");
            }
        }

        /// <summary>
        /// Raises the GameStateUpdated event with the current state.
        /// </summary>
        private void RaiseGameStateUpdated()
        {
            GameStateUpdated?.Invoke(new GameStateEventArgs(
                _enemyJunglerChampionName,
                _enemyJunglerTeam,
                _isEnemyJunglerDead,
                _enemyJunglerRespawnTimer,
                _enemyJunglerLastSeenLocation,
                _isEnemyJunglerVisibleOnMinimap,
                _activePlayerTeam));
        }

        /// <summary>
        /// Disposes resources used by the service.
        /// </summary>
        public void Dispose()
        {
            StopContinuousUpdates();
            
            // We don't dispose _leagueClientService, _minimapCaptureService, or _minimapScannerService
            // because they are injected dependencies and might be used elsewhere
        }
    }
} 