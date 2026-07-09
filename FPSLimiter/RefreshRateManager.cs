using Playnite.SDK;
using System;
using System.Runtime.InteropServices;

namespace FPSLimiter
{
    /// <summary>
    /// Changes and restores the primary display refresh rate using Win32 ChangeDisplaySettingsEx.
    /// </summary>
    public static class RefreshRateManager
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        // ── Win32 interop ────────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

        private const int ENUM_CURRENT_SETTINGS = -1;

        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>Returns the current refresh rate of the primary display, or 0 on failure.</summary>
        public static int GetCurrentRefreshRate()
        {
            var dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(dm);
            if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm))
            {
                return dm.dmDisplayFrequency;
            }

            return 0;
        }

        /// <summary>
        /// Blur Busters' recommended safe FPS cap for VRR (G-Sync/FreeSync) displays: comfortably
        /// under the panel's max refresh rate so frame delivery never outruns the VRR window, which
        /// is what causes stutter/frame-time spikes right at the refresh-rate ceiling.
        /// Formula: Refresh - (Refresh^2 / 3600).
        /// </summary>
        public static double GetVrrSafeCap(double refreshHz)
        {
            if (refreshHz <= 0)
            {
                return 0;
            }

            return refreshHz - (refreshHz * refreshHz) / 3600.0;
        }
    }
}
