using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Gomp.App.Services;
using Gomp.App.ViewModels;
using Gomp.App.Views;

namespace Gomp.App;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel(new LiveGompGatewayFactory(), new AvaloniaUiDispatcher());
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.ShutdownRequested += async (_, _) => await vm.DisposeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
