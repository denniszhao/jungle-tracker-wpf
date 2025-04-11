using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace JungleTracker
{
    public class MinimapCaptureService : IDisposable
    {
        private string? _screenshotFolder;
        private IntPtr _captureHwnd = IntPtr.Zero;
        private uint _leagueProcessId = 0;
        private double _overlaySize;
        private double _captureSize;

        public MinimapCaptureService()
        {
            SetupScreenshotFolder();
        }

        public void UpdateCaptureParameters(double overlaySize, double captureSize)
        {
            _overlaySize = overlaySize;
            _captureSize = captureSize;
            // Debug.WriteLine($"[CaptureService] Updated parameters: Overlay={_overlaySize}, Capture={_captureSize}");
        }

        /// <summary>
        /// Captures a single frame from the League of Legends window.
        /// </summary>
        /// <param name="overlaySize">The size of the overlay window.</param>
        /// <param name="captureSize">The size of the minimap capture area.</param>
        /// <returns>A Bitmap containing the captured minimap or null if capture failed. The caller must dispose the Bitmap.</returns>
        public Bitmap? CaptureCurrentFrame(double overlaySize, double captureSize)
        {
            // Store the current parameters for this capture
            _overlaySize = overlaySize;
            _captureSize = captureSize;

            // Check if we have a valid window handle, if not try to find it
            if (_captureHwnd == IntPtr.Zero)
            {
                if (!FindTargetWindow())
                {
                    Debug.WriteLine("[CaptureService] Failed to find target window for capture.");
                    return null;
                }
            }

            // Check if the game window is focused
            if (!IsLeagueOfLegendsFocused())
            {
                Debug.WriteLine("[CaptureService] Target window not focused. Skipping capture.");
                return null;
            }

            // If handle is still invalid (unlikely after above checks), bail out
            if (_captureHwnd == IntPtr.Zero)
            {
                Debug.WriteLine("[CaptureService] Capture window handle is zero despite focus check passing. Skipping.");
                return null;
            }

            try
            {
                // Get the window size
                Win32Helper.GetWindowRect(_captureHwnd, out Win32Helper.RECT rect);
                int width = rect.Width;
                int height = rect.Height;

                if (width <= 0 || height <= 0)
                {
                    Debug.WriteLine($"[CaptureService] Invalid window dimensions ({width}x{height}) during capture attempt.");
                    return null;
                }

                // Ensure window is large enough for the overlay area
                if (width < (int)_overlaySize || height < (int)_overlaySize)
                {
                    Debug.WriteLine($"[CaptureService] Window dimensions ({width}x{height}) smaller than overlay size ({_overlaySize}x{_overlaySize}). Skipping capture.");
                    return null;
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
                            return null;
                        }
                    } // Dispose graphics

                    // Calculate crop coordinates
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
                        return null;
                    }

                    // Crop the full bitmap
                    try
                    {
                        Bitmap croppedMinimap = bitmap.Clone(
                            new System.Drawing.Rectangle(captureX, captureY, captureWidth, captureHeight),
                            bitmap.PixelFormat);
                            
                        // --- Optional: Save screenshot ---
                        if (_screenshotFolder != null) {
                            try {
                                var savedFilePath = Path.Combine(_screenshotFolder, $"minimap_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                                croppedMinimap.Save(savedFilePath, ImageFormat.Png);
                            } catch (Exception ex) {
                                Debug.WriteLine($"[CaptureService] Error saving screenshot: {ex.Message}");
                            }
                        }
                        // --- End Optional Save ---
                        
                        return croppedMinimap;
                    }
                    catch (ArgumentException argEx)
                    {
                        Debug.WriteLine($"[CaptureService] Error cloning bitmap for crop: {argEx.Message}. Crop Rect: {captureX},{captureY} {captureWidth}x{captureHeight}. Bitmap Size: {bitmap.Width}x{bitmap.Height}");
                        return null;
                    }
                } // Dispose bitmap
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CaptureService] Error during capture/processing: {ex.Message}\n{ex.StackTrace}");
                return null;
            }
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
             Debug.WriteLine($"[CaptureService] Attempting to find window for process: 'League of Legends'");
             _captureHwnd = IntPtr.Zero; // Reset before search
             _leagueProcessId = 0;

            Process[] processes = Process.GetProcessesByName("League of Legends");

            if (processes.Length == 0)
            {
                Debug.WriteLine($"[CaptureService] Process 'League of Legends' not found. Checking any visible window for testing...");
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
                    Debug.WriteLine($"[CaptureService] Process 'League of Legends' found, but MainWindowHandle is IntPtr.Zero. Maybe not fully loaded?");
                    return false;
                 }
                _captureHwnd = processes[0].MainWindowHandle;
                _leagueProcessId = (uint)processes[0].Id;
                Debug.WriteLine($"[CaptureService] Found 'League of Legends' window handle: {_captureHwnd} (PID: {_leagueProcessId})");
                return true;
            }
        }

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

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
             if (disposing)
             {
                 // Dispose managed resources if needed
                 // No timer or event handlers to dispose anymore
             }
             // Dispose unmanaged resources (if any)
        }
    }
}
