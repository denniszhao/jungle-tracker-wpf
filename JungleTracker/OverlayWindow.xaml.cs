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
        private const int SCREENSHOT_INTERVAL = 1000;
        
        // --- New animation constants ---
        private const int FADE_DURATION_MS = 10000;
        private const double FINAL_FADE_OPACITY = 0.3; // Or 0.2 for semi-transparent
        
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

        // Add fields for animation
        private Storyboard? _fadeOutStoryboard;
        private bool _isPortraitFadingOut = false;
        private System.Drawing.Point? _lastKnownLocation;

        // Add field for the minimap scanner service
        private MinimapScannerService _minimapScanner;

        // Add latest screenshot field
        private Bitmap? _latestMinimapScreenshot;
        
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
        private void OnScreenshotTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            Dispatcher.Invoke(() => {
                AttemptCapture();

                // Access control via x:Name from XAML
                if (_minimapScanner != null &&
                    ChampionPortraitControl != null && // Add null check
                    !string.IsNullOrEmpty(ChampionPortraitControl.ChampionName) &&
                    _latestMinimapScreenshot != null)
                {
                    ProcessMinimapScreenshot();
                }
            });
        }

        // New method to process the minimap screenshot
        private void ProcessMinimapScreenshot()
        {
            if (ChampionPortraitControl == null) return;

            try
            {
                // Ensure the template is set based on the control's properties
                if (!_minimapScanner.SetChampionTemplate(ChampionPortraitControl.ChampionName, ChampionPortraitControl.Team))
                {
                    Debug.WriteLine($"[OverlayWindow] Failed to set champion template for {ChampionPortraitControl.ChampionName}");
                    return;
                }

                System.Drawing.Point? matchLocation = _minimapScanner.ScanForChampion(_latestMinimapScreenshot);

                if (matchLocation.HasValue)
                {
                    // --- Champion found ---
                    _lastKnownLocation = matchLocation;

                    if (_isPortraitFadingOut)
                    {
                        StopFadeOutAnimation(); // Stop fade-out (StopWarningAnimation call inside is removed below)
                    }

                    ChampionPortraitControl.Visibility = Visibility.Collapsed;
                    ChampionPortraitControl.Opacity = 0.0;

                    Debug.WriteLine($"[OverlayWindow] Champion found at {matchLocation.Value.X}, {matchLocation.Value.Y}. Hiding portrait.");
                }
                else // --- Champion NOT found ---
                {
                    if (_lastKnownLocation.HasValue)
                    {
                        UpdateChampionPortraitPosition(_lastKnownLocation.Value);

                        if (!_isPortraitFadingOut)
                        {
                             Debug.WriteLine("[OverlayWindow] Champion not found. Starting fade-out."); // Message updated
                             ChampionPortraitControl.Visibility = Visibility.Visible;
                             ChampionPortraitControl.Opacity = 1.0;
                             StartFadeOutAnimation();
                        }
                        else
                        {
                             Debug.WriteLine("[OverlayWindow] Champion not found, portrait already fading or faded.");
                        }
                    }
                    else // --- No last known location ---
                    {
                        Debug.WriteLine("[OverlayWindow] Champion not found, and no last known location to show.");
                        if (_isPortraitFadingOut)
                        {
                             StopFadeOutAnimation();
                        }
                        ChampionPortraitControl.Visibility = Visibility.Collapsed;
                        ChampionPortraitControl.Opacity = 0.0;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OverlayWindow] Error processing minimap screenshot: {ex.Message}");
                 if (ChampionPortraitControl != null)
                 {
                     if (_isPortraitFadingOut)
                     {
                         StopFadeOutAnimation();
                     }
                     ChampionPortraitControl.Visibility = Visibility.Collapsed;
                     ChampionPortraitControl.Opacity = 0.0;
                 }
            }
        }

        // Update the position of the champion portrait based on the match location
        private void UpdateChampionPortraitPosition(System.Drawing.Point matchLocation)
        {
            if (ChampionPortraitControl == null) return; // Safety check

            // --- Use the UserControl's actual dimensions for centering ---
            double controlWidth = ChampionPortraitControl.ActualWidth > 0 ? ChampionPortraitControl.ActualWidth : ChampionPortraitControl.Width;
            double controlHeight = ChampionPortraitControl.ActualHeight > 0 ? ChampionPortraitControl.ActualHeight : ChampionPortraitControl.Height;

            // Fallback if layout hasn't completed, use RenderSize or specified Width/Height
             if (controlWidth <= 0 || controlHeight <= 0)
             {
                 if (ChampionPortraitControl.RenderSize.Width > 0 && ChampionPortraitControl.RenderSize.Height > 0) {
                    controlWidth = ChampionPortraitControl.RenderSize.Width;
                    controlHeight = ChampionPortraitControl.RenderSize.Height;
                 } else {
                    Debug.WriteLine($"[OverlayWindow] Warning: Cannot determine valid dimensions for ChampionPortraitControl in UpdatePosition. Using default size ({ChampionPortraitControl.Width}x{ChampionPortraitControl.Height}).");
                    controlWidth = ChampionPortraitControl.Width; // Use defined Width/Height as last resort
                    controlHeight = ChampionPortraitControl.Height;
                     if (controlWidth <= 0 || controlHeight <= 0) {
                          Debug.WriteLine($"[OverlayWindow] Error: ChampionPortraitControl Width/Height are invalid ({controlWidth}x{controlHeight}). Cannot update position.");
                          return; // Cannot position if size is invalid
                     }
                 }
             }
            // --- End of dimension calculation ---

            double x = matchLocation.X - (controlWidth / 2);
            double y = matchLocation.Y - (controlHeight / 2);

            // Ensure staying within overlay bounds (using control's dimensions)
            x = Math.Max(0, Math.Min(x, _overlaySize - controlWidth));
            y = Math.Max(0, Math.Min(y, _overlaySize - controlHeight));

            // Update the Margin of the ChampionPortraitControl itself
            ChampionPortraitControl.Margin = new Thickness(x, y, 0, 0);
        }

        // Animation methods
        private void StartFadeOutAnimation()
        {
            // Removed _isPortraitFadingOut check here, ProcessMinimapScreenshot ensures it's only called when needed
            if (ChampionPortraitControl == null) return;

            _isPortraitFadingOut = true; // Set the flag *before* starting
            _fadeOutStoryboard = new Storyboard();
            DoubleAnimation fadeOutAnimation = new DoubleAnimation
            {
                From = 1.0,
                To = FINAL_FADE_OPACITY, // Fade to the desired final opacity
                Duration = new Duration(TimeSpan.FromMilliseconds(FADE_DURATION_MS)),
                // FillBehavior defaults to HoldEnd, which is what we want now
            };

            Storyboard.SetTarget(fadeOutAnimation, ChampionPortraitControl);
            Storyboard.SetTargetProperty(fadeOutAnimation, new PropertyPath(OpacityProperty));

            // --- Modified Completed Handler ---
            // We no longer hide the control or reset the flag here.
            // We just clear the storyboard reference *if* the flag is still true
            // (meaning StopFadeOutAnimation wasn't called prematurely).
            fadeOutAnimation.Completed += (s, e) => {
                 if (_isPortraitFadingOut) // Check if still supposed to be fading
                 {
                    _fadeOutStoryboard = null; // Animation is done, clear reference
                    Debug.WriteLine($"[OverlayWindow] Fade out completed. Opacity held at {FINAL_FADE_OPACITY}.");
                 } else {
                    Debug.WriteLine("[OverlayWindow] Fade out completed, but StopFadeOutAnimation was called before completion.");
                 }
            };

            _fadeOutStoryboard.Children.Add(fadeOutAnimation);
            _fadeOutStoryboard.Begin(ChampionPortraitControl, HandoffBehavior.SnapshotAndReplace, true);
            Debug.WriteLine("[OverlayWindow] Started fade out animation on ChampionPortraitControl");
        }

        private void StopFadeOutAnimation()
        {
             if (_isPortraitFadingOut)
             {
                 if (_fadeOutStoryboard != null)
                 {
                     _fadeOutStoryboard.Stop(ChampionPortraitControl);
                     _fadeOutStoryboard = null;
                     Debug.WriteLine("[OverlayWindow] Stopped active fade out animation.");
                 } else {
                      Debug.WriteLine("[OverlayWindow] Stopping fade state (animation already completed).");
                 }

                 _isPortraitFadingOut = false;
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
        public void SetEnemyJunglerInfo(string championName, string team)
        {
             if (ChampionPortraitControl == null) return; // Simplified null check
             Debug.WriteLine($"[OverlayWindow] Setting enemy jungler info: {championName} ({team})");

            ChampionPortraitControl.ChampionName = championName;
            ChampionPortraitControl.Team = team;

            _lastKnownLocation = null;
            StopFadeOutAnimation(); // This stops the fade, no longer stops warning implicitly
            ChampionPortraitControl.Opacity = 0.0;
            ChampionPortraitControl.Visibility = Visibility.Collapsed;
        }
    }
}