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

            // Timer setup - will capture using PrintWindow
            _screenshotTimer = new Timer(SCREENSHOT_INTERVAL);
            _screenshotTimer.Elapsed += (s, e) => Dispatcher.Invoke(AttemptCapture); 
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
                    Console.WriteLine("Invalid window dimensions");
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
                            Console.WriteLine("PrintWindow failed");
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
                        Console.WriteLine("Calculated minimap coordinates are out of bounds");
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
                    }
                }
            }
            catch (Exception ex)
            {
                savedFilePath = null;
                Console.WriteLine($"Error during capture/processing: {ex.Message}");
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
                        Console.WriteLine("League of Legends not found. Will try to find any visible window for testing...");
                        Process[] allProcesses = Process.GetProcesses();
                        foreach (var proc in allProcesses)
                        {
                            if (!string.IsNullOrEmpty(proc.MainWindowTitle) && proc.MainWindowHandle != IntPtr.Zero)
                            {
                                _captureHwnd = proc.MainWindowHandle;
                                _leagueProcessId = (uint)proc.Id;
                                Console.WriteLine($"Using '{proc.MainWindowTitle}' window for testing capture.");
                                break;
                            }
                        }
                    }
                    else
                    {
                        // League of Legends found
                        _captureHwnd = processes[0].MainWindowHandle;
                        _leagueProcessId = (uint)processes[0].Id;
                        Console.WriteLine("League of Legends window found. Starting capture...");
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
                string resourceFolder = _enemyTeam == "CHAOS" ? "champions-altered-red" : "champions-altered-blue";
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
                
                Debug.WriteLine($"Champion portrait loaded from embedded resource: {resourceName}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading champion portrait: {ex.Message}");
            }
        }
    }
}