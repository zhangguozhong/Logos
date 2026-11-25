using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace Logos.Decoder;

public partial class DecoderWindow : Window
{
    private readonly List<string> files = new();
    private string? keys, output;
    private CancellationTokenSource? active;
    private bool closeAfterCancel, outputJson;

    public DecoderWindow()
    {
        InitializeComponent();
        if (OperatingSystem.IsMacOS())
        {
            FontFamily = new Avalonia.Media.FontFamily("PingFang SC");
            Reveal.Content = "在 Finder 中显示";
        }
        PickFiles.Click += async (_, _) => await Handle(async () =>
        {
            var items = await StorageProvider.OpenFilePickerAsync(new() { Title = "选择回捞批次", AllowMultiple = true });
            AddFiles(items);
        });
        DropZone.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = active == null && e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        });
        DropZone.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (active == null) AddFiles(e.DataTransfer.TryGetFiles() ?? []);
            e.Handled = true;
        });
        PickKeys.Click += async (_, _) => await Handle(async () =>
        {
            var items = await StorageProvider.OpenFilePickerAsync(new() { Title = "选择导出密钥配置", FileTypeFilter = [new("JSON") { Patterns = ["*.json"] }] });
            if (items.FirstOrDefault()?.TryGetLocalPath() is string selected)
            {
                keys = selected;
                KeyName.Text = Path.GetFileName(keys);
                ToolTip.SetTip(KeyName, keys);
                ResetResult();
                Refresh();
            }
        });
        ClearFiles.Click += (_, _) =>
        {
            files.Clear();
            FileRows.Children.Clear();
            ResetResult();
            Refresh();
        };
        Save.Click += async (_, _) => await Handle(Export);
        CancelJob.Click += (_, _) => active?.Cancel();
        Preview.Click += async (_, _) =>
        {
            if (output != null) await new TimelineWindow { FilePath = output, JsonFormat = outputJson }.ShowDialog(this);
        };
        OpenResult.Click += async (_, _) => await Handle(() => OpenOutput(false));
        Reveal.Click += async (_, _) => await Handle(() => OpenOutput(true));
        Closing += (_, e) =>
        {
            if (active == null) return;
            e.Cancel = true;
            closeAfterCancel = true;
            active.Cancel();
        };
        Refresh();
    }

    private async Task Handle(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private void AddFiles(IEnumerable<IStorageItem> items)
    {
        bool added = false;
        foreach (var item in items)
        {
            if (item is not IStorageFile || item.TryGetLocalPath() is not string path) continue;
            if (files.Contains(path, StringComparer.Ordinal)) continue;
            if (files.Count == 512) { Status.Text = "每次最多选择 512 个批次。"; break; }
            if (!added) ResetResult();
            added = true;
            files.Add(path);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var name = new TextBlock { Text = Path.GetFileName(path), VerticalAlignment = VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(name, path);
            var remove = new Button { Content = "×", Padding = new Thickness(10, 2) };
            Avalonia.Automation.AutomationProperties.SetName(remove, "移除 " + name.Text);
            remove.Click += (_, _) => { files.Remove(path); FileRows.Children.Remove(row); ResetResult(); Refresh(); };
            row.Children.Add(name);
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            FileRows.Children.Add(row);
        }
        Refresh();
    }

    private void ResetResult()
    {
        output = null;
        Status.Text = "";
        Preview.IsVisible = OpenResult.IsVisible = Reveal.IsVisible = false;
    }

    private void Refresh()
    {
        var idle = active == null;
        FileCount.Text = $"待解密文件（{files.Count}）";
        DropZone.IsEnabled = FileRows.IsEnabled = PickKeys.IsEnabled = TextFormat.IsEnabled = JsonFormat.IsEnabled = idle;
        Save.IsEnabled = idle && keys != null && files.Count > 0;
        EmptyState.IsVisible = files.Count == 0;
        ClearFiles.IsVisible = files.Count > 0;
        BusyBar.IsVisible = BusyBar.IsIndeterminate = !idle;
        CancelJob.IsVisible = !idle;
    }

    private async Task Export()
    {
        if (keys == null || active != null || files.Count == 0) return;
        var extension = JsonFormat.IsChecked == true ? "jsonl" : "txt";
        var basename = files.Count == 1 ? Path.GetFileNameWithoutExtension(files[0]) : "日志时间线";
        var item = await StorageProvider.SaveFilePickerAsync(new()
        {
            Title = "保存解密后的时间线", SuggestedFileName = basename + "." + extension,
            DefaultExtension = extension, ShowOverwritePrompt = true
        });
        if (item?.TryGetLocalPath() is not string target) return;
        using var cancel = new CancellationTokenSource();
        active = cancel;
        ResetResult();
        Refresh();
        Status.Text = "正在校验并解密…";
        try
        {
            var inputs = files.ToArray();
            var selectedKeys = keys;
            int count = await Task.Run(() => ExportJob.Run(inputs, selectedKeys, target, extension == "txt" ? "text" : "jsonl", cancel.Token));
            output = target;
            outputJson = extension == "jsonl";
            Status.Text = $"已从 {inputs.Length} 个批次导出 {count} 条记录：{Path.GetFileName(target)}";
            Preview.IsVisible = OpenResult.IsVisible = Reveal.IsVisible = true;
        }
        catch (OperationCanceledException) { Status.Text = "已取消导出。"; }
        finally
        {
            active = null;
            Refresh();
            if (closeAfterCancel) Close();
        }
    }

    private Task OpenOutput(bool reveal)
    {
        if (output == null) return Task.CompletedTask;
        if (reveal && OperatingSystem.IsMacOS())
            Process.Start(new ProcessStartInfo("/usr/bin/open") { ArgumentList = { "-R", output } });
        else
            Process.Start(new ProcessStartInfo(reveal ? Path.GetDirectoryName(output)! : output) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}
