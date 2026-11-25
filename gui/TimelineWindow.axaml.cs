using Avalonia.Controls;
using System.Globalization;

namespace Logos.Decoder;

public partial class TimelineWindow : Window
{
    public required string FilePath { private get; init; }
    public bool JsonFormat { private get; init; }
    private CancellationTokenSource? pending;
    private readonly CancellationTokenSource lifetime = new();
    private Task<Choices>? suggestions;
    private int page;

    private sealed class Choices
    {
        internal readonly SortedSet<string> Users = new(StringComparer.Ordinal);
        internal readonly SortedSet<string> Words = new(StringComparer.Ordinal);
        internal readonly SortedSet<string> Dates = new(StringComparer.Ordinal);


        internal static string Date(DateTimeOffset value) =>
            value.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        internal static Choices Collect(IEnumerable<TimelineRow> source)
        {
            var choices = new Choices();
            DateTimeOffset? last = null;
            foreach (var row in source)
            {
                if (choices.Users.Count < 128 && row.Uid.Length > 0) choices.Users.Add(row.Uid);
                if (choices.Words.Count < 64 && row.Event.Length > 0) choices.Words.Add(row.Event);
                if (choices.Words.Count < 64 && row.Message.Length > 0)
                {
                    string word = row.Message[..Math.Min(60, row.Message.Length)].Split('\n', 2)[0].TrimEnd('\r');
                    if (word.Length > 0) choices.Words.Add(word);
                }
                if (choices.Dates.Count < 64) choices.Dates.Add(Date(row.Utc));
                last = row.Utc;
            }
            if (last.HasValue) choices.Dates.Add(Date(last.Value));
            return choices;
        }
    }
    private TimelineFilter filter = new("", "", null, null);

    public TimelineWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            Heading.Text = Path.GetFileName(FilePath);
            await Load(true);
        };
        Closed += (_, _) => { lifetime.Cancel(); pending?.Cancel(); };
        AttachChoices(UidChoices, Uid, c => c.Users, r => r.Uid);
        AttachChoices(KeywordChoices, Keyword, c => c.Words, r => r.Event);
        AttachChoices(FromChoices, From, c => c.Dates, r => Choices.Date(r.Utc));
        AttachChoices(ToChoices, To, c => c.Dates, r => Choices.Date(r.Utc));
        FirstFault.Click += async (_, _) => { ClearFilters(); await Load(true); };
        All.Click += async (_, _) =>
        {
            int number = (Records.SelectedItem as TimelineRow)?.Number ?? 1;
            ClearFilters();
            page = (number - 1) / TimelineReader.PageSize;
            await Load(false, number);
        };
        Apply.Click += async (_, _) =>
        {
            try
            {
                var from = ParseDate(From.Text, false);
                var to = ParseDate(To.Text, true);
                if (from > to) throw new FormatException("起始日期晚于截止日期。");
                filter = new(Uid.Text ?? "", Keyword.Text ?? "", from, to);
            }
            catch (FormatException e) { Summary.Text = e.Message; return; }
            page = 0;
            await Load(false);
        };
        Prev.Click += async (_, _) => { page--; await Load(false); };
        Next.Click += async (_, _) => { page++; await Load(false); };
        Records.SelectionChanged += (_, _) =>
        {
            Detail.Text = Records.SelectedItem is TimelineRow r
                ? $"第 {r.Number} 条   {r.Utc:O}   {r.Level}\nUID：{r.Uid}    事件：{r.Event}\n{r.Message}\n{r.Stack}".TrimEnd() : "";
        };
    }

    private void AttachChoices(Button button, TextBox field, Func<Choices, IEnumerable<string>> values,
        Func<TimelineRow, string> selectedValue)
    {
        button.Click += async (_, _) =>
        {
            if (lifetime.IsCancellationRequested) return;
            var token = lifetime.Token;
            button.IsEnabled = false;
            try
            {
                // 首次点开才扫描完整导出，各输入框共用一次扫描；候选仅存于本窗口。
                suggestions ??= Task.Run(() => Choices.Collect(TimelineReader.Read(FilePath, JsonFormat, token)), token);
                var choices = await suggestions;
                if (token.IsCancellationRequested) return;
                var items = new List<MenuItem>();
                void Add(string title, string value)
                {
                    var item = new MenuItem { Header = title };
                    item.Click += (_, _) => field.Text = value;
                    items.Add(item);
                }
                Add("不限（清空）", "");
                if (Records.SelectedItem is TimelineRow row && selectedValue(row) is { Length: > 0 } current)
                    Add("使用选中记录：" + current, current);
                foreach (var value in values(choices)) Add(value, value);
                var menu = new ContextMenu { ItemsSource = items, MaxHeight = 320 };
                button.ContextMenu = menu;
                menu.Open(button);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception e) { suggestions = null; Summary.Text = "读取候选失败：" + e.Message; }
            finally { button.IsEnabled = true; }
        };
    }

    internal static DateTimeOffset? ParseDate(string? text, bool endOfDay)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var day))
            throw new FormatException("请输入 yyyy-MM-dd 格式的 UTC 日期。");
        return new DateTimeOffset(day.ToDateTime(endOfDay ? TimeOnly.MaxValue : TimeOnly.MinValue, DateTimeKind.Utc));
    }

    private void ClearFilters()
    {
        Uid.Text = Keyword.Text = From.Text = To.Text = "";
        filter = new("", "", null, null);
        page = 0;
    }

    private async Task Load(bool context, int focus = 0)
    {
        pending?.Cancel();
        using var cancel = new CancellationTokenSource();
        pending = cancel;
        Prev.IsEnabled = Next.IsEnabled = false;
        Records.ItemsSource = null;
        Summary.Text = "正在读取时间线…";
        var selectedFilter = filter;
        int offset = page * TimelineReader.PageSize;
        try
        {
            var (rows, selected) = await Task.Run(() =>
            {
                var source = TimelineReader.Read(FilePath, JsonFormat, cancel.Token);
                if (context) return TimelineReader.Context(source);
                return (source.Where(selectedFilter.Matches).Skip(offset).Take(TimelineReader.PageSize + 1).ToList(), focus);
            });
            if (cancel.IsCancellationRequested) return;
            bool more = !context && rows.Count > TimelineReader.PageSize;
            if (more) rows.RemoveAt(rows.Count - 1);
            Records.ItemsSource = rows;
            Records.SelectedItem = rows.FirstOrDefault(r => r.Number == selected) ?? rows.FirstOrDefault();
            if (Records.SelectedItem != null) Records.ScrollIntoView(Records.SelectedItem);
            Prev.IsEnabled = !context && page > 0;
            Next.IsEnabled = more;
            Summary.Text = context
                ? selected == 0 ? "未发现 Error 或 Fatal，可查看完整时间线。" : $"首个异常位于第 {selected} 条，显示前后各最多 5 条。"
                : $"第 {page + 1} 页 · {rows.Count} 条记录";
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception e) { if (!cancel.IsCancellationRequested) Summary.Text = "读取失败：" + e.Message; }
        finally { if (pending == cancel) pending = null; }
    }
}
