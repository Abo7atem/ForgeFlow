using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ForgeFlow
{
    internal static class NativeWindowTheme
    {
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaBorderColor = 34;
        private const int DwmwaCaptionColor = 35;
        private const int DwmwaTextColor = 36;

        public static void Apply(Window window)
        {
            window.SourceInitialized += (_, _) => ApplyNow(window);
        }

        private static void ApplyNow(Window window)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                int darkMode = 1;
                int captionColor = ToColorRef(0x0A, 0x0A, 0x0A);
                int borderColor = ToColorRef(0xFF, 0x9F, 0x0A);
                int textColor = ToColorRef(0xF5, 0xF5, 0xF7);

                _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));
                _ = DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref captionColor, sizeof(int));
                _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref borderColor, sizeof(int));
                _ = DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref textColor, sizeof(int));
            }
            catch
            {
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

        private static int ToColorRef(byte red, byte green, byte blue) => red | (green << 8) | (blue << 16);
    }
}
