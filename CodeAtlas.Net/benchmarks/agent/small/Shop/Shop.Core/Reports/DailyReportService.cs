namespace Shop.Core.Reports;

public sealed class DailyReportService : IReportService
{
    private readonly List<(DateOnly Day, string Line)> _lines = new();

    public void Add(DateOnly day, string line)
    {
        _lines.Add((day, line));
    }

    public IReadOnlyList<string> GetOpenOrders(DateOnly day)
    {
        return _lines.Where(entry => entry.Day == day).Select(entry => entry.Line).ToList();
    }
}
