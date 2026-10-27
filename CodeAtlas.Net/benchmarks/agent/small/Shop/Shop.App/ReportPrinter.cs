using Shop.Core.Reports;

namespace Shop.App;

public sealed class ReportPrinter(IReportService reports)
{
    public void Print(DateOnly day, TextWriter output)
    {
        foreach (var line in reports.GetOpenOrders(day))
        {
            output.WriteLine(line);
        }
    }
}
