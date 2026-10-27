using Shop.Core.Orders;

namespace Shop.Core.Payments;

public interface IPaymentProcessor
{
    string Name { get; }

    PaymentResult Charge(Order order, decimal amount);
}
