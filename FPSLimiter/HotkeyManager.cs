using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace FPSLimiter
{
    /// <summary>
    /// Registers system-wide (global) hotkeys using the Win32 RegisterHotKey API, piggy-backing
    /// on Playnite's own main window message loop via HwndSource. Hotkeys fire even while
    /// Playnite is minimized or not focused, and even while a game is in the foreground.
    /// </summary>
    public class HotkeyManager : IDisposable
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;

        // Arbitrary high offset so our hotkey ids don't collide with anything else RegisterHotKey
        // might be used for elsewhere in the host process.
        private const int HotkeyIdBase = 0xB000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private HwndSource hwndSource;
        private IntPtr windowHandle = IntPtr.Zero;
        private readonly Dictionary<int, HotkeyBinding> registeredHotkeys = new Dictionary<int, HotkeyBinding>();

        public event Action<HotkeyBinding> HotkeyPressed;

        public bool IsAttached => hwndSource != null;

        /// <summary>Hooks into the given window's message loop. Must be called once before UpdateBindings.</summary>
        public bool Attach(Window window)
        {
            if (hwndSource != null)
            {
                return true;
            }

            if (window == null)
            {
                return false;
            }

            try
            {
                windowHandle = new WindowInteropHelper(window).Handle;
                if (windowHandle == IntPtr.Zero)
                {
                    return false;
                }

                hwndSource = HwndSource.FromHwnd(windowHandle);
                if (hwndSource == null)
                {
                    return false;
                }

                hwndSource.AddHook(WndProc);
                return true;
            }
            catch (Exception e)
            {
                logger.Error(e, "FPS Limiter could not attach to the main window for hotkeys.");
                return false;
            }
        }

        /// <summary>Unregisters all current hotkeys and re-registers the given (enabled) bindings.</summary>
        public void UpdateBindings(IEnumerable<HotkeyBinding> bindings)
        {
            UnregisterAll();

            if (hwndSource == null || bindings == null)
            {
                return;
            }

            var id = HotkeyIdBase;
            foreach (var binding in bindings)
            {
                if (binding == null || !binding.Enabled || binding.Key == Key.None)
                {
                    continue;
                }

                uint vk;
                try
                {
                    vk = (uint)KeyInterop.VirtualKeyFromKey(binding.Key);
                }
                catch (Exception e)
                {
                    logger.Debug(e, $"FPS Limiter could not resolve virtual key for hotkey {binding.DisplayText}.");
                    continue;
                }

                var modifiers = ToWin32Modifiers(binding.Modifiers) | MOD_NOREPEAT;

                if (RegisterHotKey(windowHandle, id, modifiers, vk))
                {
                    registeredHotkeys[id] = binding;
                    id++;
                }
                else
                {
                    logger.Warn($"FPS Limiter could not register hotkey {binding.DisplayText}. It may already be in use by another application.");
                }
            }
        }

        private void UnregisterAll()
        {
            if (windowHandle != IntPtr.Zero)
            {
                foreach (var id in registeredHotkeys.Keys)
                {
                    UnregisterHotKey(windowHandle, id);
                }
            }

            registeredHotkeys.Clear();
        }

        private static uint ToWin32Modifiers(ModifierKeys modifiers)
        {
            uint result = 0;

            if (modifiers.HasFlag(ModifierKeys.Alt))
            {
                result |= MOD_ALT;
            }

            if (modifiers.HasFlag(ModifierKeys.Control))
            {
                result |= MOD_CONTROL;
            }

            if (modifiers.HasFlag(ModifierKeys.Shift))
            {
                result |= MOD_SHIFT;
            }

            if (modifiers.HasFlag(ModifierKeys.Windows))
            {
                result |= MOD_WIN;
            }

            return result;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                var id = wParam.ToInt32();
                if (registeredHotkeys.TryGetValue(id, out var binding))
                {
                    handled = true;

                    try
                    {
                        HotkeyPressed?.Invoke(binding);
                    }
                    catch (Exception e)
                    {
                        logger.Error(e, "FPS Limiter hotkey handler threw an exception.");
                    }
                }
            }

            return IntPtr.Zero;
        }

        public void Dispose()
        {
            UnregisterAll();

            if (hwndSource != null)
            {
                hwndSource.RemoveHook(WndProc);
                hwndSource = null;
            }
        }
    }
}
