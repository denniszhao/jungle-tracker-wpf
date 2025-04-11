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

        // Add fields for animation
        private Storyboard? _fadeOutStoryboard;
        private bool _isPortraitFadingOut = false;
        private System.Drawing.Point? _lastKnownLocation;

        // Add field for the minimap scanner service
        private MinimapScannerService _minimapScanner;
        // --- Add field for the minimap capture service ---
        private MinimapCaptureService _minimapCaptureService; 

        // List to hold references to the XAML zone polygons
        private List<Polygon> _allZonePolygons;

        // --- Brushes for Zone Highlighting ---
        private static readonly System.Windows.Media.Brush _defaultZoneFill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(50, 255, 0, 0)); // #55FF0000
        private static readonly System.Windows.Media.Brush _highlightZoneFill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(50, 255, 255, 0)); // #55FFFF00

        /// <summary>
        /// Initializes a new instance of the OverlayWindow class.
        /// Sets up window properties, initializes components, populates zone polygons,
        /// applies debug visuals if enabled, and initializes services.
        /// </summary>
        public OverlayWindow()
        {
            // These MUST be set before InitializeComponent() is called
            this.AllowsTransparency = true;
            this.WindowStyle = WindowStyle.None;
            this.Background = System.Windows.Media.Brushes.Transparent;
            this.ResizeMode = ResizeMode.NoResize;
            
            InitializeComponent();
            
            // Populate the list of zone polygons AFTER InitializeComponent
            _allZonePolygons = new List<Polygon>
            {
                TopZonePolygon, RedTopJgZonePolygon, RedBaseZonePolygon, RedBotJgZonePolygon,
                BotZonePolygon, BlueBotJgZonePolygon, BlueBaseZonePolygon, BlueTopJgZonePolygon,
                TopRiverZonePolygon, MidZonePolygon, BotRiverZonePolygon
            };
            
            // Apply debug border if in debug mode
            if (DEBUG_MODE)
            {
                MainBorder.BorderBrush = System.Windows.Media.Brushes.Red;
                MainBorder.BorderThickness = new Thickness(3);

                // --- Make all zone polygons visible and semi-transparent for debugging --- 
                foreach (var polygon in _allZonePolygons)
                {
                    polygon.Visibility = Visibility.Visible;
                    polygon.Fill = _defaultZoneFill; 
                }
                Debug.WriteLine("[OverlayWindow DEBUG] Made all zone polygons visible in Constructor.");
            }
            
            // Add a source initialize handler to make sure window style is set after window handle is created
            this.SourceInitialized += OverlayWindow_SourceInitialized;
            
            // Create the minimap scanner service
            _minimapScanner = new MinimapScannerService();

            // --- Create the minimap capture service ---
            _minimapCaptureService = new MinimapCaptureService(DEFAULT_SCREENSHOT_INTERVAL); // No process name needed
            _minimapCaptureService.MinimapImageCaptured += OnMinimapImageCaptured; // Subscribe to event

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
        /// Event handler called when the MinimapCaptureService captures a new minimap image (or fails to).
        /// Ensures execution on the UI thread and passes the bitmap to ProcessMinimapScreenshot.
        /// </summary>
        /// <param name="capturedBitmap">The captured minimap Bitmap, or null if capture failed or was skipped.</param>
        private void OnMinimapImageCaptured(Bitmap? capturedBitmap)
        {
             // Ensure execution on the UI thread
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => OnMinimapImageCaptured(capturedBitmap));
                return;
            }

            if (capturedBitmap != null)
            {
                 // Debug.WriteLine("[OverlayWindow] Received minimap image from service.");
                 // Process the valid screenshot
                 if (_minimapScanner != null &&
                     ChampionPortraitControl != null &&
                     !string.IsNullOrEmpty(ChampionPortraitControl.ChampionName))
                 {
                     ProcessMinimapScreenshot(capturedBitmap);
                 }
                 else
                 {
                      // If scanner or control invalid, make sure to dispose the bitmap
                      capturedBitmap.Dispose();
                 }
            }
            else
            {
                 // Debug.WriteLine("[OverlayWindow] Received null bitmap (capture skipped or failed).");
                 // Handle the case where capture failed or game wasn't focused
                 // Maybe ensure portrait/zone are hidden if they weren't already?
                 // If the game loses focus mid-fade, the fade continues but won't restart
                 // until focus returns and the champion is still missing.
                 // If the game loses focus when portrait is shown, it stays shown until next valid scan.
                 // This seems acceptable for now. We could add logic here to hide things
                 // if the game loses focus, but let's keep it simple first.
            }
        }

        /// <summary>
        /// Processes a captured minimap screenshot to find the enemy jungler.
        /// Updates the UI (hides portrait if found, shows fading portrait and zone highlight if not found).
        /// </summary>
        /// <param name="minimapBitmap">The minimap Bitmap to process. This method takes ownership and disposes the bitmap.</param>
        private void ProcessMinimapScreenshot(Bitmap minimapBitmap)
        {
            if (ChampionPortraitControl == null)
            {
                minimapBitmap.Dispose(); // Dispose bitmap if control is null
                return;
            }

            try
            {
                // Ensure the template is set based on the control's properties
                if (!_minimapScanner.SetChampionTemplate(ChampionPortraitControl.ChampionName, ChampionPortraitControl.Team))
                {
                    Debug.WriteLine($"[OverlayWindow] Failed to set champion template for {ChampionPortraitControl.ChampionName}");
                    return; // Don't dispose bitmap here, Scanner might hold it? No, scanner uses its own.
                }

                System.Drawing.Point? matchLocation = _minimapScanner.ScanForChampion(minimapBitmap);

                if (matchLocation.HasValue)
                {
                    // --- Champion found ---
                    _lastKnownLocation = matchLocation;

                    if (_isPortraitFadingOut)
                    {
                        StopFadeOutAnimation(); // Stop fade-out
                    }

                    ChampionPortraitControl.Visibility = Visibility.Collapsed;
                    ChampionPortraitControl.Opacity = 0.0;
                    HideAllZonePolygons(); // Hide zone polygon if champion is found

                    Debug.WriteLine($"[OverlayWindow] Champion found at {matchLocation.Value.X}, {matchLocation.Value.Y}. Hiding portrait and zone.");
                }
                else // --- Champion NOT found ---
                {
                    if (_lastKnownLocation.HasValue)
                    {
                        // Highlight the correct zone and move the portrait to the last known location
                        UpdateZoneHighlight(_lastKnownLocation.Value);
                        UpdateChampionPortraitPosition(_lastKnownLocation.Value); 

                        // Only start the fade-out if the portrait is not already fading out
                        if (!_isPortraitFadingOut)
                        {
                             Debug.WriteLine("[OverlayWindow] Champion not found. Starting fade-out and showing zone."); // Message updated
                             ChampionPortraitControl.Visibility = Visibility.Visible;
                             ChampionPortraitControl.Opacity = 1.0;
                             StartFadeOutAnimation(); 
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
                        HideAllZonePolygons(); // Ensure zone is hidden
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
            finally
            {
                // --- IMPORTANT: Dispose the bitmap received from the service ---
                minimapBitmap?.Dispose();
            }
        }

        /// <summary>
        /// Updates the position of the champion portrait based on its detected location on the minimap.
        /// Calculates padding and adjusts coordinates relative to the overlay size.
        /// </summary>
        /// <param name="matchLocation">The location where the champion was detected within the captured minimap area.</param>
        private void UpdateChampionPortraitPosition(System.Drawing.Point matchLocation)
        {
            if (ChampionPortraitControl == null) return; // Safety check

            // --- Calculate padding ---
            // This is the offset between the full overlay size and the captured area size
            double padding = (_overlaySize > _captureSize && _captureSize > 0) ? (_overlaySize - _captureSize) / 2.0 : 0;

            // --- Adjust match location based on padding ---
            // Since the scan was done on a smaller, centered image, add the padding
            // back to the found coordinates to place the portrait relative to the full overlay.
            double adjustedMatchX = matchLocation.X + padding;
            double adjustedMatchY = matchLocation.Y + padding;

            // Use the UserControl's actual dimensions for centering
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

            double x = adjustedMatchX - (controlWidth / 2); // Use adjusted coordinates
            double y = adjustedMatchY - (controlHeight / 2); // Use adjusted coordinates

            // Ensure staying within overlay bounds (using control's dimensions)
            x = Math.Max(0, Math.Min(x, _overlaySize - controlWidth));
            y = Math.Max(0, Math.Min(y, _overlaySize - controlHeight));

            // Update the Margin of the ChampionPortraitControl itself
            ChampionPortraitControl.Margin = new Thickness(x, y, 0, 0);
        }

        /// <summary>
        /// Updates the visibility and fill of the zone polygons based on the last known location.
        /// Hides all polygons, then highlights the one containing the location (if found).
        /// </summary>
        /// <param name="lastKnownLocation">The last known location of the champion within the captured minimap area.</param>
        private void UpdateZoneHighlight(System.Drawing.Point lastKnownLocation)
        {
            HideAllZonePolygons(); // Start by resetting all fills (and hiding if not debug)

            // Calculate scale factor (relative to the 510 reference size used in XAML)
            double scaleFactor = _captureSize > 0 ? _captureSize / 510.0 : 1.0;
            if (scaleFactor <= 0) return; // Avoid division by zero or invalid scale

            // Convert last known location (relative to captured image) to the 510x510 reference coordinate system
            System.Windows.Point referencePoint = new System.Windows.Point(
                lastKnownLocation.X / scaleFactor,
                lastKnownLocation.Y / scaleFactor
            );

            Polygon? foundPolygon = null;
            foreach (var polygon in _allZonePolygons)
            {
                // Use FillContains on the polygon's geometry for accurate hit testing
                if (polygon.RenderedGeometry != null && polygon.RenderedGeometry.FillContains(referencePoint))
                {
                    foundPolygon = polygon;
                    break; // Found the zone, stop checking (can remove break to highlight multiple)
                }
            }

            if (foundPolygon != null)
            {
                // In debug mode, all stay visible but found one turns yellow.
                // In release mode, only the found one becomes visible (and yellow).
                foundPolygon.Fill = _highlightZoneFill; // Set highlight color

                if (!DEBUG_MODE)
                {
                    foundPolygon.Visibility = Visibility.Visible; // Show the found polygon
                    Debug.WriteLine($"[OverlayWindow] Point {referencePoint} found in polygon {foundPolygon.Name}. Making it visible with highlight.");
                }
                else {
                     // All are already visible in debug, just log the highlight
                     Debug.WriteLine($"[OverlayWindow DEBUG] Point {referencePoint} found in polygon {foundPolygon.Name}. Applied highlight fill.");
                }
            }
            else
            {
                 // No polygon found, HideAllZonePolygons already reset state.
                 Debug.WriteLine($"[OverlayWindow] Point {referencePoint} not found in any defined zone polygon points list."); 
            }
        }

        /// <summary>
        /// Resets all zone polygons to their default fill color.
        /// If not in DEBUG_MODE, also sets their visibility to Collapsed.
        /// </summary>
        private void HideAllZonePolygons()
        {
            if (_allZonePolygons == null) return;

            foreach (var polygon in _allZonePolygons)
            {
                 polygon.Fill = _defaultZoneFill; // ALWAYS Reset fill to default
                 if (!DEBUG_MODE) // ONLY collapse if not in debug mode
                 {
                    polygon.Visibility = Visibility.Collapsed;
                 }
            }
        }

        /// <summary>
        /// Starts the fade-out animation for the champion portrait.
        /// </summary>
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

        /// <summary>
        /// Stops the active fade-out animation for the champion portrait and resets the fading flag.
        /// </summary>
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

        // Removed AttemptCapture() method

        /// <summary>
        /// Shows the overlay window, calculates its size and position based on settings,
        /// updates zone transforms, starts the capture service, and makes the window visible.
        /// </summary>
        /// <param name="location">The corner of the screen where the overlay should be placed ("Top Left", "Top Right", etc.).</param>
        /// <param name="scale">The scale percentage (0-100) used to determine the overlay size.</param>
        public void ShowOverlay(string location, int scale)
        {
            // Calculate size and initial position
            _overlaySize = MIN_MINIMAP_SIZE + (scale / 100.0) * (MAX_MINIMAP_SIZE - MIN_MINIMAP_SIZE);
            _captureSize = MIN_CAPTURE_SIZE + (scale / 100.0) * (MAX_CAPTURE_SIZE - MIN_CAPTURE_SIZE); // Calculate capture size
            this.Width = this.Height = _overlaySize; // Overlay window remains the original size
            
            // --- Update Zone Transforms ---
            UpdateZoneTransforms();
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
            
            // --- Configure and Start Capture Service ---
            if (_minimapCaptureService != null)
            {
                try
                {
                    Debug.WriteLine($"[OverlayWindow] Configuring capture service: Overlay={_overlaySize}, Capture={_captureSize}");
                    _minimapCaptureService.UpdateCaptureParameters(_overlaySize, _captureSize);
                    
                    Debug.WriteLine("[OverlayWindow] Attempting to start capture service...");
                    if (!_minimapCaptureService.Start())
                    {
                         // Start() already shows a MessageBox on failure
                         Debug.WriteLine("[OverlayWindow] Capture service failed to start. Closing overlay.");
                         this.Close(); // Close overlay if service cannot start
                         return;
                    }
                    Debug.WriteLine("[OverlayWindow] Capture service started successfully.");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[OverlayWindow] Error starting capture service: {ex.Message}");
                    System.Windows.MessageBox.Show($"Error initializing capture: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    this.Close();
                    return;
                }
            }
            // Removed old timer start and window finding logic
            // ---
            
            // Show and activate the overlay window
            this.Topmost = true; 
            this.Show();
            this.Topmost = true;
            this.Activate();
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
            
            // Dispose of the minimap capture service
            _minimapCaptureService?.Stop(); // Ensure it's stopped first
            _minimapCaptureService?.Dispose();
            _minimapCaptureService = null;
            
            // Dispose of the minimap scanner service
            _minimapScanner?.Dispose();
            _minimapScanner = null;
            
            Debug.WriteLine("[OverlayWindow] Resource cleanup complete.");
        }

        /// <summary>
        /// Updates the screenshot interval used by the MinimapCaptureService.
        /// </summary>
        /// <param name="milliseconds">The desired interval between captures in milliseconds.</param>
        internal void SetScreenshotInterval(int milliseconds)
        {
            if (_minimapCaptureService != null && milliseconds > 0)
            {
                 Debug.WriteLine($"[OverlayWindow] Setting capture interval via service to {milliseconds}ms");
                 _minimapCaptureService.SetScreenshotInterval(milliseconds);
            }
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

            // 1. Stop any ongoing fade-out animation and reset its state flag
            StopFadeOutAnimation();

            // 2. Hide the portrait control immediately
            if (ChampionPortraitControl != null)
            {
                ChampionPortraitControl.Visibility = Visibility.Collapsed;
                ChampionPortraitControl.Opacity = 0.0;

                // 3. Optionally clear the champion info (triggers image source nulling)
                 ChampionPortraitControl.ChampionName = string.Empty;
                 ChampionPortraitControl.Team = string.Empty;
            }

            // 4. Reset the last known location
            _lastKnownLocation = null;

            // 5. Clear the template in the scanner service
            _minimapScanner?.ClearCurrentTemplate();

            // 6. Hide the zone polygon
            HideAllZonePolygons();

             // 7. Capture service will stop automatically when game loses focus/closes.
             // No explicit stop call needed here anymore.
        }

        /// <summary>
        /// Sets the information for the enemy jungler to be tracked.
        /// Resets the UI state (hides portrait, stops animation, clears last location).
        /// </summary>
        /// <param name="championName">The name of the enemy jungler champion.</param>
        /// <param name="team">The team identifier for the enemy jungler (e.g., "Blue" or "Red").</param>
        public void SetEnemyJunglerInfo(string championName, string team)
        {
             if (ChampionPortraitControl == null) return; // Simplified null check
             Debug.WriteLine($"[OverlayWindow] Setting enemy jungler info: {championName} ({team})");

            ChampionPortraitControl.ChampionName = championName;
            ChampionPortraitControl.Team = team;

            _lastKnownLocation = null;
            StopFadeOutAnimation();
            ChampionPortraitControl.Opacity = 0.0;
            ChampionPortraitControl.Visibility = Visibility.Collapsed;
            HideAllZonePolygons(); // Hide zone polygon when info is reset

            // Capture service will resume automatically when game is focused again.
            // No explicit timer start needed here.
        }

        /// <summary>
        /// Updates the RenderTransform (Scale and Translate) of the ZoneHighlightCanvas
        /// based on the current overlay and capture sizes.
        /// This ensures the zone polygons scale and position correctly relative to the captured minimap area.
        /// </summary>
        private void UpdateZoneTransforms()
        {
            if (_captureSize <= 0 || ZoneScaleTransform == null || ZoneTranslateTransform == null) return;

            // Scale factor relative to the 510x510 reference size the polygons were defined with
            double scaleFactor = _captureSize / 510.0;
            double padding = (_overlaySize - _captureSize) / 2.0;

            ZoneScaleTransform.ScaleX = scaleFactor;
            ZoneScaleTransform.ScaleY = scaleFactor;
            ZoneTranslateTransform.X = padding;
            ZoneTranslateTransform.Y = padding;

            Debug.WriteLine($"[OverlayWindow] Updated Zone Transforms: Scale={scaleFactor:F2}, Padding={padding:F1}");
        }
    }
}