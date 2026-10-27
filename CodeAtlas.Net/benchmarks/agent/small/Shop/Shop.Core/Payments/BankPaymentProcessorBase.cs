using Shop.Core.Orders;

namespace Shop.Core.Payments;

public abstract class BankPaymentProcessorBase : IPaymentProcessor
{
    public abstract string Name { get; }

    protected abstract string Scheme { get; }

    public PaymentResult Charge(Order order, decimal amount)
    {
        return new PaymentResult(amount > 0, $"{Scheme}-{order.Id:N}");
    }
}
