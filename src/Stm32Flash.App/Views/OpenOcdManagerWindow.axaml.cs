using Avalonia.Controls;
using Avalonia.Interactivity;
using Stm32Flash.App.ViewModels;

namespace Stm32Flash.App.Views;

public partial class OpenOcdManagerWindow : Window
{
    public OpenOcdManagerWindow()
    {
        InitializeComponent();
    }

    /// <summary>窗口显示后再扫描本机版本、拉取在线列表，避免点开“管理…”时界面先卡住。</summary>
    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (DataContext is OpenOcdManagerViewModel viewModel)
        {
            await viewModel.InitializeAsync();
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
