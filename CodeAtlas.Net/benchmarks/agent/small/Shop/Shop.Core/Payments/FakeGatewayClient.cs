using Shop.Core.Orders;

namespace Shop.Core.Payments;

// Test double for the external gateway. It is not a payment processor.
public sealed class FakeGatewayClient
{
    public List<decimal> Charges { get; } = new();

    public PaymentResult Charge(Order order, decimal amount)
    {
        Charges.Add(amount);
        return new PaymentResult(true, "fake");
    }
}
