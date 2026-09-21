using Avalonia.Controls;
using Stm32Flash.App.ViewModels;

namespace Stm32Flash.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (ViewModel is { } viewModel)
        {
            await viewModel.InitializeAsync();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel?.Shutdown();
        base.OnClosed(e);
    }

    /// <summary>
    /// 日志追加后滚到底部，方便盯着 OpenOCD 的实时输出。
    /// Avalonia 的 TextBox 没有 ScrollToEnd，把光标移到末尾即可带动滚动。
    /// </summary>
    private void OnLogTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.CaretIndex = box.Text?.Length ?? 0;
        }
    }
}
