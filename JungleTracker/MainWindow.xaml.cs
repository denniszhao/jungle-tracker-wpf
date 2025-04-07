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

namespace JungleTracker
{
    public partial class MainWindow : Window
    {
        private OverlayWindow? _overlayWindow = null;
        private LeagueClientService _leagueClientService;
        private Timer? _leagueMonitorTimer = null;
        private bool _wasLeagueRunning = false;

        public MainWindow()
        {
            InitializeComponent();
            this.Loaded += MainWindow_Loaded;
            this.Closing += MainWindow_Closing;
            
            // Initialize the League Client Service
            _leagueClientService = new LeagueClientService();
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Ask for data consent on first load.
            var consentWindow = new ConsentWindow();
            bool? result = consentWindow.ShowDialog();
            if (result != true)
            {
                WPFMessageBox.Show("Consent not given. The application will now close.", "Consent Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                this.Close();
            }
            
            // Start monitoring for League of Legends
            StartMonitoringForLeague();
        }

        // Monitor for League of Legends being launched
        private void StartMonitoringForLeague(int checkIntervalMs = 2000)
        {
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
                    
                    // If app just started running
                    if (isRunningNow && !_wasLeagueRunning)
                    {
                        await OnLeagueLaunchedAsync();
                    }
                    // If app just closed
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
            Debug.WriteLine("League of Legends has launched!");
            
            // Wait a bit for the game to initialize
            await Task.Delay(5000);
            
            // Try to get game data (will retry several times)
            bool gameDataFound = await _leagueClientService.TryGetGameDataAsync();
            
            if (gameDataFound)
            {
                Debug.WriteLine($"Enemy jungler detected: {_leagueClientService.EnemyJunglerChampionName}");
                
                // If overlay is already open, update it
                if (_overlayWindow != null)
                {
                    _overlayWindow.SetEnemyJunglerInfo(
                        _leagueClientService.EnemyJunglerChampionName, 
                        _leagueClientService.ActivePlayerTeam == "ORDER" ? "CHAOS" : "ORDER");
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
                // Only allow overlay if the game is running
                if (!IsLeagueGameRunning() && !IsRiotClientRunning())
                {
                    WPFMessageBox.Show("League of Legends is not running. Please start the game or client first.", 
                        "Game Not Running", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Create overlay window
                _overlayWindow = new OverlayWindow();
                _overlayWindow.Closed += OverlayWindow_Closed;

                // Try to get enemy jungler info if game is running
                if (IsLeagueGameRunning())
                {
                    Debug.WriteLine("Getting enemy jungler info after overlay click");
                    bool gameDataFound = await _leagueClientService.TryGetGameDataAsync();
                    if (gameDataFound)
                    {
                        Debug.WriteLine($"Enemy jungler detected, passing to Overlay: {_leagueClientService.EnemyJunglerChampionName}");
                        // Pass both champion name and team
                        _overlayWindow.SetEnemyJunglerInfo(
                            _leagueClientService.EnemyJunglerChampionName,
                            _leagueClientService.ActivePlayerTeam == "ORDER" ? "CHAOS" : "ORDER");
                    }
                }

                // Configure and show overlay
                string location = ((ComboBoxItem)MinimapLocationComboBox.SelectedItem).Content.ToString() ?? "Bottom Right";
                int scale = (int)MinimapScaleSlider.Value;
                _overlayWindow.ShowOverlay(location, scale);
                
                ToggleOverlayButton.Content = "Close Overlay";
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
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            _overlayWindow?.Close();
            _leagueMonitorTimer?.Stop();
            _leagueMonitorTimer?.Dispose();
        }
    }
} 