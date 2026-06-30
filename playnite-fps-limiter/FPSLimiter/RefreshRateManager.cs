using Playnite.SDK;
using System;
using System.Collections.Generic;
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

        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        private static extern int ChangeDisplaySettingsEx(string lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

        private const int ENUM_CURRENT_SETTINGS = -1;
        private const int DM_DISPLAYFREQUENCY = 0x400000;
        private const int DISP_CHANGE_SUCCESSFUL = 0;
        private const uint CDS_UPDATEREGISTRY = 0x01;
        private const uint CDS_NORESET = 0x10000000;

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
        /// Sets the primary display refresh rate.
        /// </summary>
        /// <param name="hz">Target refresh rate in Hz.</param>
        /// <returns>True on success.</returns>
        public static bool SetRefreshRate(int hz)
        {
            var dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(dm);

            if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm))
            {
                logger.Error("RefreshRateManager: EnumDisplaySettings failed.");
                return false;
            }

            if (dm.dmDisplayFrequency == hz)
            {
                logger.Info($"RefreshRateManager: Already at {hz} Hz, nothing to do.");
                return true;
            }

            dm.dmDisplayFrequency = hz;
            dm.dmFields = DM_DISPLAYFREQUENCY;

            int result = ChangeDisplaySettingsEx(null, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
            if (result == DISP_CHANGE_SUCCESSFUL)
            {
                logger.Info($"RefreshRateManager: Switched to {hz} Hz.");
                return true;
            }

            logger.Error($"RefreshRateManager: ChangeDisplaySettingsEx returned {result} when trying {hz} Hz.");
            return false;
        }

        /// <summary>
        /// Returns the distinct refresh rates (Hz) the primary display supports at its current
        /// resolution and color depth.
        /// </summary>
        public static List<int> GetSupportedRefreshRates()
        {
            var rates = new List<int>();

            var current = new DEVMODE();
            current.dmSize = (short)Marshal.SizeOf(current);
            if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref current))
            {
                logger.Error("RefreshRateManager: EnumDisplaySettings (current mode) failed.");
                return rates;
            }

            var dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(dm);

            int modeNum = 0;
            while (EnumDisplaySettings(null, modeNum, ref dm))
            {
                if (dm.dmPelsWidth == current.dmPelsWidth &&
                    dm.dmPelsHeight == current.dmPelsHeight &&
                    dm.dmBitsPerPel == current.dmBitsPerPel &&
                    dm.dmDisplayFrequency > 1 &&
                    !rates.Contains(dm.dmDisplayFrequency))
                {
                    rates.Add(dm.dmDisplayFrequency);
                }

                modeNum++;
            }

            rates.Sort();
            return rates;
        }

        /// <summary>
        /// Finds the best display refresh rate that the FPS cap divides evenly into, so every
        /// rendered frame gets the same number of display refreshes (no judder on non-VRR
        /// displays). Tries 2x the cap first, then 3x, 4x, ... up to <paramref name="maxMultiplier"/>,
        /// and finally falls back to a 1x match (refresh rate == cap). Returns 0 if the display
        /// doesn't support any matching mode.
        /// </summary>
        public static int FindMatchingRefreshRate(int fps, int maxMultiplier = 4)
        {
            if (fps <= 0)
            {
                return 0;
            }

            var supported = GetSupportedRefreshRates();
            if (supported.Count == 0)
            {
                return 0;
            }

            for (int multiplier = 2; multiplier <= maxMultiplier; multiplier++)
            {
                int candidate = fps * multiplier;
                if (supported.Contains(candidate))
                {
                    return candidate;
                }
            }

            return supported.Contains(fps) ? fps : 0;
        }
    }
}
