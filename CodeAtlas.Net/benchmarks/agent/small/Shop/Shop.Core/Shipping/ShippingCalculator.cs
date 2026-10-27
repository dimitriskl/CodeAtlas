using Shop.Core.Orders;

namespace Shop.Core.Shipping;

public sealed class ShippingCalculator
{
    public decimal Calculate(Order order)
    {
        if (order.Total >= 50m)
        {
            return 0m;
        }

        return order.WeightKg <= 2m ? 4.90m : 4.90m + (order.WeightKg - 2m) * 1.20m;
    }
}
