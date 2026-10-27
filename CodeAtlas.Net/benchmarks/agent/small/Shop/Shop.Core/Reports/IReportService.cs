namespace Shop.Core.Reports;

public interface IReportService
{
    IReadOnlyList<string> GetOpenOrders(DateOnly day);
}
