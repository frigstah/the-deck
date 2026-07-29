// Enabling WindowsForms for the notification-area icon brings System.Drawing and
// System.Windows.Forms into scope alongside WPF, and several type names exist in both. These
// aliases pick the WPF meaning once, so the rest of the app reads normally. Code that genuinely
// wants the System.Drawing type - TrayPresence, which draws its icon - qualifies it explicitly.

global using Application = System.Windows.Application;
global using Brush = System.Windows.Media.Brush;
global using Brushes = System.Windows.Media.Brushes;
global using Clipboard = System.Windows.Clipboard;
global using Color = System.Windows.Media.Color;
global using ColorConverter = System.Windows.Media.ColorConverter;
global using FontFamily = System.Windows.Media.FontFamily;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
global using MessageBox = System.Windows.MessageBox;
global using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
global using Pen = System.Windows.Media.Pen;
global using Point = System.Windows.Point;
global using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
global using Size = System.Windows.Size;
