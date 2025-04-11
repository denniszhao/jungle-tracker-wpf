using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Timers;
using Timer = System.Timers.Timer; // Use alias to avoid conflict if System.Windows.Threading.Timer is used elsewhere

namespace JungleTracker
{
    public class MinimapCaptureService : IDisposable
    {
        public event Action<Bitmap?>? MinimapImageCaptured;

        private const string TargetProcessName = "League of Legends"; // Hardcoded process name
        private Timer? _screenshotTimer;
        private string? _screenshotFolder;
        private IntPtr _captureHwnd = IntPtr.Zero;
        private uint _leagueProcessId = 0;
        private double _overlaySize;
        private double _captureSize;
        private double _interval;
        private bool _isRunning = false;

        public MinimapCaptureService(double initialInterval) // Removed targetProcessName parameter
        {
            _interval = initialInterval > 0 ? initialInterval : 1000; // Ensure positive interval
            SetupScreenshotFolder();
        }

        public void UpdateCaptureParameters(double overlaySize, double captureSize)
        {
            _overlaySize = overlaySize;
            _captureSize = captureSize;
            // Debug.WriteLine($"[CaptureService] Updated parameters: Overlay={_overlaySize}, Capture={_captureSize}");
        }

        public bool Start()
        {
            if (_isRunning)
            {
                Debug.WriteLine("[CaptureService] Already running.");
                return true;
            }

            if (!FindTargetWindow())
            {
                Debug.WriteLine("[CaptureService] Failed to find target window on start.");
                // Optionally notify UI or throw? For now, show message box and return false.
                System.Windows.MessageBox.Show($"Could not find a window for process '{TargetProcessName}'.", "Capture Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return false;
            }

             // Verify window dimensions are initially valid
            Win32Helper.GetWindowRect(_captureHwnd, out Win32Helper.RECT rect);
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                 Debug.WriteLine($"[CaptureService] Initial target window dimensions invalid ({rect.Width}x{rect.Height}). PID: {_leagueProcessId}");
                 System.Windows.MessageBox.Show($"Target window for '{TargetProcessName}' has invalid dimensions. Is the game fully loaded?", "Capture Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                 _captureHwnd = IntPtr.Zero; // Reset handle
                 _leagueProcessId = 0;
                 return false;
            }

            Debug.WriteLine($"[CaptureService] Starting capture for window {_captureHwnd} (PID: {_leagueProcessId}) with interval {_interval}ms.");
            _screenshotTimer = new Timer(_interval);
            _screenshotTimer.Elapsed += CaptureTimerElapsed;
            _screenshotTimer.AutoReset = true;
            _screenshotTimer.Start();
            _isRunning = true;
            return true;
        }

        public void Stop()
        {
            if (!_isRunning) return;
            Debug.WriteLine("[CaptureService] Stopping capture.");
            _screenshotTimer?.Stop();
            _screenshotTimer?.Dispose();
            _screenshotTimer = null;
             _captureHwnd = IntPtr.Zero; // Clear handle on stop
             _leagueProcessId = 0;
            _isRunning = false;
        }

        public void SetScreenshotInterval(int milliseconds)
        {
             if (milliseconds <= 0) return;
             _interval = milliseconds;
             if (_screenshotTimer != null)
             {
                 _screenshotTimer.Interval = milliseconds;
                 Debug.WriteLine($"[CaptureService] Interval updated to {milliseconds}ms.");
             }
        }

        private void CaptureTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            // Execute the capture logic
            AttemptCapture();
        }

        private void SetupScreenshotFolder()
        {
            try
            {
                string baseFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                _screenshotFolder = Path.Combine(baseFolder, "JungleTracker", "Screenshots",
                                                DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
                Directory.CreateDirectory(_screenshotFolder);
                 Debug.WriteLine($"[CaptureService] Screenshot folder set up: {_screenshotFolder}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CaptureService] Error setting up screenshot folder: {ex.Message}");
                _screenshotFolder = null; // Indicate failure
            }
        }

        private bool FindTargetWindow()
        {
             Debug.WriteLine($"[CaptureService] Attempting to find window for process: '{TargetProcessName}'");
             _captureHwnd = IntPtr.Zero; // Reset before search
             _leagueProcessId = 0;

            Process[] processes = Process.GetProcessesByName(TargetProcessName); // Use constant

            if (processes.Length == 0)
            {
                Debug.WriteLine($"[CaptureService] Process '{TargetProcessName}' not found. Checking any visible window for testing..."); // Use constant
                // Fallback for testing: find *any* window with a title
                Process[] allProcesses = Process.GetProcesses();
                foreach (var proc in allProcesses)
                {
                    if (!string.IsNullOrEmpty(proc.MainWindowTitle) && proc.MainWindowHandle != IntPtr.Zero && proc.MainWindowHandle != System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle)
                    {
                        _captureHwnd = proc.MainWindowHandle;
                        _leagueProcessId = (uint)proc.Id;
                        Debug.WriteLine($"[CaptureService] Using fallback window '{proc.MainWindowTitle}' (PID: {proc.Id}) for testing capture.");
                        return true;
                    }
                }
                Debug.WriteLine($"[CaptureService] No suitable fallback window found either.");
                return false; // No window found
            }
            else
            {
                // Target process found - use the first one's main window handle
                 if (processes[0].MainWindowHandle == IntPtr.Zero) {
                    Debug.WriteLine($"[CaptureService] Process '{TargetProcessName}' found, but MainWindowHandle is IntPtr.Zero. Maybe not fully loaded?"); // Use constant
                    return false;
                 }
                _captureHwnd = processes[0].MainWindowHandle;
                _leagueProcessId = (uint)processes[0].Id;
                Debug.WriteLine($"[CaptureService] Found '{TargetProcessName}' window handle: {_captureHwnd} (PID: {_leagueProcessId})"); // Use constant
                return true;
            }
        }

        // Keeping the name for clarity, even though service could target other processes
        private bool IsLeagueOfLegendsFocused()
        {
            IntPtr focusedWindow = Win32Helper.GetForegroundWindow();
            if (focusedWindow == IntPtr.Zero) return false;

            // If we haven't found the window yet or lost the handle, we can't be focused
            if (_captureHwnd == IntPtr.Zero || _leagueProcessId == 0)
            {
                 // Attempt to re-find the window if focus check is called and handle is lost
                 // This helps if the game was closed and reopened while overlay was active.
                 if (!FindTargetWindow()) {
                     return false;
                 }
                 // If FindTargetWindow succeeded, _captureHwnd and _leagueProcessId are updated
            }

            // Direct check if the found window handle is the currently focused one
            if (focusedWindow == _captureHwnd) return true;

            // Fallback: Check if the focused window belongs to the correct process ID
            // This handles cases like modal dialogs within the game process.
            Win32Helper.GetWindowThreadProcessId(focusedWindow, out uint focusedProcessId);
            return focusedProcessId == _leagueProcessId;
        }

        private void AttemptCapture()
        {
            // Check focus first. If not focused, raise null event.
            if (!IsLeagueOfLegendsFocused())
            {
                // Debug.WriteLine("[CaptureService] Target window not focused. Skipping capture.");
                MinimapImageCaptured?.Invoke(null);
                return;
            }

            // If focus check succeeded but we lost the handle somehow (unlikely after IsFocused logic), bail out.
            if (_captureHwnd == IntPtr.Zero)
            {
                 Debug.WriteLine("[CaptureService] Capture window handle is zero despite focus check passing. Skipping.");
                 MinimapImageCaptured?.Invoke(null);
                 return;
            }


            Bitmap? croppedMinimap = null;
            // string? savedFilePath = null; // Optional: only used if saving is enabled

            try
            {
                // Get the window size
                Win32Helper.GetWindowRect(_captureHwnd, out Win32Helper.RECT rect);
                int width = rect.Width;
                int height = rect.Height;

                if (width <= 0 || height <= 0)
                {
                    Debug.WriteLine($"[CaptureService] Invalid window dimensions ({width}x{height}) during capture attempt.");
                    MinimapImageCaptured?.Invoke(null);
                    return;
                }

                // Ensure window is large enough for the overlay area (using latest known sizes)
                if (width < (int)_overlaySize || height < (int)_overlaySize)
                {
                    Debug.WriteLine($"[CaptureService] Window dimensions ({width}x{height}) smaller than overlay size ({_overlaySize}x{_overlaySize}). Skipping capture.");
                    MinimapImageCaptured?.Invoke(null);
                    return;
                }

                // Create a bitmap to hold the captured image
                using (Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb)) // Ensure Alpha channel for PrintWindow
                {
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                        IntPtr hdc = graphics.GetHdc();
                        bool success = false;
                        try
                        {
                            // Use PrintWindow with RENDERFULLCONTENT flag
                            success = Win32Helper.PrintWindow(_captureHwnd, hdc, Win32Helper.PW_RENDERFULLCONTENT);
                        }
                        finally
                        {
                            graphics.ReleaseHdc(hdc);
                        }

                        if (!success)
                        {
                            Debug.WriteLine("[CaptureService] PrintWindow failed.");
                            MinimapImageCaptured?.Invoke(null);
                            return;
                        }
                    } // Dispose graphics

                    // Calculate crop coordinates (using latest known sizes)
                    int minimapAreaX = width - (int)_overlaySize;
                    int minimapAreaY = height - (int)_overlaySize;
                    int padding = (_overlaySize > _captureSize && _captureSize > 0) ? (int)((_overlaySize - _captureSize) / 2.0) : 0;
                    int captureX = minimapAreaX + padding;
                    int captureY = minimapAreaY + padding;
                    int captureWidth = (int)_captureSize;
                    int captureHeight = (int)_captureSize;

                    // Ensure coordinates are valid for cropping
                    if (captureX < 0 || captureY < 0 ||
                        captureX + captureWidth > width ||
                        captureY + captureHeight > height ||
                        captureWidth <= 0 || captureHeight <= 0)
                    {
                        Debug.WriteLine($"[CaptureService] Calculated capture coordinates out of bounds. CX:{captureX}, CY:{captureY}, CW:{captureWidth}, CH:{captureHeight}, WinW:{width}, WinH:{height}");
                        MinimapImageCaptured?.Invoke(null);
                        return;
                    }

                    // Crop the full bitmap
                    // Need to handle potential ArgumentException if crop rect is invalid
                    try
                    {
                        croppedMinimap = bitmap.Clone(
                            new System.Drawing.Rectangle(captureX, captureY, captureWidth, captureHeight),
                            bitmap.PixelFormat); // Keep original pixel format for now
                    }
                    catch (ArgumentException argEx)
                    {
                        Debug.WriteLine($"[CaptureService] Error cloning bitmap for crop: {argEx.Message}. Crop Rect: {captureX},{captureY} {captureWidth}x{captureHeight}. Bitmap Size: {bitmap.Width}x{bitmap.Height}");
                        MinimapImageCaptured?.Invoke(null);
                        return; // Stop processing if crop failed
                    }

                } // Dispose bitmap

                 // --- Optional: Save screenshot ---
                 if (_screenshotFolder != null && croppedMinimap != null) {
                     try {
                          var savedFilePath = Path.Combine(_screenshotFolder, $"minimap_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                          croppedMinimap.Save(savedFilePath, ImageFormat.Png);
                     } catch (Exception ex) {
                          Debug.WriteLine($"[CaptureService] Error saving screenshot: {ex.Message}");
                     }
                 }
                 // --- End Optional Save ---

                // Raise the event with the cropped image
                MinimapImageCaptured?.Invoke(croppedMinimap);
                // Note: The receiver of the event is responsible for disposing the bitmap eventually.
                // We dispose the intermediate 'bitmap' but return 'croppedMinimap'.

            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CaptureService] Error during capture/processing: {ex.Message}\n{ex.StackTrace}");
                croppedMinimap?.Dispose(); // Dispose if created before error
                MinimapImageCaptured?.Invoke(null); // Raise event with null on error
            }
        }


        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
             if (disposing)
             {
                 // Dispose managed resources
                 Stop(); // Ensure timer is stopped and disposed
                 // No other managed resources to dispose directly here
             }
             // Dispose unmanaged resources (if any)
        }

        // Optional Finalizer (only if you have unmanaged resources directly in THIS class)
        // ~MinimapCaptureService()
        // {
        //     Dispose(false);
        // }
    }
}
