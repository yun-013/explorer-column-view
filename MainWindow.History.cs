using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ColumnView;

/// <summary>操作履歴まわり: 移動・削除直後の「元に戻す」通知と、履歴パネルの呼び出し。</summary>
public partial class MainWindow
{
    internal MainViewModel ViewModel => _vm;

    private HistoryEntry? _toastEntry;
    private DispatcherTimer? _toastTimer;

    /// <summary>移動・削除の直後に、何が起きたかを「元に戻す」付きで出す。</summary>
    private void ShowUndoToast(HistoryEntry entry, string message)
    {
        var seconds = AppSettings.Current.UndoToastSeconds;
        if (seconds <= 0)
            return;

        _toastEntry = entry;
        UndoToastIcon.Glyph = entry.Glyph;
        UndoToastText.Text = message;
        UndoToastText.ToolTip = message;
        UndoToastUndo.IsEnabled = true;

        UndoToast.BeginAnimation(OpacityProperty, null);
        UndoToast.Opacity = 0;
        UndoToast.Visibility = Visibility.Visible;
        UndoToast.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));

        _toastTimer ??= new DispatcherTimer();
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(seconds);
        _toastTimer.Tick -= ToastTimer_Tick;
        _toastTimer.Tick += ToastTimer_Tick;
        if (!UndoToast.IsMouseOver)
            _toastTimer.Start();
    }

    private void ToastTimer_Tick(object? sender, EventArgs e) => HideUndoToast();

    private void HideUndoToast()
    {
        _toastTimer?.Stop();
        _toastEntry = null;
        if (UndoToast.Visibility != Visibility.Visible)
            return;
        var fade = new DoubleAnimation(UndoToast.Opacity, 0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) =>
        {
            // フェード中に次の通知が出ていたら消さない
            if (_toastEntry is null)
                UndoToast.Visibility = Visibility.Collapsed;
        };
        UndoToast.BeginAnimation(OpacityProperty, fade);
    }

    // マウスを載せている間は消さない (読んでいる途中・押そうとしている途中で消えないように)
    private void UndoToast_MouseEnter(object sender, MouseEventArgs e) => _toastTimer?.Stop();

    private void UndoToast_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_toastEntry is not null)
            _toastTimer?.Start();
    }

    private void UndoToastClose_Click(object sender, RoutedEventArgs e) => HideUndoToast();

    private void UndoToastHistory_Click(object sender, RoutedEventArgs e)
    {
        var entry = _toastEntry;
        HideUndoToast();
        HistoryWindow.ShowFor(this, entry);
    }

    private async void UndoToastUndo_Click(object sender, RoutedEventArgs e)
    {
        if (_toastEntry is not { } entry)
            return;
        // この通知のあとに別の操作をしていたら、ここからは戻さずに履歴で確かめてもらう
        // (まとめて戻すと、後からした操作まで巻き込むため)
        if (!entry.IsDone || !ReferenceEquals(FileHistory.LatestDone, entry))
        {
            HideUndoToast();
            HistoryWindow.ShowFor(this, entry);
            return;
        }
        UndoToastUndo.IsEnabled = false;
        HideUndoToast();
        await _vm.UndoToAsync(entry);
    }

    private void History_Click(object sender, RoutedEventArgs e) => HistoryWindow.ShowFor(this, null);

    /// <summary>通知の対象を Ctrl+Z や履歴パネルで戻したら、通知は役目を終えたので下げる。</summary>
    private void OnHistoryChangedForToast()
    {
        if (_toastEntry is { IsDone: false } || (_toastEntry is { } e && !FileHistory.Entries.Contains(e)))
            HideUndoToast();
    }

    private void HookHistory()
    {
        _vm.HistoryNotice += ShowUndoToast;
        FileHistory.Changed += OnHistoryChangedForToast;
        Closed += (_, _) => FileHistory.Changed -= OnHistoryChangedForToast;
    }
}
