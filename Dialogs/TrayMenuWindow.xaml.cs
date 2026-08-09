using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Forms = System.Windows.Forms;

namespace AppLauncher.Dialogs;

public partial class TrayMenuWindow : Window
{
    private readonly Action _showAction;
    private readonly Action _exitAction;
    private readonly Action _dismissAction;
    private bool _closing;

    public TrayMenuWindow(Action showAction, Action exitAction, Action dismissAction)
    {
        InitializeComponent();
        _showAction = showAction;
        _exitAction = exitAction;
        _dismissAction = dismissAction;
        Loaded += TrayMenuWindow_Loaded;
    }

    private void TrayMenuWindow_Loaded(object sender, RoutedEventArgs e)
    {
        PositionNearCursor();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(135))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        MenuTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(9, 0, TimeSpan.FromMilliseconds(165))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void PositionNearCursor()
    {
        var cursor = Forms.Cursor.Position;
        var area = Forms.Screen.FromPoint(cursor).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var areaLeft = area.Left / dpi.DpiScaleX;
        var areaTop = area.Top / dpi.DpiScaleY;
        var areaRight = area.Right / dpi.DpiScaleX;
        var areaBottom = area.Bottom / dpi.DpiScaleY;
        var cursorX = cursor.X / dpi.DpiScaleX;
        var cursorY = cursor.Y / dpi.DpiScaleY;

        Left = Math.Clamp(cursorX - ActualWidth + 18, areaLeft + 8, areaRight - ActualWidth - 8);
        Top = Math.Clamp(cursorY - ActualHeight - 8, areaTop + 8, areaBottom - ActualHeight - 8);
    }

    private void Show_Click(object sender, RoutedEventArgs e)
        => CloseAnimated(_showAction);

    private void Exit_Click(object sender, RoutedEventArgs e)
        => CloseAnimated(_exitAction);

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (!_closing)
            CloseAnimated(null);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseAnimated(null);
        }
    }

    private void CloseAnimated(Action? completedAction)
    {
        if (_closing)
            return;
        _closing = true;

        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(95));
        fade.Completed += (_, _) =>
        {
            Close();
            (completedAction ?? _dismissAction).Invoke();
        };
        BeginAnimation(OpacityProperty, fade);
        MenuTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(MenuTranslate.Y, 7, TimeSpan.FromMilliseconds(105)));
    }
}
