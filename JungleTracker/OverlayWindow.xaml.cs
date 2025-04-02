using System;
using System.IO;
using System.Windows;
// Remove System.Windows.Shapes which is causing Rectangle conflict
using Timer = System.Timers.Timer;
using System.Timers;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Threading.Tasks; // Needed for Task.Run
using System.Windows.Media.Imaging;
using System.Windows.Media; // Add for Colors
using System.Windows.Media.Animation; // Add for animations

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

        // Win32 API imports
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        
        // --- Core constants ---
        private const double MIN_MINIMAP_SIZE = 280.0;
        private const double MAX_MINIMAP_SIZE = 560.0;
        private const int SCREENSHOT_INTERVAL = 10000;
        
        // --- New animation constants ---
        private const int FADE_DURATION_MS = 3000;
        
        // --- Instance variables ---
        private Timer _screenshotTimer;
        private string _screenshotFolder;
        private double _overlaySize;
        private uint _leagueProcessId; // Still useful for focus check

        // Add a field to store the window handle we're capturing
        private IntPtr _captureHwnd = IntPtr.Zero;

        // Add window rect structure and method for getting window bounds
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        // Additional Win32 API imports for BitBlt
        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        // PrintWindow flags
        private const uint PW_RENDERFULLCONTENT = 0x00000002; // Added in Windows 10

        // New Win32 API imports for SetWindowLong and GetWindowLong
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        // Window style constants
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TOOLWINDOW = 0x00000080; // Additional style to prevent showing in alt+tab

        // Add this field to store enemy jungler information
        private string _enemyJunglerChampionName;
        private string _enemyTeam;
        private System.Windows.Controls.Image _championPortrait;

        // Add fields for animation
        private Storyboard _fadeOutStoryboard;
        private System.Drawing.Point? _lastKnownLocation;

        // Add field for the minimap scanner service
        private MinimapScannerService _minimapScanner;

        // Add latest screenshot field
        private Bitmap _latestMinimapScreenshot;
        
        public OverlayWindow()
        {
            // These MUST be set before InitializeComponent() is called
            this.AllowsTransparency = true;
            this.WindowStyle = WindowStyle.None;
            this.Background = System.Windows.Media.Brushes.Transparent;
            this.ResizeMode = ResizeMode.NoResize;
            
            InitializeComponent();
            
            // Apply debug border if in debug mode
            if (DEBUG_MODE)
            {
                MainBorder.BorderBrush = System.Windows.Media.Brushes.Red;
                MainBorder.BorderThickness = new Thickness(3);
            }
            
            // Add a source initialize handler to make sure window style is set after window handle is created
            this.SourceInitialized += OverlayWindow_SourceInitialized;
            
            SetupScreenshotFolder();

            // Create the minimap scanner service
            _minimapScanner = new MinimapScannerService();

            // Timer setup - will capture using PrintWindow
            _screenshotTimer = new Timer(SCREENSHOT_INTERVAL);
            _screenshotTimer.Elapsed += OnScreenshotTimerElapsed; 
            _screenshotTimer.AutoReset = true;

            this.Closed += OnOverlayClosed; // Use named method for cleanup
        }

        private void OverlayWindow_SourceInitialized(object sender, EventArgs e)
        {
            // Make the window click-through by modifying its extended styles
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW);
        }
        
        // Setup folder for saving screenshots
        private void SetupScreenshotFolder()
        {
            string baseFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            _screenshotFolder = System.IO.Path.Combine(baseFolder, "JungleTracker", "Screenshots", 
                                            DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            Directory.CreateDirectory(_screenshotFolder);
        }

        // --- Process Management ---
        private bool IsLeagueOfLegendsFocused()
        {
            IntPtr focusedWindow = GetForegroundWindow();
            if (focusedWindow == IntPtr.Zero) return false;

            GetWindowThreadProcessId(focusedWindow, out uint focusedProcessId);

            if (_leagueProcessId != 0 && focusedProcessId == _leagueProcessId) return true;

            Process[] processes = Process.GetProcessesByName("League of Legends");
            if (processes.Length == 0)
            {
                 _leagueProcessId = 0; // Reset if game not found
                 return false;
            }

            _leagueProcessId = (uint)processes[0].Id;
            return focusedProcessId == _leagueProcessId;
        }

        // Handle screenshot timer elapsed - NEW METHOD
        private void OnScreenshotTimerElapsed(object sender, ElapsedEventArgs e)
        {
            Dispatcher.Invoke(() => {
                // First capture the screenshot using the existing method
                AttemptCapture();
                
                // Then process the screenshot if we have a valid scanner and champion info
                if (_minimapScanner != null && !string.IsNullOrEmpty(_enemyJunglerChampionName) && _latestMinimapScreenshot != null)
                {
                    ProcessMinimapScreenshot();
                }
            });
        }

        // New method to process the minimap screenshot
        private void ProcessMinimapScreenshot()
        {
            try
            {
                // Make sure we have the right champion template loaded
                if (!_minimapScanner.SetChampionTemplate(_enemyJunglerChampionName, _enemyTeam))
                {
                    Debug.WriteLine($"Failed to set champion template for {_enemyJunglerChampionName}");
                    return;
                }

                // Scan for the champion
                System.Drawing.Point? matchLocation = _minimapScanner.ScanForChampion(_latestMinimapScreenshot);

                if (matchLocation.HasValue)
                {
                    // Champion found - update position and make invisible
                    UpdateChampionPortraitPosition(matchLocation.Value);
                    _lastKnownLocation = matchLocation;
                    
                    // Make champion portrait invisible since jungler is visible on minimap
                    if (_championPortrait != null)
                    {
                        // Stop any fade animation in progress
                        StopFadeOutAnimation();
                        _championPortrait.Opacity = 0.0;
                    }
                    
                    Debug.WriteLine($"Champion found at {matchLocation.Value.X}, {matchLocation.Value.Y}");
                }
                else
                {
                    // Champion not found - start or continue fade out from last known location
                    if (_lastKnownLocation.HasValue && _championPortrait != null)
                    {
                        // Only start fading if the portrait is fully visible
                        if (_championPortrait.Opacity == 1.0)
                        {
                            StartFadeOutAnimation();
                        }
                        // Otherwise let any current fade continue
                    }
                    
                    Debug.WriteLine("Champion not found in minimap");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error processing minimap screenshot: {ex.Message}");
            }
        }

        // Update the position of the champion portrait based on the match location
        private void UpdateChampionPortraitPosition(System.Drawing.Point matchLocation)
        {
            if (_championPortrait == null)
                return;
                
            // Convert match location to overlay coordinates
            // The match location is relative to the minimap screenshot
            double x = matchLocation.X - (_championPortrait.Width / 2);
            double y = matchLocation.Y - (_championPortrait.Height / 2);
            
            // Ensure we stay within the overlay bounds
            x = Math.Max(0, Math.Min(x, _overlaySize - _championPortrait.Width));
            y = Math.Max(0, Math.Min(y, _overlaySize - _championPortrait.Height));
            
            // Update the position
            _championPortrait.Margin = new Thickness(x, y, 0, 0);
        }

        // Animation methods
        private void StartFadeOutAnimation()
        {
            if (_championPortrait == null)
                return;
                
            // Make sure the portrait is visible before starting fade
            _championPortrait.Opacity = 1.0;
            _championPortrait.Visibility = Visibility.Visible;
            
            // Create a new storyboard for the fade out animation
            _fadeOutStoryboard = new Storyboard();
            
            // Create the opacity animation
            DoubleAnimation fadeOutAnimation = new DoubleAnimation
            {
                From = 1.0,
                To = 0.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(FADE_DURATION_MS)),
                AutoReverse = false
            };
            
            // Set the target property
            Storyboard.SetTarget(fadeOutAnimation, _championPortrait);
            Storyboard.SetTargetProperty(fadeOutAnimation, new PropertyPath(System.Windows.Controls.Image.OpacityProperty));
            
            // Add the animation to the storyboard
            _fadeOutStoryboard.Children.Add(fadeOutAnimation);
            
            // Start the animation
            _fadeOutStoryboard.Begin();
            
            Debug.WriteLine("Started fade out animation");
        }

        private void StopFadeOutAnimation()
        {
            if (_fadeOutStoryboard != null)
            {
                _fadeOutStoryboard.Stop();
                _fadeOutStoryboard = null;
                Debug.WriteLine("Stopped fade out animation");
            }
        }

        // BitBlt-based capture
        private void AttemptCapture()
        {
            if (_captureHwnd == IntPtr.Zero || !IsLeagueOfLegendsFocused())
            {
                // Don't attempt capture if we don't have a target window or if game is not focused
                return;
            }

            string savedFilePath = null;
            try
            {
                // Get the window size
                GetWindowRect(_captureHwnd, out RECT rect);
                int width = rect.right - rect.left;
                int height = rect.bottom - rect.top;
                
                if (width <= 0 || height <= 0)
                {
                    Debug.WriteLine("Invalid window dimensions");
                    return;
                }

                // Create a bitmap to hold the captured image
                using (Bitmap bitmap = new Bitmap(width, height))
                {
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                        // Get device context for the bitmap
                        IntPtr hdc = graphics.GetHdc();
                        bool success = false;
                        
                        try
                        {
                            // Use PrintWindow with RENDERFULLCONTENT flag instead of BitBlt
                            success = PrintWindow(_captureHwnd, hdc, PW_RENDERFULLCONTENT);
                        }
                        finally
                        {
                            graphics.ReleaseHdc(hdc);
                        }
                        
                        if (!success)
                        {
                            Debug.WriteLine("PrintWindow failed");
                            return;
                        }
                    }

                    // Calculate minimap coordinates WITHIN the captured window
                    int minimapX = width - (int)_overlaySize;
                    int minimapY = height - (int)_overlaySize;
                    
                    // Ensure coordinates are valid for cropping
                    if (minimapX < 0 || minimapY < 0 || 
                        minimapX + (int)_overlaySize > width || 
                        minimapY + (int)_overlaySize > height ||
                        (int)_overlaySize <= 0)
                    {
                        Debug.WriteLine("Calculated minimap coordinates are out of bounds");
                        return; // Don't proceed if crop is invalid
                    }
                    
                    // Crop the full bitmap to just the minimap area
                    using (Bitmap minimapImage = bitmap.Clone(
                        new System.Drawing.Rectangle(minimapX, minimapY, (int)_overlaySize, (int)_overlaySize), 
                        bitmap.PixelFormat))
                    {
                        // Save screenshot with timestamp
                        savedFilePath = System.IO.Path.Combine(_screenshotFolder,
                                     $"minimap_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                        minimapImage.Save(savedFilePath, ImageFormat.Png);
                        
                        // Store the latest minimap screenshot for processing
                        _latestMinimapScreenshot?.Dispose();
                        _latestMinimapScreenshot = new Bitmap(minimapImage);
                    }
                }
            }
            catch (Exception ex)
            {
                savedFilePath = null;
                Debug.WriteLine($"Error during capture/processing: {ex.Message}");
            }
        }

        // --- UI and Lifecycle ---
        public void ShowOverlay(string location, int scale)
        {
            // Calculate size and initial position
            _overlaySize = MIN_MINIMAP_SIZE + (scale / 100.0) * (MAX_MINIMAP_SIZE - MIN_MINIMAP_SIZE);
            this.Width = this.Height = _overlaySize;
            
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
            
            // Only set up capture if we don't already have a window handle
            if (_captureHwnd == IntPtr.Zero)
            {
                try
                {
                    // Find the League of Legends window
                    Process[] processes = Process.GetProcessesByName("League of Legends");
                    
                    if (processes.Length == 0)
                    {
                        // For testing purposes, allow any window with a title
                        Debug.WriteLine("League of Legends not found. Will try to find any visible window for testing...");
                        Process[] allProcesses = Process.GetProcesses();
                        foreach (var proc in allProcesses)
                        {
                            if (!string.IsNullOrEmpty(proc.MainWindowTitle) && proc.MainWindowHandle != IntPtr.Zero)
                            {
                                _captureHwnd = proc.MainWindowHandle;
                                _leagueProcessId = (uint)proc.Id;
                                Debug.WriteLine($"Using '{proc.MainWindowTitle}' window for testing capture.");
                                break;
                            }
                        }
                    }
                    else
                    {
                        // League of Legends found
                        _captureHwnd = processes[0].MainWindowHandle;
                        _leagueProcessId = (uint)processes[0].Id;
                        Debug.WriteLine("League of Legends window found. Starting capture...");
                    }
                    
                    if (_captureHwnd == IntPtr.Zero)
                    {
                        System.Windows.MessageBox.Show("Could not find a suitable window to capture.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        this.Close();
                        return;
                    }
                    
                    // Get window size to verify it's valid
                    GetWindowRect(_captureHwnd, out RECT rect);
                    int width = rect.right - rect.left;
                    int height = rect.bottom - rect.top;
                    
                    if (width <= 0 || height <= 0)
                    {
                        System.Windows.MessageBox.Show("Invalid window dimensions.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        this.Close();
                        return;
                    }
                    
                    // Start the screenshot timer
                    Console.WriteLine($"Starting capture for window {_captureHwnd} with dimensions {width}x{height}");
                    _screenshotTimer.Start();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error setting up capture: {ex.Message}");
                    System.Windows.MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    this.Close();
                    return;
                }
            }
            
            // Show and activate the overlay window
            this.Topmost = true; 
            this.Show();
            this.Topmost = true;
            this.Activate();
        }

        private void OnOverlayClosed(object sender, EventArgs e)
        {
            CleanupCaptureResources();
        }

        private void CleanupCaptureResources()
        {
             Console.WriteLine("Cleaning up capture resources...");
            _screenshotTimer?.Stop();
            _screenshotTimer?.Dispose();
            
            // Dispose of the minimap scanner
            _minimapScanner?.Dispose();
            _minimapScanner = null;
            
            // Dispose of the latest screenshot
            _latestMinimapScreenshot?.Dispose();
            _latestMinimapScreenshot = null;
            
            // Clear capture handle
            _captureHwnd = IntPtr.Zero;
        }

        // Screenshot interval control - still useful
        internal void SetScreenshotInterval(int milliseconds)
        {
            if (_screenshotTimer != null && milliseconds > 0)
                _screenshotTimer.Interval = milliseconds;
        }

        // Add this method to set enemy jungler information
        public void SetEnemyJunglerInfo(string championName, string team = "")
        {
            _enemyJunglerChampionName = championName;
            _enemyTeam = team;
            Debug.WriteLine($"Overlay now tracking enemy jungler: {_enemyJunglerChampionName} on {_enemyTeam} team");
            
            // Display the champion portrait on the overlay
            DisplayChampionPortrait();
            
            // Reset any last known location
            _lastKnownLocation = null;
        }

        // Method to display the champion portrait based on team
        private void DisplayChampionPortrait()
        {
            if (string.IsNullOrEmpty(_enemyJunglerChampionName))
                return;
            
            try
            {
                string championFileName = _enemyJunglerChampionName;
                
                // Special case for Wukong/MonkeyKing
                if (_enemyJunglerChampionName == "Wukong" || _enemyJunglerChampionName == "MonkeyKing")
                {
                    championFileName = "MonkeyKing";
                }
                
                // Determine the resource name based on team
                string resourceFolder = _enemyTeam == "CHAOS" ? "champions_altered_red" : "champions_altered_blue";
                string resourceName = $"JungleTracker.Assets.Champions.{resourceFolder}.{championFileName}.png";
                
                // Load the image from embedded resources
                System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();
                System.IO.Stream resourceStream = assembly.GetManifestResourceStream(resourceName);
                
                if (resourceStream == null)
                {
                    // Try to list available resources for debugging
                    string[] resources = assembly.GetManifestResourceNames();
                    Debug.WriteLine("Available resources:");
                    foreach (string resource in resources)
                    {
                        Debug.WriteLine($"  {resource}");
                    }
                    
                    Debug.WriteLine($"Champion portrait not found as embedded resource: {resourceName}");
                    return;
                }
                
                // Create the image control if it doesn't exist
                if (_championPortrait == null)
                {
                    _championPortrait = new System.Windows.Controls.Image
                    {
                        Width = 46,
                        Height = 46,
                        Margin = new Thickness(5),
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                        VerticalAlignment = System.Windows.VerticalAlignment.Top
                    };
                    
                    // Add drop shadow effect
                    System.Windows.Media.Effects.DropShadowEffect effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        ShadowDepth = 3,
                        Direction = 315,
                        Opacity = 0.6,
                        BlurRadius = 5,
                        Color = Colors.Black
                    };
                    _championPortrait.Effect = effect;
                    
                    // Add to the overlay
                    MainBorder.Child = _championPortrait;
                }
                
                // Load the image from stream
                BitmapImage bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.StreamSource = resourceStream;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                
                _championPortrait.Source = bitmap;
                _championPortrait.Visibility = Visibility.Visible;
                
                // Start with the champion portrait fully visible
                _championPortrait.Opacity = 1.0;
                
                Debug.WriteLine($"Champion portrait loaded from embedded resource: {resourceName}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading champion portrait: {ex.Message}");
            }
        }
    }
}