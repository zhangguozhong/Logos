using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Logos.Decoder;

internal sealed record TimelineRow(int Number, DateTimeOffset Utc, string Level, string Uid, string Message, string Stack, string Event)
{
    public override string ToString() => $"{Number,5}   {Utc:yyyy-MM-dd HH:mm:ss.fff}   {Level}   {Uid}   {Message.ReplaceLineEndings(" ")}";
}

internal sealed record TimelineFilter(string Uid, string Word, DateTimeOffset? From, DateTimeOffset? To)
{
    public bool Matches(TimelineRow row) =>
        (Uid.Length == 0 || row.Uid == Uid) && !(row.Utc < From || row.Utc > To)
        && (Word.Length == 0 || row.Message.Contains(Word, StringComparison.OrdinalIgnoreCase)
            || row.Stack.Contains(Word, StringComparison.OrdinalIgnoreCase) || row.Event.Contains(Word, StringComparison.OrdinalIgnoreCase));
}

internal static class TimelineReader
{
    internal const int PageSize = 100;
    // 文本导出中的字符串遵循 JSON 转义；按完整字段匹配，避免把消息中的 uid= 当成归属。
    private const string Quoted = """(?:null|"(?:[^"\\]|\\.)*")""";
    private static readonly Regex Header = new(
        $@"^(?<utc>\S+) (?<level>\S+) install={Quoted} session={Quoted} instance={Quoted} visit={Quoted} uid=(?<uid>{Quoted}) seq=-?\d+ id={Quoted} event=(?<event>{Quoted}) (?<msg>{Quoted})$",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    internal static IEnumerable<TimelineRow> Read(string file, bool json, CancellationToken token)
    {
        using var reader = File.OpenText(file);
        int number = 0;
        string? line = reader.ReadLine();
        while (line != null)
        {
            token.ThrowIfCancellationRequested();
            number++;
            if (json)
            {
                using var document = JsonDocument.Parse(line);
                var row = document.RootElement;
                string Field(string key) => row.GetProperty(key).GetString() ?? "";
                yield return new(number, row.GetProperty("utc").GetDateTimeOffset(), Field("level"),
                    Field("diagnosticUserId"), Field("message"), Field("stack"), Field("eventTypeId"));
                line = reader.ReadLine();
            }
            else
            {
                var match = Header.Match(line);
                if (!match.Success) throw new InvalidDataException($"第 {number} 条记录的文本格式无效。");
                string Field(string key) => JsonSerializer.Deserialize<string>(match.Groups[key].Value) ?? "";
                line = reader.ReadLine();
                string stack = "";
                if (line?.StartsWith("    stack=", StringComparison.Ordinal) == true)
                {
                    stack = JsonSerializer.Deserialize<string>(line[10..]) ?? "";
                    line = reader.ReadLine();
                }
                yield return new(number, DateTimeOffset.Parse(match.Groups["utc"].Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal), match.Groups["level"].Value, Field("uid"), Field("msg"), stack, Field("event"));
            }
        }
    }

    internal static (List<TimelineRow> Rows, int Focus) Context(IEnumerable<TimelineRow> source)
    {
        var rows = new List<TimelineRow>();
        int focus = 0;
        foreach (var row in source)
        {
            if (focus == 0 && row.Level is "Error" or "Fatal") focus = row.Number;
            rows.Add(row);
            if (focus == 0 && rows.Count > 5) rows.RemoveAt(0);
            if (focus > 0 && row.Number >= focus + 5) break;
        }
        if (focus == 0) rows.Clear();
        return (rows, focus);
    }
}
