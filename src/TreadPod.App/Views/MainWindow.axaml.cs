using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using TreadPod.App.Platform;
using TreadPod.App.ViewModels;

namespace TreadPod.App.Views;

public partial class MainWindow : Window
{
    private bool _userDragged;

    public bool AllowProcessClose { get; set; }

    public MainWindow()
    {
        InitializeComponent();

        HeaderStrip.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _userDragged = true;
                BeginMoveDrag(e);
            }
        };

        MenuBackdrop.PointerPressed += (_, _) =>
        {
            (DataContext as MainViewModel)?.CloseMenuCommand.Execute(null);
        };

        Opened += (_, _) => KeepInsideScreen();

        SizeChanged += (_, e) =>
        {
            if (IsVisible && Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 0.5)
            {
                KeepInsideScreen();
            }
        };

        Closing += (_, e) =>
        {
            if (!AllowProcessClose)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    public void PositionNearTaskbar()
    {
        _userDragged = false;
        KeepInsideScreen();
        Dispatcher.UIThread.Post(KeepInsideScreen, DispatcherPriority.Render);
    }

    private void KeepInsideScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null || screen.WorkingArea.Width <= 0 || screen.WorkingArea.Height <= 0)
        {
            return;
        }

        var area = screen.WorkingArea;
        var scale = screen.Scaling > 0 ? screen.Scaling : 1.0;
        var marginDip = 12.0;
        MaxHeight = Math.Max(320.0, area.Height / scale - marginDip * 2.0);

        var widthDip = ClientSize.Width > 0 ? ClientSize.Width : (Bounds.Width > 0 ? Bounds.Width : Width);
        var heightDip = ClientSize.Height > 0 ? ClientSize.Height : (Bounds.Height > 0 ? Bounds.Height : 360.0);

        var widthPx = (int)Math.Round(widthDip * scale);
        var heightPx = (int)Math.Round(heightDip * scale);
        var marginPx = (int)Math.Round(marginDip * scale);

        Position = PopoverPositioning.ComputePosition(
            area,
            Position,
            widthPx,
            heightPx,
            marginPx,
            anchorBottomRight: !_userDragged);
    }
}
