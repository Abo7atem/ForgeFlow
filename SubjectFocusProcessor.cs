using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VideoProcessing
{
    public sealed class SubjectFocusProcessor : IDisposable
    {
        private const int ModelSize = 512;
        private readonly InferenceSession _session;
        private readonly string _inputName;
        private readonly string _outputName;

        public SubjectFocusProcessor(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
                throw new FileNotFoundException("Subject Focus model not found.", modelPath);

            _session = new InferenceSession(modelPath);
            _inputName = _session.InputMetadata.Keys.First();
            _outputName = _session.OutputMetadata.Keys.First();
        }

        public void ProcessFrame(string inputPath, string outputPath, SubjectFocusMode mode, double strength)
        {
            float effectStrength = (float)Math.Clamp(strength, 0.0, 1.0);
            using var source = new Bitmap(inputPath);
            using var modelInput = ResizeForModel(source);
            float[] alpha = RunMatting(modelInput);

            var frame = BitmapBuffer.FromBitmap(source);
            byte[] foreground = mode is SubjectFocusMode.ProtectEdges or SubjectFocusMode.EnhanceSubject
                ? Sharpen(frame.Pixels, frame.Width, frame.Height)
                : frame.Pixels;
            byte[] background = mode switch
            {
                SubjectFocusMode.SoftenBackground => BoxBlur(frame.Pixels, frame.Width, frame.Height, radius: effectStrength >= 0.55f ? 2 : 1),
                SubjectFocusMode.EnhanceSubject => effectStrength >= 0.45f ? BoxBlur(frame.Pixels, frame.Width, frame.Height, radius: 1) : frame.Pixels,
                _ => frame.Pixels
            };

            byte[] output = Compose(frame.Pixels, foreground, background, alpha, frame.Width, frame.Height, effectStrength);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            BitmapBuffer.Save(outputPath, output, frame.Width, frame.Height);
        }

        private float[] RunMatting(Bitmap image)
        {
            var tensor = new DenseTensor<float>(new[] { 1, 3, ModelSize, ModelSize });
            for (int y = 0; y < ModelSize; y++)
            {
                for (int x = 0; x < ModelSize; x++)
                {
                    Color px = image.GetPixel(x, y);
                    tensor[0, 0, y, x] = Normalize(px.R);
                    tensor[0, 1, y, x] = Normalize(px.G);
                    tensor[0, 2, y, x] = Normalize(px.B);
                }
            }

            var input = NamedOnnxValue.CreateFromTensor(_inputName, tensor);
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(new[] { input });
            Tensor<float> output = results.First(v => v.Name == _outputName).AsTensor<float>();

            float[] alpha = new float[ModelSize * ModelSize];
            for (int y = 0; y < ModelSize; y++)
            {
                for (int x = 0; x < ModelSize; x++)
                {
                    float value = output[0, 0, y, x];
                    value = Math.Clamp((value - 0.03f) / 0.94f, 0f, 1f);
                    alpha[y * ModelSize + x] = SmoothStep(value);
                }
            }

            return alpha;
        }

        private static Bitmap ResizeForModel(Bitmap source)
        {
            var resized = new Bitmap(ModelSize, ModelSize, PixelFormat.Format24bppRgb);
            using Graphics graphics = Graphics.FromImage(resized);
            graphics.CompositingQuality = CompositingQuality.HighSpeed;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.HighSpeed;
            graphics.DrawImage(source, 0, 0, ModelSize, ModelSize);
            return resized;
        }

        private static byte[] Compose(byte[] original, byte[] foreground, byte[] background, float[] alpha, int width, int height, float strength)
        {
            byte[] output = new byte[original.Length];
            strength = Math.Clamp(strength, 0f, 1f);

            for (int y = 0; y < height; y++)
            {
                int maskY = Math.Clamp((int)((long)y * ModelSize / height), 0, ModelSize - 1);
                for (int x = 0; x < width; x++)
                {
                    int maskX = Math.Clamp((int)((long)x * ModelSize / width), 0, ModelSize - 1);
                    float a = alpha[maskY * ModelSize + maskX] * strength;
                    int idx = ((y * width) + x) * 4;
                    output[idx + 0] = Lerp(background[idx + 0], foreground[idx + 0], a);
                    output[idx + 1] = Lerp(background[idx + 1], foreground[idx + 1], a);
                    output[idx + 2] = Lerp(background[idx + 2], foreground[idx + 2], a);
                    output[idx + 3] = original[idx + 3];
                }
            }

            return output;
        }

        private static byte[] Sharpen(byte[] source, int width, int height)
        {
            byte[] output = new byte[source.Length];
            Array.Copy(source, output, source.Length);
            for (int y = 1; y < height - 1; y++)
            {
                for (int x = 1; x < width - 1; x++)
                {
                    int idx = ((y * width) + x) * 4;
                    for (int c = 0; c < 3; c++)
                    {
                        int value =
                            (5 * source[idx + c])
                            - source[(((y - 1) * width) + x) * 4 + c]
                            - source[(((y + 1) * width) + x) * 4 + c]
                            - source[((y * width) + x - 1) * 4 + c]
                            - source[((y * width) + x + 1) * 4 + c];
                        output[idx + c] = ClampByte(value);
                    }
                }
            }

            return output;
        }

        private static byte[] BoxBlur(byte[] source, int width, int height, int radius)
        {
            if (radius <= 0)
                return source;

            byte[] output = new byte[source.Length];
            int diameter = (radius * 2) + 1;
            int area = diameter * diameter;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int sumB = 0, sumG = 0, sumR = 0;
                    for (int ky = -radius; ky <= radius; ky++)
                    {
                        int sy = Math.Clamp(y + ky, 0, height - 1);
                        for (int kx = -radius; kx <= radius; kx++)
                        {
                            int sx = Math.Clamp(x + kx, 0, width - 1);
                            int sidx = ((sy * width) + sx) * 4;
                            sumB += source[sidx + 0];
                            sumG += source[sidx + 1];
                            sumR += source[sidx + 2];
                        }
                    }

                    int idx = ((y * width) + x) * 4;
                    output[idx + 0] = (byte)(sumB / area);
                    output[idx + 1] = (byte)(sumG / area);
                    output[idx + 2] = (byte)(sumR / area);
                    output[idx + 3] = source[idx + 3];
                }
            }

            return output;
        }

        private static byte Lerp(byte a, byte b, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return ClampByte(a + (int)Math.Round((b - a) * t));
        }

        private static byte ClampByte(int value) => (byte)Math.Clamp(value, 0, 255);

        private static float Normalize(byte value) => ((value / 255f) - 0.5f) / 0.5f;

        private static float SmoothStep(float value) => value * value * (3f - (2f * value));

        public void Dispose()
        {
            _session.Dispose();
        }

        private sealed class BitmapBuffer
        {
            public int Width { get; private init; }
            public int Height { get; private init; }
            public byte[] Pixels { get; private init; } = Array.Empty<byte>();

            public static BitmapBuffer FromBitmap(Bitmap source)
            {
                using var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
                using (Graphics graphics = Graphics.FromImage(bitmap))
                    graphics.DrawImage(source, 0, 0, source.Width, source.Height);

                Rectangle rect = new(0, 0, bitmap.Width, bitmap.Height);
                BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int length = Math.Abs(data.Stride) * data.Height;
                    byte[] raw = new byte[length];
                    Marshal.Copy(data.Scan0, raw, 0, length);
                    byte[] packed = new byte[bitmap.Width * bitmap.Height * 4];
                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        Buffer.BlockCopy(raw, y * data.Stride, packed, y * bitmap.Width * 4, bitmap.Width * 4);
                    }

                    return new BitmapBuffer { Width = bitmap.Width, Height = bitmap.Height, Pixels = packed };
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }
            }

            public static void Save(string path, byte[] pixels, int width, int height)
            {
                using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                Rectangle rect = new(0, 0, width, height);
                BitmapData data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    byte[] raw = new byte[Math.Abs(data.Stride) * height];
                    for (int y = 0; y < height; y++)
                    {
                        Buffer.BlockCopy(pixels, y * width * 4, raw, y * data.Stride, width * 4);
                    }

                    Marshal.Copy(raw, 0, data.Scan0, raw.Length);
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }

                bitmap.Save(path, ImageFormat.Png);
            }
        }
    }
}
