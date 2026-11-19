using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using ChatPulse.Desktop.Services;

namespace ChatPulse.Desktop.Views.Dialogs;

public partial class InstagramBatchProgressDialog : Window
{
    private readonly CancellationTokenSource _cancellation;
    private readonly bool _chooseDirections;
    private bool _started;
    private bool _recenterQueued;
    private const double MAX_DIALOG_HEIGHT = 710;
    private const double OWNER_VERTICAL_PADDING = 32;

    public InstagramBatchProgressDialog(CancellationTokenSource cancellation,
        bool chooseDirections = true, InstagramCheckDirections predetermined = InstagramCheckDirections.BOTH,
        string? individualUserName = null)
    {
        _chooseDirections = chooseDirections;
        SelectedDirections = predetermined;
        _cancellation = cancellation;
        InitializeComponent();
        Model = new InstagramBatchProgressModel();
        DataContext = Model;
        if (individualUserName is not null)
        {
            Width = 420;
            BatchContent.Visibility = Visibility.Collapsed;
            IndividualContent.Visibility = Visibility.Visible;
            IndividualUserName.Text = $"@{individualUserName}";
            IndividualDirection.Text = predetermined == InstagramCheckDirections.FOLLOWERS
                ? "Подписана на меня"
                : "Я подписана";
        }
        Loaded += (_, _) => QueueRecenter();
        SizeChanged += (_, _) => QueueRecenter();
        if (!_chooseDirections)
        {
            DirectionSelectionPanel.Visibility = Visibility.Collapsed;
            Loaded += (_, _) => BeginCheck();
        }
    }

    public InstagramBatchProgressModel Model { get; }
    public InstagramCheckDirections SelectedDirections { get; private set; }
    public event EventHandler? StartRequested;

    private void QueueRecenter()
    {
        if (!IsLoaded || _recenterQueued) return;

        _recenterQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _recenterQueued = false;
            if (!IsVisible || Owner is not { IsVisible: true } owner) return;
            if (owner.ActualHeight <= 0 || owner.ActualWidth <= 0) return;

            // The dialog grows when the check starts or a result card appears.
            // CenterOwner only positions it once, before those content changes.
            MaxHeight = Math.Min(MAX_DIALOG_HEIGHT,
                Math.Max(280, owner.ActualHeight - OWNER_VERTICAL_PADDING));

            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            Left = owner.Left + (owner.ActualWidth - ActualWidth) / 2;
            Top = owner.Top + (owner.ActualHeight - ActualHeight) / 2;
        }));
    }

    private void DirectionCheckBox_OnChanged(object sender, RoutedEventArgs args)
    {
        if (StartCheckButton is null || CheckMyFollowingBox is null || CheckFollowersBox is null) return;
        StartCheckButton.IsEnabled = CheckMyFollowingBox.IsChecked == true || CheckFollowersBox.IsChecked == true;
    }

    private void StartCheckButton_OnClick(object sender, RoutedEventArgs args)
    {
        SelectedDirections = (CheckFollowersBox.IsChecked == true ? InstagramCheckDirections.FOLLOWERS : InstagramCheckDirections.NONE) |
                             (CheckMyFollowingBox.IsChecked == true ? InstagramCheckDirections.FOLLOWING : InstagramCheckDirections.NONE);
        BeginCheck();
    }

    private void CancelSelectionButton_OnClick(object sender, RoutedEventArgs args) => Close();

    private void BeginCheck()
    {
        if (_started || !InstagramCheckDirectionSelection.IsValid(SelectedDirections)) return;
        _started = true;
        DirectionSelectionPanel.Visibility = Visibility.Collapsed;
        ProgressContent.Visibility = Visibility.Visible;
        StartRequested?.Invoke(this, EventArgs.Empty);
    }

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
        Model.RequestStop();
        _cancellation.Cancel();
    }

    protected override void OnClosing(CancelEventArgs args)
    {
        if (_started && Model.IsRunning)
        {
            RequestStop();
            args.Cancel = true;
        }
        base.OnClosing(args);
    }
}
