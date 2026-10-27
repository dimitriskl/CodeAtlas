using Shop.Core.Orders;

namespace Shop.App;

public sealed class CustomerStatementService(IOrderRepository orders)
{
    public string Build(string customerId)
    {
        var open = orders.GetOpenOrders(customerId);
        return $"{customerId}: {open.Count} open order(s), {open.Sum(order => order.Total):0.00} due";
    }
}
