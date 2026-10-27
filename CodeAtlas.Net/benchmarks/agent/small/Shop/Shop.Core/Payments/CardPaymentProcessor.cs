using Shop.Core.Orders;

namespace Shop.Core.Payments;

public sealed class CardPaymentProcessor : IPaymentProcessor
{
    public string Name => "card";

    public PaymentResult Charge(Order order, decimal amount)
    {
        return new PaymentResult(amount > 0, $"card-{order.Id:N}");
    }
}
