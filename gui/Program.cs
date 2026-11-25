using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace Logos.Decoder;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // 后台解码进程沿用 CLI 的参数解析、认证及错误码，不创建窗口。
        if (args is ["--decode-worker", ..])
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            return Logos.Cli.Program.Main(args[1..]);
        }

        return AppBuilder.Configure<DecoderApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }
}

public sealed class DecoderApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new DecoderWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
