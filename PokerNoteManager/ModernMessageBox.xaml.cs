using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace PokerNoteManager
{
    public partial class ModernMessageBox : Window
    {
        public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

        private readonly MessageBoxButton _buttons;

        public ModernMessageBox(string message, string caption, MessageBoxButton buttons, MessageBoxImage icon)
        {
            InitializeComponent();

            _buttons = buttons;
            TxtTitle.Text = string.IsNullOrWhiteSpace(caption) ? "PokerVision HUD" : caption;
            TxtMessage.Text = message ?? string.Empty;

            ConfigureIcon(icon);
            ConfigureButtons(buttons, icon, caption, message);
        }

        private void ConfigureIcon(MessageBoxImage icon)
        {
            switch (icon)
            {
                case MessageBoxImage.Information: // Also Asterisk
                    IconBadge.Visibility = Visibility.Visible;
                    IconBadge.Background = new SolidColorBrush(Color.FromRgb(12, 74, 110)); // #0C4A6E
                    IconBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(2, 132, 199)); // #0284C7
                    IconPath.Fill = new SolidColorBrush(Color.FromRgb(56, 189, 248)); // #38BDF8
                    IconPath.Data = Geometry.Parse("M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2M11,16.5V11H13V16.5H11M11,9.5V7.5H13V9.5H11Z");
                    break;

                case MessageBoxImage.Warning: // Also Exclamation
                    IconBadge.Visibility = Visibility.Visible;
                    IconBadge.Background = new SolidColorBrush(Color.FromRgb(69, 26, 3)); // #451A03
                    IconBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(217, 119, 6)); // #D97706
                    IconPath.Fill = new SolidColorBrush(Color.FromRgb(251, 191, 36)); // #FBBF24
                    IconPath.Data = Geometry.Parse("M12,2L1,21H23L12,2M12,6L19.53,19H4.47L12,6M11,10V14H13V10H11M11,16V18H13V16H11Z");
                    break;

                case MessageBoxImage.Error: // Also Hand, Stop
                    IconBadge.Visibility = Visibility.Visible;
                    IconBadge.Background = new SolidColorBrush(Color.FromRgb(69, 10, 10)); // #450A0A
                    IconBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 38, 38)); // #DC2626
                    IconPath.Fill = new SolidColorBrush(Color.FromRgb(248, 113, 113)); // #F87171
                    IconPath.Data = Geometry.Parse("M12,2C17.53,2 22,6.47 22,12C22,17.53 17.53,22 12,22C6.47,22 2,17.53 2,12C2,6.47 6.47,2 12,2M15.59,7L12,10.59L8.41,7L7,8.41L10.59,12L7,15.59L8.41,17L12,13.41L15.59,17L17,15.59L13.41,12L17,8.41L15.59,7Z");
                    break;

                case MessageBoxImage.Question:
                    IconBadge.Visibility = Visibility.Visible;
                    IconBadge.Background = new SolidColorBrush(Color.FromRgb(30, 27, 75)); // #1E1B4B
                    IconBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(79, 70, 229)); // #4F46E5
                    IconPath.Fill = new SolidColorBrush(Color.FromRgb(129, 140, 248)); // #818CF8
                    IconPath.Data = Geometry.Parse("M12,2C17.52,2 22,6.48 22,12C22,17.52 17.52,22 12,22C6.48,22 2,17.52 2,12C2,6.48 6.48,2 12,2M12,18C12.55,18 13,17.55 13,17C13,16.45 12.55,16 12,16C11.45,16 11,16.45 11,17C11,17.55 11.45,18 12,18M12,6C9.79,6 8,7.79 8,10H10C10,8.9 10.9,8 12,8C13.1,8 14,8.9 14,10C14,12 11,11.75 11,15H13C13,12.75 16,12.5 16,10C16,7.79 14.21,6 12,6Z");
                    break;

                default:
                    IconBadge.Visibility = Visibility.Collapsed;
                    break;
            }
        }

        private void ConfigureButtons(MessageBoxButton buttons, MessageBoxImage icon, string? caption, string? message)
        {
            BtnOk.Visibility = Visibility.Collapsed;
            BtnCancel.Visibility = Visibility.Collapsed;
            BtnYes.Visibility = Visibility.Collapsed;
            BtnNo.Visibility = Visibility.Collapsed;

            bool isDestructive = (caption != null && (caption.Contains("Delete", StringComparison.OrdinalIgnoreCase) || caption.Contains("Reset", StringComparison.OrdinalIgnoreCase)))
                                 || (message != null && (message.Contains("delete", StringComparison.OrdinalIgnoreCase) || message.Contains("remove", StringComparison.OrdinalIgnoreCase) || message.Contains("reset", StringComparison.OrdinalIgnoreCase)));

            switch (buttons)
            {
                case MessageBoxButton.OK:
                    BtnOk.Visibility = Visibility.Visible;
                    BtnOk.IsDefault = true;
                    BtnOk.Focus();
                    break;

                case MessageBoxButton.OKCancel:
                    BtnOk.Visibility = Visibility.Visible;
                    BtnCancel.Visibility = Visibility.Visible;
                    BtnOk.IsDefault = true;
                    BtnCancel.IsCancel = true;
                    BtnOk.Focus();
                    break;

                case MessageBoxButton.YesNo:
                    BtnYes.Visibility = Visibility.Visible;
                    BtnNo.Visibility = Visibility.Visible;
                    if (isDestructive)
                    {
                        BtnYes.Style = (Style)FindResource("DangerDialogBtn");
                        BtnNo.IsDefault = true;
                        BtnNo.Focus();
                    }
                    else
                    {
                        BtnYes.IsDefault = true;
                        BtnYes.Focus();
                    }
                    break;

                case MessageBoxButton.YesNoCancel:
                    BtnYes.Visibility = Visibility.Visible;
                    BtnNo.Visibility = Visibility.Visible;
                    BtnCancel.Visibility = Visibility.Visible;
                    BtnCancel.IsCancel = true;
                    if (isDestructive)
                    {
                        BtnYes.Style = (Style)FindResource("DangerDialogBtn");
                        BtnNo.Focus();
                    }
                    else
                    {
                        BtnYes.IsDefault = true;
                        BtnYes.Focus();
                    }
                    break;
            }
        }

        private void Header_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            DismissDialog();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            Result = MessageBoxResult.OK;
            Close();
        }

        private void BtnYes_Click(object sender, RoutedEventArgs e)
        {
            Result = MessageBoxResult.Yes;
            Close();
        }

        private void BtnNo_Click(object sender, RoutedEventArgs e)
        {
            Result = MessageBoxResult.No;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Result = MessageBoxResult.Cancel;
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                DismissDialog();
            }
        }

        private void DismissDialog()
        {
            if (_buttons == MessageBoxButton.YesNo)
            {
                Result = MessageBoxResult.No;
            }
            else if (_buttons == MessageBoxButton.OKCancel || _buttons == MessageBoxButton.YesNoCancel)
            {
                Result = MessageBoxResult.Cancel;
            }
            else
            {
                Result = MessageBoxResult.OK;
            }
            Close();
        }

        /// <summary>
        /// Shows a custom dark-themed modern message box matching the PokerVision HUD aesthetic.
        /// </summary>
        public static MessageBoxResult Show(
            string messageBoxText,
            string caption = "PokerVision HUD",
            MessageBoxButton button = MessageBoxButton.OK,
            MessageBoxImage icon = MessageBoxImage.None,
            Window? owner = null)
        {
            var targetOwner = owner;
            if (targetOwner == null && Application.Current != null)
            {
                targetOwner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
                              ?? Application.Current.MainWindow;
            }

            var dialog = new ModernMessageBox(messageBoxText, caption, button, icon);

            if (targetOwner != null && targetOwner.IsVisible)
            {
                dialog.Owner = targetOwner;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            dialog.ShowDialog();
            return dialog.Result;
        }

        /// <summary>
        /// Owner-first overload matching WPF MessageBox.Show
        /// </summary>
        public static MessageBoxResult Show(
            Window owner,
            string messageBoxText,
            string caption = "PokerVision HUD",
            MessageBoxButton button = MessageBoxButton.OK,
            MessageBoxImage icon = MessageBoxImage.None)
        {
            return Show(messageBoxText, caption, button, icon, owner);
        }
    }
}
