using System;
using System.IO;
using System.Windows;
// Remove System.Windows.Shapes which is causing Rectangle conflict
using System.Timers;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Threading.Tasks; // Needed for Task.Run
using System.Windows.Media.Imaging;
using System.Windows.Media; // Add for Colors
using System.Windows.Media.Animation; // Add for animations
using System.Collections.Generic; // Needed for Dictionary
using System.Linq; // Needed for LINQ operations in point-in-polygon
using System.Windows.Shapes; // Needed for Polygon class

namespace JungleTracker
{
    // Explicitly specify System.Windows.Window
    public partial class OverlayWindow : System.Windows.Window
    {
        // Debug mode constant - set to true during development, false for production
#if DEBUG
        private const bool DEBUG_MODE = true;
#else
        private const bool DEBUG_MODE = false;
#endif

        // --- Core constants ---
        private const double MIN_MINIMAP_SIZE = 280.0;
        private const double MAX_MINIMAP_SIZE = 560.0;
        private const double MIN_CAPTURE_SIZE = 254.0;
        private const double MAX_CAPTURE_SIZE = 510.0;
        private const int DEFAULT_SCREENSHOT_INTERVAL = 1000; // Renamed
        
        // --- New animation constants ---
        private const int FADE_DURATION_MS = 10000;
        private const double FINAL_FADE_OPACITY = 0.3; 
        
        // --- Instance variables ---
        private double _overlaySize;
        private double _captureSize;

        // Animation fields moved to PortraitManager
        private System.Drawing.Point? _lastKnownLocation;

        // --- Services and Managers ---
        private MinimapScannerService _minimapScanner;
        private MinimapCaptureService _minimapCaptureService; 
        private PortraitManager _enemyJunglerPortraitManager; 
        private ZoneManager _zoneManager; // Add ZoneManager field
        private GameStateService _gameStateService; // Add GameStateService field

        // List to hold references to the XAML zone polygons
        private List<Polygon> _allZonePolygons;

        // --- Brushes for Zone Highlighting ---
        private static readonly System.Windows.Media.Brush _defaultZoneFill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(30, 255, 0, 0)); // #55FF0000
        private static readonly System.Windows.Media.Brush _highlightZoneFill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(30, 255, 255, 0)); // #55FFFF00

        /// <summary>
        /// Initializes a new instance of the OverlayWindow class.
        /// Sets up window properties, initializes components, populates zone polygons,
        /// applies debug visuals if enabled, and initializes services.
        /// </summary>
        public OverlayWindow(GameStateService gameStateService)
        {
            // These MUST be set before InitializeComponent() is called
            this.AllowsTransparency = true;
            this.WindowStyle = WindowStyle.None;
            this.Background = System.Windows.Media.Brushes.Transparent;
            this.ResizeMode = ResizeMode.NoResize;
            
            InitializeComponent();
            
            // Store the GameStateService reference
            _gameStateService = gameStateService ?? throw new ArgumentNullException(nameof(gameStateService));
            
            // --- Initialize Managers and Services ---
            _enemyJunglerPortraitManager = new PortraitManager(ChampionPortraitControl); // Use renamed field
            
            // Populate the list of zone polygons AFTER InitializeComponent
            _allZonePolygons = new List<Polygon>
            {
                TopZonePolygon, RedTopJgZonePolygon, RedBaseZonePolygon, RedBotJgZonePolygon,
                BotZonePolygon, BlueBotJgZonePolygon, BlueBaseZonePolygon, BlueTopJgZonePolygon,
                TopRiverZonePolygon, MidZonePolygon, BotRiverZonePolygon
            };
            
            _zoneManager = new ZoneManager(
                _allZonePolygons,
                ZoneScaleTransform,
                ZoneTranslateTransform,
                _defaultZoneFill, 
                _highlightZoneFill,
                DEBUG_MODE
            );

            // Apply debug or production border
            if (DEBUG_MODE)
            {
                // Debug: Thick Red Border
                MainBorder.BorderBrush = System.Windows.Media.Brushes.Red;
                MainBorder.BorderThickness = new Thickness(3);
                Debug.WriteLine("[OverlayWindow] DEBUG_MODE enabled: Applying Red border.");
            }
            else
            {
                // Production: Semi-Transparent Green Border
                // Adjust Alpha (AA) for transparency (00=invisible, FF=opaque)
                MainBorder.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x44, 0x00, 0xFF, 0x00)); // Semi-transparent bright green
                MainBorder.BorderThickness = new Thickness(2); // Adjust thickness as desired
                Debug.WriteLine("[OverlayWindow] DEBUG_MODE disabled: Applying Green border.");
            }
            
            // Add a source initialize handler to make sure window style is set after window handle is created
            this.SourceInitialized += OverlayWindow_SourceInitialized;
            
            // Initialize scanner and capture services
            _minimapScanner = new MinimapScannerService();
            _minimapCaptureService = new MinimapCaptureService(); 

            this.Closed += OnOverlayClosed; // Use named method for cleanup
        }

        /// <summary>
        /// Applies click-through and other necessary extended window styles after the window handle is created.
        /// </summary>
        private void OverlayWindow_SourceInitialized(object sender, EventArgs e)
        {
            // Make the window click-through by modifying its extended styles
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int extendedStyle = Win32Helper.GetWindowLong(hwnd, Win32Helper.GWL_EXSTYLE);
            Win32Helper.SetWindowLong(hwnd, Win32Helper.GWL_EXSTYLE, extendedStyle | Win32Helper.WS_EX_TRANSPARENT | Win32Helper.WS_EX_LAYERED | Win32Helper.WS_EX_TOOLWINDOW);
        }
        
        /// <summary>
        /// Handles game state updates from the GameStateService.
        /// Updates UI elements based on the enemy jungler's state.
        /// </summary>
        /// <param name="state">The current game state.</param>
        private void OnGameStateUpdated(GameStateEventArgs state)
        {
            // Ensure execution on the UI thread
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => OnGameStateUpdated(state));
                return;
            }

            // Update champion info if needed
            if (ChampionPortraitControl != null && 
                (!string.Equals(ChampionPortraitControl.ChampionName, state.EnemyJunglerChampionName) ||
                 !string.Equals(ChampionPortraitControl.Team, state.EnemyJunglerTeam)))
            {
                ChampionPortraitControl.ChampionName = state.EnemyJunglerChampionName;
                ChampionPortraitControl.Team = state.EnemyJunglerTeam;
            }

            // UI Logic based on jungler state:
            if (state.IsEnemyJunglerDead)
            {
                // When jungler is dead, show portrait at base and hide zone
                _zoneManager.HideAll();
                _enemyJunglerPortraitManager.ShowAtBase(state.EnemyJunglerTeam, _overlaySize);
                Debug.WriteLine($"[OverlayWindow] Enemy jungler is dead. Showing at base with respawn timer: {state.EnemyJunglerRespawnTimer:F1}s");
            }
            else // Jungler is alive
            {
                if (state.IsEnemyJunglerVisibleOnMinimap)
                {
                    // When jungler is visible, hide portrait and zone
                    _enemyJunglerPortraitManager.Hide();
                    _zoneManager.HideAll();
                    Debug.WriteLine("[OverlayWindow] Enemy jungler is visible on minimap. Hiding portrait and zone.");
                }
                else // Jungler is alive but not visible
                {
                    if (state.EnemyJunglerLastSeenLocation.HasValue)
                    {
                        // When jungler was last seen somewhere, update zone and portrait
                        _zoneManager.UpdateHighlight(state.EnemyJunglerLastSeenLocation.Value, _captureSize);
                        _enemyJunglerPortraitManager.UpdatePosition(state.EnemyJunglerLastSeenLocation.Value, _overlaySize, _captureSize);

                        // Only start fade-out if not already fading
                        if (!_enemyJunglerPortraitManager.IsFadingOut)
                        {
                            Debug.WriteLine("[OverlayWindow] Enemy jungler not visible. Starting fade-out and showing zone.");
                            _enemyJunglerPortraitManager.StartFadeOut();
                        }
                    }
                    else // No last known location
                    {
                        Debug.WriteLine("[OverlayWindow] Enemy jungler not visible, and no last known location.");
                        _enemyJunglerPortraitManager.Hide();
                        _zoneManager.HideAll();
                    }
                }
            }
        }

        /// <summary>
        /// Shows the overlay window, calculates its size and position based on settings,
        /// updates zone transforms, registers services with GameStateService, 
        /// starts continuous updates, and makes the window visible.
        /// </summary>
        /// <param name="location">The corner of the screen where the overlay should be placed ("Top Left", "Top Right", etc.).</param>
        /// <param name="scale">The scale percentage (0-100) used to determine the overlay size.</param>
        public void ShowOverlay(string location, int scale)
        {
            // Calculate size and initial position
            _overlaySize = MIN_MINIMAP_SIZE + (scale / 100.0) * (MAX_MINIMAP_SIZE - MIN_MINIMAP_SIZE);
            _captureSize = MIN_CAPTURE_SIZE + (scale / 100.0) * (MAX_CAPTURE_SIZE - MIN_CAPTURE_SIZE); // Calculate capture size
            this.Width = this.Height = _overlaySize; // Overlay window remains the original size
            
            // --- Use Zone Manager to Update Transforms ---
            _zoneManager.UpdateTransforms(_overlaySize, _captureSize);
            // ---

            // Position overlay
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;
            
            switch (location)
            {
                case "Bottom Right":
                    this.Left = screenWidth - _overlaySize;
                    this.Top = screenHeight - _overlaySize;
                    break;
                case "Bottom Left":
                    this.Left = 0;
                    this.Top = screenHeight - _overlaySize;
                    break;
                case "Top Right":
                    this.Left = screenWidth - _overlaySize;
                    this.Top = 0;
                    break;
                case "Top Left":
                    this.Left = 0;
                    this.Top = 0;
                    break;
            }
            
            // --- Register and Initialize Game State Service ---
            try
            {
                Debug.WriteLine("[OverlayWindow] Registering minimap services with GameStateService");
                _gameStateService.RegisterMinimapServices(_minimapCaptureService, _minimapScanner);
                _gameStateService.UpdateOverlayParameters(_overlaySize, _captureSize);
                
                // Subscribe to game state updates
                _gameStateService.GameStateUpdated += OnGameStateUpdated;
                
                // Start continuous updates
                Debug.WriteLine("[OverlayWindow] Starting continuous game state updates");
                _gameStateService.StartContinuousUpdates(
                    TimeSpan.FromMilliseconds(DEFAULT_SCREENSHOT_INTERVAL),
                    _overlaySize,
                    _captureSize);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OverlayWindow] Error initializing game state service: {ex.Message}");
                System.Windows.MessageBox.Show($"Error initializing game state service: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                this.Close();
                return;
            }
            
            // Show and activate the overlay window
            this.Topmost = true; 
            this.Show();
            this.Topmost = true;
            this.Activate();

            _zoneManager.HideAll();
            _enemyJunglerPortraitManager.Reset(); 
        }

        /// <summary>
        /// Event handler for the Window.Closed event. Ensures resources are cleaned up.
        /// </summary>
        private void OnOverlayClosed(object sender, EventArgs e)
        {
            CleanupCaptureResources();
        }

        /// <summary>
        /// Cleans up resources used by the overlay, primarily stopping and disposing services.
        /// </summary>
        private void CleanupCaptureResources()
        {
            Debug.WriteLine("[OverlayWindow] Cleaning up resources...");
            
            // Stop game state updates and unsubscribe from events
            if (_gameStateService != null)
            {
                _gameStateService.StopContinuousUpdates();
                _gameStateService.GameStateUpdated -= OnGameStateUpdated;
            }
            
            // Dispose of the minimap capture service
            _minimapCaptureService?.Dispose();
            _minimapCaptureService = null;
            
            // Dispose of the minimap scanner service
            _minimapScanner?.Dispose();
            _minimapScanner = null;
            
            Debug.WriteLine("[OverlayWindow] Resource cleanup complete.");
        }

        /// <summary>
        /// Handles the scenario when the League of Legends game process is closed.
        /// Stops animations, hides UI elements, and resets state.
        /// </summary>
        public void HandleGameClosed()
        {
            // Ensure this runs on the UI thread if called from elsewhere
             if (!Dispatcher.CheckAccess())
             {
                 Dispatcher.Invoke(HandleGameClosed);
                 return;
             }

            Debug.WriteLine("[OverlayWindow] Handling game closed event.");

            // Stop continuous updates
            _gameStateService.StopContinuousUpdates();

            // Reset UI state
            _enemyJunglerPortraitManager?.Reset(); 

            // Clear champion info
            if (ChampionPortraitControl != null)
            {
                ChampionPortraitControl.ChampionName = string.Empty;
                ChampionPortraitControl.Team = string.Empty;
            }

            // Reset state
            _minimapScanner?.ClearCurrentTemplate();
            _zoneManager.HideAll();
        }

        /// <summary>
        /// Sets the information for the enemy jungler to be tracked.
        /// </summary>
        /// <param name="championName">The name of the enemy jungler champion.</param>
        /// <param name="team">The team identifier for the enemy jungler (e.g., "Blue" or "Red").</param>
        public void SetEnemyJunglerInfo(string championName, string team)
        {
             if (ChampionPortraitControl == null) return; // Simplified null check
             Debug.WriteLine($"[OverlayWindow] Setting enemy jungler info: {championName} ({team})");

            ChampionPortraitControl.ChampionName = championName;
            ChampionPortraitControl.Team = team;

            _enemyJunglerPortraitManager.Reset(); 
            _zoneManager.HideAll(); 
        }
    }
}