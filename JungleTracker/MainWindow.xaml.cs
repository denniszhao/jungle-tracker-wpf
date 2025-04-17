using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.ComponentModel;
using WPFMessageBox = System.Windows.MessageBox;
using System.Diagnostics;
using System.Threading.Tasks;
using Timer = System.Timers.Timer;
using JungleTracker.Properties; // Optional, can use full name below

namespace JungleTracker
{
    public partial class MainWindow : Window
    {
        private OverlayWindow? _overlayWindow = null;
        private LeagueClientService _leagueClientService;
        private GameStateService _gameStateService;
        private Timer? _leagueMonitorTimer = null;
        private bool _wasLeagueRunning = false;

        public MainWindow()
        {
            InitializeComponent();
            this.Loaded += MainWindow_Loaded;
            this.Closing += MainWindow_Closing;
            
            // Initialize the League Client Service
            _leagueClientService = new LeagueClientService();
            // Initialize the Game State Service
            _gameStateService = new GameStateService(_leagueClientService);

            // Set initial status text (redundant if set in XAML, but safe)
            // UpdateOverlayStatus(false); // Or let XAML handle initial state
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Check if user has ever interacted with the consent dialog
            if (!Settings.Default.HasInteractedWithConsent)
            {
                Debug.WriteLine("First interaction: Showing ConsentWindow.");
                var consentWindow = new ConsentWindow();
                bool? result = consentWindow.ShowDialog();

                bool consentWasGiven = (result == true);
                
                // Store the actual decision
                Settings.Default.HasGivenDataConsent = consentWasGiven;
                // Mark that the interaction has happened
                Settings.Default.HasInteractedWithConsent = true;
                // Save both settings
                Settings.Default.Save();

                // Optional: Show a message if denied, but don't prevent startup
                if (!consentWasGiven)
                {
                     Debug.WriteLine("Consent denied by user during first interaction.");
                     // You could potentially still show this, but it doesn't block anything
                     // WPFMessageBox.Show("Data collection consent denied. You can change this in settings.", 
                     //                   "Consent Information", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    Debug.WriteLine("Consent granted by user during first interaction.");
                }
            }
            else
            {
                 Debug.WriteLine("User has previously interacted with consent. Consent state: " + Settings.Default.HasGivenDataConsent);
            }

            // ---- Application Startup Logic ----
            // Start monitoring regardless of the HasGivenDataConsent setting value
            Debug.WriteLine("Proceeding with application startup and monitoring.");
            StartMonitoringForLeague();
            
            // The CheckBox UI is automatically updated via the TwoWay binding
        }

        // Monitor for League of Legends being launched
        private void StartMonitoringForLeague(int checkIntervalMs = 2000)
        {
            Debug.WriteLine("Starting League monitoring process...");
            // Stop any existing timer
            _leagueMonitorTimer?.Stop();
            _leagueMonitorTimer?.Dispose();
            
            // Initialize with current state
            _wasLeagueRunning = IsLeagueGameRunning();
            
            // Create and start new timer
            _leagueMonitorTimer = new Timer(checkIntervalMs);
            _leagueMonitorTimer.Elapsed += (s, e) => 
            {
                // Check on UI thread
                this.Dispatcher.Invoke(async () => 
                {
                    bool isRunningNow = IsLeagueGameRunning();
                    
                    if (isRunningNow && !_wasLeagueRunning)
                    {
                        await OnLeagueLaunchedAsync();
                    }
                    else if (!isRunningNow && _wasLeagueRunning)
                    {
                        OnLeagueClosed();
                    }
                    
                    _wasLeagueRunning = isRunningNow;
                });
            };
            _leagueMonitorTimer.AutoReset = true;
            _leagueMonitorTimer.Start();
            
            Debug.WriteLine("Started monitoring for League of Legends");
        }
        
        // Handle League of Legends launch
        private async Task OnLeagueLaunchedAsync()
        {
            Debug.WriteLine("League of Legends has launched! Attempting data retrieval...");
            
            // Wait a bit for the game to initialize
            await Task.Delay(5000);
            
            // Try to get game data through GameStateService
            bool gameDataFound = await _gameStateService.InitializeEnemyJunglerDataAsync();
            
            if (gameDataFound)
            {
                Debug.WriteLine($"Enemy jungler detected: {_gameStateService.EnemyJunglerChampionName}");
                
                // If overlay is already open, update it
                if (_overlayWindow != null)
                {
                    _overlayWindow.SetEnemyJunglerInfo(
                        _gameStateService.EnemyJunglerChampionName, 
                        _gameStateService.EnemyJunglerTeam);
                }
            }
            else
            {
                Debug.WriteLine("Could not retrieve game data after multiple attempts");
            }
        }
        
        // Handle League of Legends close
        private void OnLeagueClosed()
        {
            Debug.WriteLine("League of Legends game process has closed");

            // Tell the overlay window to clear its state
            // Use Dispatcher to ensure UI access is safe, though HandleGameClosed checks itself too
            this.Dispatcher.Invoke(() =>
            {
                _overlayWindow?.HandleGameClosed();
            });

            // Note: We are NOT closing the overlay window itself here, just clearing its game state.
            // The user can still close it via the button or by closing the main window.
        }

        private void MinimapScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ScaleValueTextBlock != null)
            {
                ScaleValueTextBlock.Text = ((int)e.NewValue).ToString();
            }
        }

        private async void ToggleOverlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_overlayWindow == null)
            {
                // Check if game/client is running (this is still relevant)
                 if (!IsLeagueGameRunning() && !IsRiotClientRunning())
                 {
                     WPFMessageBox.Show("League of Legends is not running. Please start the game or client first.", 
                         "Game Not Running", MessageBoxButton.OK, MessageBoxImage.Information);
                     return;
                 }

                // Create overlay window
                _overlayWindow = new OverlayWindow(_gameStateService);
                _overlayWindow.Closed += OverlayWindow_Closed;

                // Try to get enemy jungler info if game is running
                if (IsLeagueGameRunning())
                {
                    Debug.WriteLine("Getting enemy jungler info after overlay click");
                    bool gameDataFound = await _gameStateService.InitializeEnemyJunglerDataAsync(); 
                    if (gameDataFound)
                    {
                        Debug.WriteLine($"Enemy jungler detected, passing to Overlay: {_gameStateService.EnemyJunglerChampionName}");
                        _overlayWindow.SetEnemyJunglerInfo(
                            _gameStateService.EnemyJunglerChampionName,
                            _gameStateService.EnemyJunglerTeam);
                    }
                }

                // Configure and show overlay
                string location = ((ComboBoxItem)MinimapLocationComboBox.SelectedItem).Content.ToString() ?? "Bottom Right";
                int scale = (int)MinimapScaleSlider.Value;
                _overlayWindow.ShowOverlay(location, scale);
                
                ToggleOverlayButton.Content = "Close Overlay";
                UpdateOverlayStatus(true); // Update status to Open
            }
            else
            {
                _overlayWindow.Close(); 
            }
        }

        // Check if the League game is running
        private bool IsLeagueGameRunning()
        {
            return Process.GetProcessesByName("League of Legends").Length > 0;
        }

        // Check if the Riot client is running
        private bool IsRiotClientRunning()
        {
            return Process.GetProcessesByName("RiotClientServices").Length > 0 || 
                   Process.GetProcessesByName("LeagueClient").Length > 0;
        }

        private void OverlayWindow_Closed(object? sender, EventArgs e)
        {
            _overlayWindow = null;
            ToggleOverlayButton.Content = "Open Overlay";
            UpdateOverlayStatus(false); // Update status to Closed
        }

        // Helper method to update the status TextBlock
        private void UpdateOverlayStatus(bool isOpen)
        {
            if (isOpen)
            {
                OverlayStatusTextBlock.Text = "Overlay Open";
                OverlayStatusTextBlock.Foreground = System.Windows.Media.Brushes.Green;
            }
            else
            {
                OverlayStatusTextBlock.Text = "Overlay Closed";
                OverlayStatusTextBlock.Foreground = System.Windows.Media.Brushes.Red;
            }
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            _overlayWindow?.Close();
            _leagueMonitorTimer?.Stop();
            _leagueMonitorTimer?.Dispose();
            _gameStateService?.Dispose();
        }
    }
} 