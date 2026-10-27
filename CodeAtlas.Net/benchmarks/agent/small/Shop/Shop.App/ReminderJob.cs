using Shop.Core.Discounts;
using Shop.Core.Orders;

namespace Shop.App;

public sealed class ReminderJob(IOrderRepository orders, DiscountCalculator discounts)
{
    public IEnumerable<string> Run(string customerId)
    {
        foreach (var order in orders.GetOpenOrders(customerId))
        {
            yield return $"Order {order.Id}: pay now and get {discounts.Apply(order, 5m):0.00}";
        }
    }
}
