using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using ChatPulse.Desktop.Services;

namespace ChatPulse.Desktop.Views.Dialogs;

public partial class ParticipantListSyncProgressDialog : Window
{
    private readonly CancellationTokenSource _cancellation;
    private bool _started;
    private bool _recenterQueued;
    private const double MAX_DIALOG_HEIGHT = 600;
    private const double OWNER_VERTICAL_PADDING = 32;

    public ParticipantListSyncProgressDialog(CancellationTokenSource cancellation)
    {
        _cancellation = cancellation;
        InitializeComponent();
        Model = new ParticipantListSyncProgressModel();
        DataContext = Model;
        Loaded += (_, _) =>
        {
            QueueRecenter();
            if (_started) return;
            _started = true;
            StartRequested?.Invoke(this, EventArgs.Empty);
        };
        SizeChanged += (_, _) => QueueRecenter();
    }

    public ParticipantListSyncProgressModel Model { get; }
    public event EventHandler? StartRequested;

    private void ActionButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (!Model.IsRunning)
        {
            Close();
            return;
        }
        RequestStop();
    }

    private void RequestStop()
    {
        if (!Model.CanStop) return;
        Model.RequestStop();
        _cancellation.Cancel();
    }

    protected override void OnClosing(CancelEventArgs args)
    {
        if (Model.IsRunning)
        {
            RequestStop();
            args.Cancel = true;
        }
        base.OnClosing(args);
    }

    private void QueueRecenter()
    {
        if (!IsLoaded || _recenterQueued) return;
        _recenterQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _recenterQueued = false;
            if (!IsVisible || Owner is not { IsVisible: true } owner) return;
            MaxHeight = Math.Min(MAX_DIALOG_HEIGHT,
                Math.Max(270, owner.ActualHeight - OWNER_VERTICAL_PADDING));
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            Left = owner.Left + (owner.ActualWidth - ActualWidth) / 2;
            Top = owner.Top + (owner.ActualHeight - ActualHeight) / 2;
        }));
    }
}
