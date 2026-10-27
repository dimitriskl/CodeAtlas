namespace Shop.Core.Orders;

public sealed class InMemoryOrderRepository : IOrderRepository
{
    private readonly Dictionary<Guid, Order> _orders = new();

    public IReadOnlyList<Order> GetOpenOrders(string customerId)
    {
        return _orders.Values.Where(order => order.IsOpen && order.CustomerId == customerId).ToList();
    }

    public Order? Find(Guid id)
    {
        return _orders.TryGetValue(id, out var order) ? order : null;
    }

    public void Save(Order order)
    {
        _orders[order.Id] = order;
    }
}
