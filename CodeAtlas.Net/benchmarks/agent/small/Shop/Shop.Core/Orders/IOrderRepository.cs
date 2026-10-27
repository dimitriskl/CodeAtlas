namespace Shop.Core.Orders;

public interface IOrderRepository
{
    IReadOnlyList<Order> GetOpenOrders(string customerId);

    Order? Find(Guid id);

    void Save(Order order);
}
