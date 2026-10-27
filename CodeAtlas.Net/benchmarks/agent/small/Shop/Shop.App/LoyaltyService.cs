using Shop.Core.Discounts;
using Shop.Core.Orders;

namespace Shop.App;

public sealed class LoyaltyService(DiscountCalculator discounts)
{
    public decimal PriceForMember(Order order, Coupon memberCoupon)
    {
        return discounts.Apply(order, memberCoupon);
    }
}
