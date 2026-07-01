using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace FPSLimiter
{
    public partial class FPSLimiterSettingsView : UserControl
    {
        public FPSLimiterSettingsView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Captures the next key combo pressed while a hotkey box has focus and stores it on the
        /// HotkeyBinding bound to that row. Escape clears the assigned combo instead.
        /// </summary>
        private void HotkeyCaptureBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;

            if (!(sender is FrameworkElement element) || !(element.DataContext is HotkeyBinding binding))
            {
                return;
            }

            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            // Ignore bare modifier presses; wait for the actual key that completes the combo.
            if (key == Key.LeftCtrl || key == Key.RightCtrl ||
                key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LeftShift || key == Key.RightShift ||
                key == Key.LWin || key == Key.RWin ||
                key == Key.System || key == Key.None)
            {
                return;
            }

            if (key == Key.Escape)
            {
                binding.Key = Key.None;
                binding.Modifiers = ModifierKeys.None;
                return;
            }

            binding.Modifiers = Keyboard.Modifiers;
            binding.Key = key;
        }
    }
}