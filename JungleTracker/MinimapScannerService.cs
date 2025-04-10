using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using OpenCvSharp;

namespace JungleTracker
{
    public class MinimapScannerService : IDisposable
    {
        // Constants for template matching - Thresholds often need tuning!
        // For CCoeffNormed (higher is better match): Start high (e.g., 0.7-0.8) and adjust.
        // For SqDiffNormed (lower is better match): Start low (e.g., 0.1-0.2) and adjust.
        private const double DEFAULT_MATCH_THRESHOLD = 0.6; // Adjusted default for CCoeffNormed, REQUIRES TESTING!
        private const TemplateMatchModes DEFAULT_MATCH_MODE = TemplateMatchModes.CCoeffNormed;

        // Current matching configuration
        private double _matchThreshold;
        private TemplateMatchModes _matchMode;

        // Champion template (BGR) and its Alpha Mask (if available)
        private Mat? _championTemplate;
        private Mat? _championTemplateMask;
        private string? _currentChampionName;
        private string? _currentTeam;

        public MinimapScannerService(double matchThreshold = DEFAULT_MATCH_THRESHOLD,
                                    TemplateMatchModes matchMode = DEFAULT_MATCH_MODE)
        {
            _matchThreshold = matchThreshold;
            _matchMode = matchMode;
        }

        /// <summary>
        /// Sets the champion template to use for detection. Uses the original size from the resource.
        /// </summary>
        public bool SetChampionTemplate(string championName, string team)
        {
            if (string.IsNullOrEmpty(championName) || string.IsNullOrEmpty(team))
            {
                Debug.WriteLine("[Scanner] SetChampionTemplate called with null/empty championName or team.");
                return false;
            }

            // Check if the requested template is already loaded and valid
            // Added null checks and IsDisposed checks for more safety
            if (championName.Equals(_currentChampionName, StringComparison.Ordinal) &&
                team.Equals(_currentTeam, StringComparison.OrdinalIgnoreCase) &&
                _championTemplate != null && !_championTemplate.IsDisposed && !_championTemplate.Empty())
            {
                // It's already loaded and valid, no need to reload.
                // Debug.WriteLine($"[Scanner] Template for {championName} ({team}) already loaded."); // Optional: Reduce log spam
                return true;
            }

            Debug.WriteLine($"[Scanner] Request to load template for NEW/DIFFERENT champion/team: {championName} ({team}). Previous: {_currentChampionName} ({_currentTeam})");

            // --- Explicitly Dispose BEFORE loading ---
            Debug.WriteLine("[Scanner] Disposing existing template and mask (if any)...");
            DisposeTemplateAndMask(); // This sets _championTemplate and _championTemplateMask to null
            Debug.WriteLine($"[Scanner] Disposal complete.");
            // ---

            Mat? newTemplate = null;
            Mat? newMask = null;
            bool success = false;

            try
            {
                // --- Use the helper method for normalization ---
                string championFileName = ChampionNameHelper.Normalize(championName);
                // --- Removed old if checks ---
                // if (championFileName == "Wukong") ...

                string resourceFolder = team.Equals("CHAOS", StringComparison.OrdinalIgnoreCase) ? "champions_altered_red" : "champions_altered_blue";
                string resourceName = $"JungleTracker.Assets.Champions.{resourceFolder}.{championFileName}.png";
                Debug.WriteLine($"[Scanner] Loading resource: {resourceName}");

                Assembly assembly = Assembly.GetExecutingAssembly();
                using (Stream? resourceStream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (resourceStream == null)
                    {
                        Debug.WriteLine($"[Scanner] Error: Resource stream not found for {resourceName}");
                        _currentChampionName = null; // Clear state as loading failed
                        _currentTeam = null;
                        return false; // Template resource not found
                    }

                    using (Bitmap bitmap = new Bitmap(resourceStream))
                    {
                        // Use a temporary variable for the potentially-alpha Mat
                        using (Mat? tempMat = BitmapToMat(bitmap, preserveAlpha: true))
                        {
                            if (tempMat == null || tempMat.Empty())
                            {
                                Debug.WriteLine($"[Scanner] Error: Failed to convert bitmap to Mat for {resourceName}");
                                _currentChampionName = null; // Clear state
                                _currentTeam = null;
                                return false;
                            }

                            Debug.WriteLine($"[Scanner] Loaded temporary Mat for {resourceName}. Channels: {tempMat.Channels()}");

                            if (tempMat.Channels() == 4)
                            {
                                Debug.WriteLine("[Scanner] Extracting BGR template and Alpha mask from 4 channels.");
                                Mat[] channels = Cv2.Split(tempMat);
                                try
                                {
                                    // IMPORTANT: Clone the mask, don't just assign reference
                                    newMask = channels[3].Clone();
                                    // Create the BGR template by merging
                                    newTemplate = new Mat();
                                    Cv2.Merge(new[] { channels[0], channels[1], channels[2] }, newTemplate);

                                    Debug.WriteLine($"[Scanner] Successfully created NEW BGR template ({newTemplate?.Width}x{newTemplate?.Height}) and Alpha mask ({newMask?.Width}x{newMask?.Height}).");
                                }
                                finally
                                {
                                    // Dispose all split channels regardless of success/failure in try block
                                    foreach (var channel in channels) channel?.Dispose();
                                }
                            }
                            else if (tempMat.Channels() == 3)
                            {
                                Debug.WriteLine("[Scanner] Cloning 3-channel BGR template. No mask generated.");
                                // IMPORTANT: Clone the template, don't just assign reference
                                newTemplate = tempMat.Clone();
                                newMask = null; // Ensure mask is null
                            }
                            else
                            {
                                Debug.WriteLine($"[Scanner] Error: Temporary Mat has unexpected number of channels: {tempMat.Channels()}");
                                _currentChampionName = null; // Clear state
                                _currentTeam = null;
                                return false; // Unexpected channel count
                            }
                        } // Dispose tempMat here
                    } // Dispose bitmap here
                } // Dispose resourceStream here

                // --- Assign new Mats AFTER successful loading ---
                _championTemplate = newTemplate; // Assign the successfully created new template
                _championTemplateMask = newMask; // Assign the successfully created new mask (or null)
                _currentChampionName = championName;
                _currentTeam = team;
                success = true;
                // ---

                Debug.WriteLine($"[Scanner] NEW Champion template set for {championName} (Normalized: {championFileName}, Team: {team}). Template Valid: {!_championTemplate?.IsDisposed ?? false}. Mask Valid: {!_championTemplateMask?.IsDisposed ?? true}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Scanner] General Error loading champion template {championName}: {ex.Message}");
                Debug.WriteLine(ex.StackTrace);
                // Clean up potentially created new Mats on error
                newTemplate?.Dispose();
                newMask?.Dispose();
                // Ensure instance variables are null after failure
                _championTemplate = null; // Already done by DisposeTemplateAndMask below, but explicit
                _championTemplateMask = null;
                _currentChampionName = null;
                _currentTeam = null;
                return false;
            }
            finally
            {
                // If loading failed before assignment, ensure the instance variables are cleaned up.
                if (!success)
                {
                    DisposeTemplateAndMask(); // Ensure instance vars are null and disposed
                     _currentChampionName = null; // Make sure name/team are cleared on any failure path
                     _currentTeam = null;
                }
            }
        }

        /// <summary>
        /// Scans the minimap image for the currently set champion template.
        /// </summary>
        /// <returns>Point with center coordinates if found, null if not found or error.</returns>
        public System.Drawing.Point? ScanForChampion(Bitmap minimapImage)
        {
            // Check if a valid template is loaded (Added IsDisposed checks)
            if (_championTemplate == null || _championTemplate.IsDisposed || _championTemplate.Empty() || minimapImage == null)
            {
                // Be more specific in logging why it failed
                if (_championTemplate == null) Debug.WriteLine("[Scanner] ScanForChampion failed: Template is null.");
                else if (_championTemplate.IsDisposed) Debug.WriteLine("[Scanner] ScanForChampion failed: Template is disposed.");
                else if (_championTemplate.Empty()) Debug.WriteLine("[Scanner] ScanForChampion failed: Template is empty.");
                if (minimapImage == null) Debug.WriteLine("[Scanner] ScanForChampion failed: Minimap image is null.");
                return null;
            }

            // Check if template dimensions are valid
             if (_championTemplate.Width <= 0 || _championTemplate.Height <= 0)
             {
                 Debug.WriteLine($"ScanForChampion failed: Invalid template dimensions ({_championTemplate.Width}x{_championTemplate.Height}).");
                 return null;
             }

            // Check if minimap dimensions are valid and larger than template
            if (minimapImage.Width < _championTemplate.Width || minimapImage.Height < _championTemplate.Height)
            {
                Debug.WriteLine($"ScanForChampion failed: Minimap image ({minimapImage.Width}x{minimapImage.Height}) is smaller than template ({_championTemplate.Width}x{_championTemplate.Height}).");
                return null;
            }

            try
            {
                Debug.WriteLine($"Starting scan for {_currentChampionName} ({_currentTeam}) on minimap {minimapImage.Width}x{minimapImage.Height}. Template: {_championTemplate.Width}x{_championTemplate.Height}, Mode: {_matchMode}, Threshold: {_matchThreshold}");

                // Convert minimap image to BGR Mat (ensure no alpha processing for source)
                using (Mat sourceMat = BitmapToMat(minimapImage, preserveAlpha: false))
                {
                    if (sourceMat == null || sourceMat.Empty())
                    {
                         Debug.WriteLine("ScanForChampion failed: Could not convert minimap bitmap to Mat.");
                         return null;
                    }
                    Debug.WriteLine($"Converted minimap to Mat: {sourceMat.Width}x{sourceMat.Height}, Type: {sourceMat.Type()}");

                    // Calculate result matrix dimensions
                    int resultRows = sourceMat.Rows - _championTemplate.Rows + 1;
                    int resultCols = sourceMat.Cols - _championTemplate.Cols + 1;

                    // Ensure result dimensions are valid
                    if (resultRows <= 0 || resultCols <= 0)
                    {
                        Debug.WriteLine($"ScanForChampion failed: Invalid result matrix dimensions ({resultCols}x{resultRows}). Source: {sourceMat.Rows}x{sourceMat.Cols}, Template: {_championTemplate.Rows}x{_championTemplate.Cols}");
                        return null;
                    }

                    // Create a result matrix to store matching scores
                    using (Mat result = new Mat(resultRows, resultCols, MatType.CV_32FC1))
                    {
                        // Perform template matching
                        bool useMask = _championTemplateMask != null && !_championTemplateMask.IsDisposed && !_championTemplateMask.Empty() && _championTemplateMask.Size() == _championTemplate.Size();
                        if (useMask)
                        {
                            Debug.WriteLine("Using alpha mask for template matching.");
                            Cv2.MatchTemplate(sourceMat, _championTemplate, result, _matchMode, _championTemplateMask);
                        }
                        else
                        {
                            if (_championTemplateMask != null && !_championTemplateMask.IsDisposed && !_championTemplateMask.Empty() && _championTemplateMask.Size() != _championTemplate.Size())
                            {
                                Debug.WriteLine("Warning: Mask exists but size does not match template. Performing regular matching.");
                            }
                            else
                            {
                                Debug.WriteLine("Mask not available or empty. Performing regular template matching.");
                            }
                            Cv2.MatchTemplate(sourceMat, _championTemplate, result, _matchMode);
                        }

                        // Find the best match location and score
                        Cv2.MinMaxLoc(result, out double minVal, out double maxVal,
                                     out OpenCvSharp.Point minLoc, out OpenCvSharp.Point maxLoc);

                        Debug.WriteLine($"Template matching results - MinVal: {minVal:F4}, MaxVal: {maxVal:F4}");
                        Debug.WriteLine($"Min location: ({minLoc.X}, {minLoc.Y}), Max location: ({maxLoc.X}, {maxLoc.Y})");

                        // Determine the best match score and location based on the mode
                        double matchValue;
                        OpenCvSharp.Point matchLocation;

                        if (_matchMode == TemplateMatchModes.SqDiff || _matchMode == TemplateMatchModes.SqDiffNormed)
                        {
                            matchValue = minVal;
                            matchLocation = minLoc;
                        }
                        else // CCoeff, CCoeffNormed, CCorr, CCorrNormed
                        {
                            matchValue = maxVal;
                            matchLocation = maxLoc;
                        }

                        // Check if the match score meets the threshold
                        bool isGoodMatch;
                        if (_matchMode == TemplateMatchModes.SqDiff || _matchMode == TemplateMatchModes.SqDiffNormed)
                        {
                            isGoodMatch = matchValue < _matchThreshold; // Lower is better
                        }
                        else
                        {
                            isGoodMatch = matchValue > _matchThreshold; // Higher is better
                        }

                        Debug.WriteLine($"Best match score: {matchValue:F4} at ({matchLocation.X}, {matchLocation.Y}). Required threshold: {(_matchMode == TemplateMatchModes.SqDiff || _matchMode == TemplateMatchModes.SqDiffNormed ? "<" : ">")} {_matchThreshold}. Good match: {isGoodMatch}");

                        if (isGoodMatch)
                        {
                            // Return the center point of the matched region
                            System.Drawing.Point resultPoint = new System.Drawing.Point(
                                matchLocation.X + _championTemplate.Width / 2,
                                matchLocation.Y + _championTemplate.Height / 2
                            );
                            Debug.WriteLine($"Match found. Center point: ({resultPoint.X}, {resultPoint.Y})");
                            return resultPoint;
                        }

                        Debug.WriteLine("No match found meeting the threshold criteria.");
                        return null;
                    }
                }
            }
            catch (OpenCVException cvEx)
            {
                 // Log the standard exception message and stack trace,
                 // which usually includes the relevant OpenCV error details.
                 Debug.WriteLine($"OpenCV Error during template matching: {cvEx.Message}");
                 Debug.WriteLine(cvEx.StackTrace);
                 return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"General Error during template matching: {ex.Message}");
                Debug.WriteLine(ex.StackTrace);
                return null;
            }
        }

        /// <summary>
        /// Updates the template matching configuration
        /// </summary>
        public void UpdateConfiguration(double threshold, TemplateMatchModes matchMode)
        {
            _matchThreshold = threshold;
            _matchMode = matchMode;
             Debug.WriteLine($"Configuration updated: Threshold={_matchThreshold}, Mode={_matchMode}");
        }

        /// <summary>
        /// Converts a System.Drawing.Bitmap to an OpenCvSharp.Mat.
        /// </summary>
        /// <param name="bitmap">The input Bitmap.</param>
        /// <param name="preserveAlpha">If true and the bitmap is Format32bppArgb, returns a 4-channel Mat (BGRA). Otherwise, returns a 3-channel Mat (BGR).</param>
        /// <returns>An OpenCvSharp.Mat object, or null on failure.</returns>
        private Mat? BitmapToMat(Bitmap bitmap, bool preserveAlpha = false)
        {
            if (bitmap == null) return null;

            // Lock the bitmap data
            Rectangle rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            System.Drawing.Imaging.BitmapData? bmpData = null;
            Mat? mat = null;
            Mat? resultMat = null;

            try
            {
                // Determine the target Mat type based on PixelFormat and preserveAlpha flag
                MatType targetType;
                System.Drawing.Imaging.PixelFormat lockFormat;

                if (preserveAlpha && bitmap.PixelFormat == System.Drawing.Imaging.PixelFormat.Format32bppArgb)
                {
                    targetType = MatType.CV_8UC4; // BGRA
                    lockFormat = System.Drawing.Imaging.PixelFormat.Format32bppArgb;
                }
                else if (bitmap.PixelFormat == System.Drawing.Imaging.PixelFormat.Format32bppArgb ||
                         bitmap.PixelFormat == System.Drawing.Imaging.PixelFormat.Format32bppRgb)
                {
                    targetType = MatType.CV_8UC4; // Temporarily load as BGRA/BGRX
                    lockFormat = bitmap.PixelFormat;
                }
                else if (bitmap.PixelFormat == System.Drawing.Imaging.PixelFormat.Format24bppRgb)
                {
                    targetType = MatType.CV_8UC3; // BGR
                    lockFormat = System.Drawing.Imaging.PixelFormat.Format24bppRgb;
                }
                else
                {
                    // Unsupported format directly, needs conversion first
                    Debug.WriteLine($"BitmapToMat: Unsupported direct pixel format {bitmap.PixelFormat}. Converting...");
                    // Use PixelFormat.Format32bppArgb for conversion if preserveAlpha might be needed later,
                    // otherwise Format24bppRgb is fine. Let's use 32bppArgb as it's often safer for retaining info.
                    System.Drawing.Imaging.PixelFormat convertToFormat = System.Drawing.Imaging.PixelFormat.Format32bppArgb; // Use 32bppArgb for intermediate conversion
                     Debug.WriteLine($"Converting to: {convertToFormat}");


                    using (Bitmap convertedBitmap = new Bitmap(bitmap.Width, bitmap.Height, convertToFormat))
                    {
                        using (Graphics g = Graphics.FromImage(convertedBitmap))
                        {
                             // Ensure transparency is handled correctly if converting to ARGB
                            if (convertToFormat == System.Drawing.Imaging.PixelFormat.Format32bppArgb)
                            {
                                g.Clear(Color.Transparent); // Initialize with transparency
                            }
                            g.DrawImage(bitmap, 0, 0, bitmap.Width, bitmap.Height);
                        }
                        // Recursive call with the converted bitmap
                        return BitmapToMat(convertedBitmap, preserveAlpha);
                    }
                }

                // Lock the bitmap with the determined format
                bmpData = bitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, lockFormat);

                // Create Mat pointing to the locked bitmap data
                mat = Mat.FromPixelData(bitmap.Width, bitmap.Height, targetType, bmpData.Scan0, bmpData.Stride);

                // Clone the data or convert as needed
                if (targetType == MatType.CV_8UC4 && preserveAlpha)
                {
                    resultMat = mat.Clone(); // Clone the BGRA data
                }
                else if (targetType == MatType.CV_8UC3)
                {
                     resultMat = mat.Clone(); // Clone the BGR data
                }
                else if (targetType == MatType.CV_8UC4 && !preserveAlpha)
                {
                    // Loaded as 4-channel (BGRA or BGRX) but need 3-channel BGR
                    resultMat = new Mat();
                    // Use BGRA2BGR for both cases, as it extracts the first 3 channels
                    var conversionCode = ColorConversionCodes.BGRA2BGR;
                    Cv2.CvtColor(mat, resultMat, conversionCode);
                }
                else
                {
                     Debug.WriteLine($"BitmapToMat: Unexpected targetType/preserveAlpha combination. Type: {targetType}, PreserveAlpha: {preserveAlpha}");
                     // Should not happen based on logic above, but clone as fallback
                     resultMat = mat.Clone();
                }

                return resultMat; // Return the cloned/converted Mat
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in BitmapToMat: {ex.Message}");
                resultMat?.Dispose(); // Clean up potential partial result
                return null;
            }
            finally
            {
                // Ensure original Mat pointing to bitmap data is disposed
                mat?.Dispose();
                // Ensure bitmap bits are unlocked
                if (bmpData != null)
                {
                    try { bitmap.UnlockBits(bmpData); } catch { /* Ignore error during cleanup */ }
                }
            }
        }


        // --- Debugging Helper Methods ---
        public string GetLastLoadedTemplateInfo()
        {
            if (_currentChampionName == null || _currentTeam == null)
                return "No template loaded";

            string resourceFolder = _currentTeam == "CHAOS" ? "champions_altered_red" : "champions_altered_blue";
            // Normalize here too for building the path correctly
            string championFileName = ChampionNameHelper.Normalize(_currentChampionName);
            string resourceName = $"JungleTracker.Assets.Champions.{resourceFolder}.{championFileName}.png";
            string size = (_championTemplate != null && !_championTemplate.IsDisposed) ? $"{_championTemplate.Width}x{_championTemplate.Height}" : "N/A"; // Added IsDisposed check
            string maskStatus = (_championTemplateMask != null && !_championTemplateMask.IsDisposed && !_championTemplateMask.Empty()) ? "Yes" : "No"; // Added IsDisposed check

            return $"Template: {resourceName} (From: {_currentChampionName}), Size: {size}, Mask Present: {maskStatus}";
        }

        public System.Drawing.Size GetTemplateSize()
        {
            if (_championTemplate == null || _championTemplate.IsDisposed || _championTemplate.Empty())
                return System.Drawing.Size.Empty;

            return new System.Drawing.Size(_championTemplate.Width, _championTemplate.Height);
        }

        public double GetMatchingThreshold() => _matchThreshold;
        public TemplateMatchModes GetMatchingMode() => _matchMode;


        // --- IDisposable Implementation ---
        private void DisposeTemplateAndMask()
        {
            // Use ?. operator for safe disposal
            _championTemplate?.Dispose();
            _championTemplate = null; // Set to null after disposing
            _championTemplateMask?.Dispose();
            _championTemplateMask = null; // Set to null after disposing
             // Optionally clear name/team here too, or rely on SetChampionTemplate logic
             // _currentChampionName = null;
             // _currentTeam = null;
        }

        public void Dispose()
        {
            DisposeTemplateAndMask();
            // No other managed resources to dispose in this specific class example
            // Call GC.SuppressFinalize if you add a finalizer (~MinimapScannerService())
            // GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Explicitly disposes the current template and resets tracking variables.
        /// </summary>
        public void ClearCurrentTemplate()
        {
            Debug.WriteLine("[Scanner] Clearing current template and state explicitly.");
            DisposeTemplateAndMask(); // Disposes Mats and sets fields to null
            _currentChampionName = null;
            _currentTeam = null;
        }
    }
}