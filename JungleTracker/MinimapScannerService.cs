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
        private const double DEFAULT_MATCH_THRESHOLD = 0.67; // Adjusted default for CCoeffNormed, REQUIRES TESTING!
        private const TemplateMatchModes DEFAULT_MATCH_MODE = TemplateMatchModes.CCoeffNormed;

        // Current matching configuration
        private double _matchThreshold;
        private TemplateMatchModes _matchMode;

        // Champion template (BGR) and its Alpha Mask (if available)
        private Mat _championTemplate;
        private Mat _championTemplateMask;
        private string _currentChampionName;
        private string _currentTeam;

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
            if (string.IsNullOrEmpty(championName)) return false;

            // If we already have this template loaded, don't reload
            if (championName == _currentChampionName && team == _currentTeam && _championTemplate != null)
                return true;

            // Clean up existing template and mask
            DisposeTemplateAndMask(); // Helper to clean up both

            try
            {
                // Special casing for Wukong 
                string championFileName = championName == "Wukong" ? "MonkeyKing" : championName;
                string resourceFolder = team == "CHAOS" ? "champions_altered_red" : "champions_altered_blue";
                string resourceName = $"JungleTracker.Assets.Champions.{resourceFolder}.{championFileName}.png";

                Assembly assembly = Assembly.GetExecutingAssembly();
                using (Stream? resourceStream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (resourceStream == null)
                    {
                        Debug.WriteLine($"Error: Resource stream not found for {resourceName}");
                        return false; // Template not found
                    }

                    using (Bitmap bitmap = new Bitmap(resourceStream))
                    {
                        // Load the template Mat, attempting to preserve the alpha channel
                        using (Mat templateMatWithPotentialAlpha = BitmapToMat(bitmap, preserveAlpha: true))
                        {
                            if (templateMatWithPotentialAlpha == null || templateMatWithPotentialAlpha.Empty())
                            {
                                Debug.WriteLine($"Error: Failed to convert bitmap to Mat for {resourceName}");
                                return false;
                            }

                            Debug.WriteLine($"Loaded template Mat for {resourceName}. Original size: {bitmap.Width}x{bitmap.Height}, Channels: {templateMatWithPotentialAlpha.Channels()}");

                            // Check if the loaded Mat has an Alpha channel (4 channels)
                            if (templateMatWithPotentialAlpha.Channels() == 4)
                            {
                                Debug.WriteLine("Template has 4 channels (BGRA). Extracting mask.");
                                // Split the BGRA channels
                                Mat[] channels = Cv2.Split(templateMatWithPotentialAlpha);
                                try
                                {
                                    // Last channel is alpha (index 3)
                                    _championTemplateMask = channels[3]; // Keep this channel

                                    // Merge the BGR channels (0, 1, 2) into the template
                                    _championTemplate = new Mat();
                                    Cv2.Merge(new[] { channels[0], channels[1], channels[2] }, _championTemplate);

                                    // Dispose the intermediate BGR channels now that they are merged
                                    channels[0].Dispose();
                                    channels[1].Dispose();
                                    channels[2].Dispose();
                                    Debug.WriteLine($"Successfully created BGR template ({_championTemplate.Width}x{_championTemplate.Height}) and Alpha mask ({_championTemplateMask.Width}x{_championTemplateMask.Height}).");
                                }
                                catch(Exception splitEx)
                                {
                                     Debug.WriteLine($"Error during channel split/merge: {splitEx.Message}");
                                     // Clean up if partially created
                                     DisposeTemplateAndMask();
                                     // Dispose any remaining channels from the split
                                     foreach (var channel in channels) channel?.Dispose();
                                     return false;
                                }
                            }
                            else if (templateMatWithPotentialAlpha.Channels() == 3)
                            {
                                Debug.WriteLine("Template has 3 channels (BGR). No mask will be used.");
                                // No alpha channel, just use the template as is (it's already BGR)
                                _championTemplate = templateMatWithPotentialAlpha.Clone();
                                _championTemplateMask = null; // Ensure mask is null
                            }
                            else
                            {
                                Debug.WriteLine($"Error: Template Mat has unexpected number of channels: {templateMatWithPotentialAlpha.Channels()}");
                                return false; // Unexpected channel count
                            }
                        } // Dispose templateMatWithPotentialAlpha here

                        _currentChampionName = championName;
                        _currentTeam = team;

                        Debug.WriteLine($"Champion template set: {championName} ({team}). Template Size: {GetTemplateSize()}. Mask Present: {_championTemplateMask != null && !_championTemplateMask.Empty()}");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading champion template {championName}: {ex.Message}");
                Debug.WriteLine(ex.StackTrace);
                DisposeTemplateAndMask();
                return false;
            }
        }

        /// <summary>
        /// Scans the minimap image for the currently set champion template.
        /// </summary>
        /// <returns>Point with center coordinates if found, null if not found or error.</returns>
        public System.Drawing.Point? ScanForChampion(Bitmap minimapImage)
        {
            // Check if a valid template is loaded
            if (_championTemplate == null || _championTemplate.Empty() || minimapImage == null)
            {
                Debug.WriteLine($"ScanForChampion failed: Template not loaded (_championTemplate is null/empty: {_championTemplate == null || _championTemplate.Empty()}) or minimap image is null (minimapImage is null: {minimapImage == null}).");
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
                        bool useMask = _championTemplateMask != null && !_championTemplateMask.Empty() && _championTemplateMask.Size() == _championTemplate.Size();
                        if (useMask)
                        {
                            Debug.WriteLine("Using alpha mask for template matching.");
                            Cv2.MatchTemplate(sourceMat, _championTemplate, result, _matchMode, _championTemplateMask);
                        }
                        else
                        {
                            if (_championTemplateMask != null && !_championTemplateMask.Empty() && _championTemplateMask.Size() != _championTemplate.Size())
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
            string championFileName = _currentChampionName == "Wukong" ? "MonkeyKing" : _currentChampionName;
            string resourceName = $"JungleTracker.Assets.Champions.{resourceFolder}.{championFileName}.png";
            string size = _championTemplate != null ? $"{_championTemplate.Width}x{_championTemplate.Height}" : "N/A";
            string maskStatus = (_championTemplateMask != null && !_championTemplateMask.Empty()) ? "Yes" : "No";

            return $"Template: {resourceName}, Size: {size}, Mask Present: {maskStatus}";
        }

        public System.Drawing.Size GetTemplateSize()
        {
            if (_championTemplate == null || _championTemplate.Empty())
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
            _championTemplate = null;
            _championTemplateMask?.Dispose();
            _championTemplateMask = null;
        }

        public void Dispose()
        {
            DisposeTemplateAndMask();
            // No other managed resources to dispose in this specific class example
            // Call GC.SuppressFinalize if you add a finalizer (~MinimapScannerService())
            // GC.SuppressFinalize(this);
        }
    }
}