using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VideoProcessing
{
    public enum NanoVsrExecutionBackend
    {
        DirectML,
        TensorRT
    }

    /// <summary>
    /// Runs the official NanoVSR 4x model in overlapping temporal windows.
    /// DirectML keeps deployment self-contained and works with the customer's DX12 GPU driver.
    /// </summary>
    public sealed class NanoVsrProcessor : IDisposable
    {
        public const int TemporalWindow = 15;
        public const int ModelScale = 4;
        // The model's output tensor becomes very large at 4x. Keep one shared
        // limit for the UI preflight and the worker preflight so a selection
        // cannot pass one check and fail later inside ONNX Runtime.
        public const long MaxOutputPixels = 38_000_000L;
        private const int TemporalOverlap = 4;
        private const int TemporalStride = TemporalWindow - TemporalOverlap;
        private const int SpatialCoreSize = 512;
        private const int SpatialContext = 32;
        private const int MaxUntiledInputPixels = 576 * 576;

        private readonly record struct SpatialTile(
            int SourceX,
            int SourceY,
            int Width,
            int Height,
            int CoreX,
            int CoreY,
            int CoreWidth,
            int CoreHeight);

        private readonly InferenceSession _session;
        private readonly string _inputName;
        private readonly string _outputName;

        public NanoVsrProcessor(
            string modelPath,
            int deviceId = 0,
            NanoVsrExecutionBackend backend = NanoVsrExecutionBackend.DirectML,
            string? engineCachePath = null)
        {
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
                throw new FileNotFoundException("NanoVSR model not found.", modelPath);

            var options = new SessionOptions
            {
                EnableMemoryPattern = false,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };
            if (backend == NanoVsrExecutionBackend.TensorRT)
            {
                string cachePath = string.IsNullOrWhiteSpace(engineCachePath)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ForgeFlow", "TensorRTCache")
                    : engineCachePath;
                if (!TensorRtRuntime.TryAppendExecutionProvider(options, deviceId, cachePath, out string tensorRtMessage))
                    throw new InvalidOperationException(tensorRtMessage);
            }
            else
            {
                options.AppendExecutionProvider_DML(Math.Max(0, deviceId));
            }
            _session = new InferenceSession(modelPath, options);
            _inputName = _session.InputMetadata.Keys.First();
            _outputName = _session.OutputMetadata.Keys.First();
        }

        public Task ProcessDirectoryAsync(
            string inputDirectory,
            string outputDirectory,
            string extension,
            Action<int, int, double>? progress,
            CancellationToken cancellationToken,
            Action<string>? status = null)
        {
            return Task.Run(() => ProcessDirectory(inputDirectory, outputDirectory, extension, progress, status, cancellationToken), cancellationToken);
        }

        private void ProcessDirectory(
            string inputDirectory,
            string outputDirectory,
            string extension,
            Action<int, int, double>? progress,
            Action<string>? status,
            CancellationToken cancellationToken)
        {
            string normalizedExtension = "." + extension.Trim().TrimStart('.').ToLowerInvariant();
            string[] frames = Directory.EnumerateFiles(inputDirectory)
                .Where(path => string.Equals(Path.GetExtension(path), normalizedExtension, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (frames.Length == 0)
                throw new InvalidOperationException("NanoVSR did not receive any input frames.");

            Directory.CreateDirectory(outputDirectory);
            using var first = new Bitmap(frames[0]);
            int width = first.Width;
            int height = first.Height;
            if (width <= 0 || height <= 0)
                throw new InvalidOperationException("NanoVSR received an invalid frame size.");

            int outputWidth = checked(width * ModelScale);
            int outputHeight = checked(height * ModelScale);
            long outputPixels = (long)outputWidth * outputHeight;
            if (outputPixels > MaxOutputPixels)
                throw new InvalidOperationException($"NanoVSR x{ModelScale} output would be {outputWidth}x{outputHeight} ({outputPixels:N0} pixels), above the safe limit of {MaxOutputPixels:N0}. Choose a bundled x2 model or reduce the source resolution before processing.");

            var watch = Stopwatch.StartNew();
            int completed = 0;
            bool useSpatialTiling = (long)width * height > MaxUntiledInputPixels;
            IReadOnlyList<SpatialTile> spatialTiles = useSpatialTiling
                ? BuildSpatialTiles(width, height)
                : Array.Empty<SpatialTile>();
            status?.Invoke(useSpatialTiling
                ? $"NanoVSR spatial safety: {spatialTiles.Count} tiles per temporal window (512px core + 32px context)."
                : $"NanoVSR spatial safety: direct frame mode ({width}x{height}).");
            progress?.Invoke(0, frames.Length, 0);
            for (int start = 0; start < frames.Length; start += TemporalStride)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int actualCount = Math.Min(TemporalWindow, frames.Length - start);
                int keepFrom = start == 0 ? 0 : TemporalOverlap / 2;
                int keepThrough = start + TemporalWindow >= frames.Length
                    ? actualCount - 1
                    : TemporalWindow - (TemporalOverlap / 2) - 1;
                keepThrough = Math.Min(keepThrough, actualCount - 1);

                if (useSpatialTiling)
                {
                    completed += ProcessTiledWindow(
                        frames,
                        start,
                        actualCount,
                        keepFrom,
                        keepThrough,
                        width,
                        height,
                        outputWidth,
                        outputHeight,
                        spatialTiles,
                        outputDirectory,
                        completed,
                        watch,
                        progress,
                        cancellationToken);
                }
                else
                {
                    float[] inputData = LoadWindow(frames, start, actualCount, width, height, cancellationToken);
                    var tensor = new DenseTensor<float>(inputData, new[] { 1, TemporalWindow, 3, height, width });
                    var input = NamedOnnxValue.CreateFromTensor(_inputName, tensor);
                    using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(new[] { input });
                    Tensor<float> output = results.First(value => value.Name == _outputName).AsTensor<float>();

                    for (int localIndex = keepFrom; localIndex <= keepThrough; localIndex++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int globalIndex = start + localIndex;
                        if (globalIndex < 0 || globalIndex >= frames.Length)
                            continue;

                        string outputPath = Path.Combine(outputDirectory, Path.GetFileName(frames[globalIndex]));
                        SaveOutputFrame(output, localIndex, outputPath, outputWidth, outputHeight, cancellationToken);
                        completed++;
                        progress?.Invoke(completed, frames.Length, watch.Elapsed.TotalSeconds);
                    }
                }

                if (start + TemporalWindow >= frames.Length)
                    break;
            }

            if (completed != frames.Length)
                throw new InvalidOperationException($"NanoVSR produced {completed} of {frames.Length} frames.");
        }

        private int ProcessTiledWindow(
            string[] frames,
            int start,
            int actualCount,
            int keepFrom,
            int keepThrough,
            int inputWidth,
            int inputHeight,
            int outputWidth,
            int outputHeight,
            IReadOnlyList<SpatialTile> tiles,
            string outputDirectory,
            int completedBeforeWindow,
            Stopwatch watch,
            Action<int, int, double>? progress,
            CancellationToken cancellationToken)
        {
            Bitmap[] sources = LoadWindowBitmaps(frames, start, actualCount, inputWidth, inputHeight, cancellationToken);
            int outputCount = keepThrough - keepFrom + 1;
            var canvases = new Bitmap[outputCount];
            try
            {
                for (int index = 0; index < outputCount; index++)
                    canvases[index] = new Bitmap(outputWidth, outputHeight, PixelFormat.Format32bppArgb);

                foreach (SpatialTile tile in tiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    float[] inputData = LoadWindow(sources, tile, cancellationToken);
                    var tensor = new DenseTensor<float>(inputData, new[] { 1, TemporalWindow, 3, tile.Height, tile.Width });
                    var input = NamedOnnxValue.CreateFromTensor(_inputName, tensor);
                    using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(new[] { input });
                    Tensor<float> output = results.First(value => value.Name == _outputName).AsTensor<float>();

                    int tileOutputWidth = checked(tile.Width * ModelScale);
                    int tileOutputHeight = checked(tile.Height * ModelScale);
                    int sourceOffsetX = checked((tile.CoreX - tile.SourceX) * ModelScale);
                    int sourceOffsetY = checked((tile.CoreY - tile.SourceY) * ModelScale);
                    int coreOutputWidth = checked(tile.CoreWidth * ModelScale);
                    int coreOutputHeight = checked(tile.CoreHeight * ModelScale);

                    for (int localIndex = keepFrom; localIndex <= keepThrough; localIndex++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using Bitmap tileBitmap = CreateOutputFrameBitmap(
                            output,
                            localIndex,
                            tileOutputWidth,
                            tileOutputHeight,
                            cancellationToken);
                        using Graphics graphics = Graphics.FromImage(canvases[localIndex - keepFrom]);
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                        graphics.PixelOffsetMode = PixelOffsetMode.Half;
                        graphics.DrawImage(
                            tileBitmap,
                            new Rectangle(tile.CoreX * 4, tile.CoreY * 4, coreOutputWidth, coreOutputHeight),
                            new Rectangle(sourceOffsetX, sourceOffsetY, coreOutputWidth, coreOutputHeight),
                            GraphicsUnit.Pixel);
                    }
                }

                int produced = 0;
                for (int localIndex = keepFrom; localIndex <= keepThrough; localIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int globalIndex = start + localIndex;
                    if (globalIndex < 0 || globalIndex >= frames.Length)
                        continue;

                    string outputPath = Path.Combine(outputDirectory, Path.GetFileName(frames[globalIndex]));
                    canvases[localIndex - keepFrom].Save(outputPath, ImageFormat.Png);
                    produced++;
                    progress?.Invoke(completedBeforeWindow + produced, frames.Length, watch.Elapsed.TotalSeconds);
                }

                return produced;
            }
            finally
            {
                foreach (Bitmap? canvas in canvases)
                    canvas?.Dispose();
                foreach (Bitmap source in sources)
                    source.Dispose();
            }
        }

        private static IReadOnlyList<SpatialTile> BuildSpatialTiles(int width, int height)
        {
            var tiles = new List<SpatialTile>();
            for (int coreY = 0; coreY < height; coreY += SpatialCoreSize)
            {
                int coreHeight = Math.Min(SpatialCoreSize, height - coreY);
                int sourceY = Math.Max(0, coreY - SpatialContext);
                int sourceBottom = Math.Min(height, coreY + coreHeight + SpatialContext);
                for (int coreX = 0; coreX < width; coreX += SpatialCoreSize)
                {
                    int coreWidth = Math.Min(SpatialCoreSize, width - coreX);
                    int sourceX = Math.Max(0, coreX - SpatialContext);
                    int sourceRight = Math.Min(width, coreX + coreWidth + SpatialContext);
                    tiles.Add(new SpatialTile(
                        sourceX,
                        sourceY,
                        sourceRight - sourceX,
                        sourceBottom - sourceY,
                        coreX,
                        coreY,
                        coreWidth,
                        coreHeight));
                }
            }

            return tiles;
        }

        private static Bitmap[] LoadWindowBitmaps(
            string[] frames,
            int start,
            int actualCount,
            int width,
            int height,
            CancellationToken cancellationToken)
        {
            var sources = new Bitmap[TemporalWindow];
            try
            {
                for (int t = 0; t < TemporalWindow; t++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int sourceIndex = start + Math.Min(t, actualCount - 1);
                    sources[t] = new Bitmap(frames[sourceIndex]);
                    if (sources[t].Width != width || sources[t].Height != height)
                        throw new InvalidOperationException("NanoVSR requires every frame to have the same dimensions.");
                }
                return sources;
            }
            catch
            {
                foreach (Bitmap? source in sources)
                    source?.Dispose();
                throw;
            }
        }

        private static float[] LoadWindow(
            Bitmap[] sources,
            SpatialTile tile,
            CancellationToken cancellationToken)
        {
            int plane = checked(tile.Width * tile.Height);
            float[] data = new float[checked(TemporalWindow * 3 * plane)];
            for (int t = 0; t < TemporalWindow; t++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadBitmapRgb(
                    sources[t],
                    data,
                    t,
                    tile.Width,
                    tile.Height,
                    new Rectangle(tile.SourceX, tile.SourceY, tile.Width, tile.Height));
            }
            return data;
        }

        private static float[] LoadWindow(
            string[] frames,
            int start,
            int actualCount,
            int width,
            int height,
            CancellationToken cancellationToken)
        {
            int plane = checked(width * height);
            float[] data = new float[checked(TemporalWindow * 3 * plane)];
            for (int t = 0; t < TemporalWindow; t++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int sourceIndex = start + Math.Min(t, actualCount - 1);
                using var bitmap = new Bitmap(frames[sourceIndex]);
                if (bitmap.Width != width || bitmap.Height != height)
                    throw new InvalidOperationException("NanoVSR requires every frame to have the same dimensions.");

                ReadBitmapRgb(bitmap, data, t, width, height, new Rectangle(0, 0, width, height));
            }

            return data;
        }

        private static void ReadBitmapRgb(
            Bitmap source,
            float[] destination,
            int temporalIndex,
            int width,
            int height,
            Rectangle sourceRectangle)
        {
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.DrawImage(source, new Rectangle(0, 0, width, height), sourceRectangle, GraphicsUnit.Pixel);
            }

            Rectangle rectangle = new(0, 0, width, height);
            BitmapData bits = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rawLength = Math.Abs(bits.Stride) * height;
                byte[] raw = new byte[rawLength];
                Marshal.Copy(bits.Scan0, raw, 0, rawLength);
                int plane = width * height;
                int temporalBase = temporalIndex * 3 * plane;
                for (int y = 0; y < height; y++)
                {
                    int row = y * bits.Stride;
                    int pixelRow = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        int sourceOffset = row + (x * 4);
                        int pixel = pixelRow + x;
                        destination[temporalBase + pixel] = raw[sourceOffset + 2] / 255f;
                        destination[temporalBase + plane + pixel] = raw[sourceOffset + 1] / 255f;
                        destination[temporalBase + (2 * plane) + pixel] = raw[sourceOffset] / 255f;
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(bits);
            }
        }

        private static void SaveOutputFrame(
            Tensor<float> output,
            int temporalIndex,
            string outputPath,
            int width,
            int height,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            using Bitmap bitmap = CreateOutputFrameBitmap(output, temporalIndex, width, height, cancellationToken);
            bitmap.Save(outputPath, ImageFormat.Png);
        }

        private static Bitmap CreateOutputFrameBitmap(
            Tensor<float> output,
            int temporalIndex,
            int width,
            int height,
            CancellationToken cancellationToken)
        {
            int plane = checked(width * height);
            byte[] pixels = new byte[checked(plane * 4)];
            Parallel.For(0, height, new ParallelOptions { CancellationToken = cancellationToken }, y =>
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    int pixel = row + x;
                    int destinationOffset = pixel * 4;
                    pixels[destinationOffset] = ToByte(output[0, temporalIndex, 2, y, x]);
                    pixels[destinationOffset + 1] = ToByte(output[0, temporalIndex, 1, y, x]);
                    pixels[destinationOffset + 2] = ToByte(output[0, temporalIndex, 0, y, x]);
                    pixels[destinationOffset + 3] = 255;
                }
            });

            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            Rectangle rectangle = new(0, 0, width, height);
            BitmapData bits = bitmap.LockBits(rectangle, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                if (bits.Stride == width * 4)
                {
                    Marshal.Copy(pixels, 0, bits.Scan0, pixels.Length);
                }
                else
                {
                    for (int y = 0; y < height; y++)
                        Marshal.Copy(pixels, y * width * 4, bits.Scan0 + (y * bits.Stride), width * 4);
                }
            }
            finally
            {
                bitmap.UnlockBits(bits);
            }

            return bitmap;
        }

        private static byte ToByte(float value)
        {
            return (byte)Math.Clamp((int)Math.Round(Math.Clamp(value, 0f, 1f) * 255f), 0, 255);
        }

        public void Dispose()
        {
            _session.Dispose();
        }
    }
}
