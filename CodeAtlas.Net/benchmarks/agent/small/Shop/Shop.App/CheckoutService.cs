using Shop.Core.Discounts;
using Shop.Core.Orders;
using Shop.Core.Payments;
using Shop.Core.Shipping;

namespace Shop.App;

public sealed class CheckoutService(IPaymentProcessor processor, DiscountCalculator discounts, ShippingCalculator shipping)
{
    public PaymentResult Complete(Order order, Coupon? coupon)
    {
        var total = coupon is null ? order.Total : discounts.Apply(order, coupon);
        total += shipping.Calculate(order);
        return processor.Charge(order, total);
    }
}
