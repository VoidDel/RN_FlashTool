using Avalonia.Controls;
using Avalonia.Interactivity;
using Stm32Flash.App.ViewModels;

namespace Stm32Flash.App.Views;

public partial class AppUpdateWindow : Window
{
    public AppUpdateWindow()
    {
        InitializeComponent();
    }

    /// <summary>窗口显示后再联网检查，避免点开时界面先卡住。</summary>
    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (DataContext is AppUpdateViewModel viewModel)
        {
            await viewModel.InitializeAsync();
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
