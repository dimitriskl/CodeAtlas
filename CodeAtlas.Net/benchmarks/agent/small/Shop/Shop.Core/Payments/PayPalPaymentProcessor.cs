using Shop.Core.Orders;

namespace Shop.Core.Payments;

public sealed class PayPalPaymentProcessor : IPaymentProcessor
{
    public string Name => "paypal";

    public PaymentResult Charge(Order order, decimal amount)
    {
        return new PaymentResult(amount > 0 && amount < 10_000m, $"pp-{order.Id:N}");
    }
}
