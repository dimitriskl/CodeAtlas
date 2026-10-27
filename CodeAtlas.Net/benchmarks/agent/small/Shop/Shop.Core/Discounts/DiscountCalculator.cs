using Shop.Core.Orders;

namespace Shop.Core.Discounts;

public sealed class DiscountCalculator
{
    private readonly CouponValidator _validator = new();

    public decimal Apply(Order order, decimal percent)
    {
        return Math.Round(order.Total * (1 - percent / 100m), 2);
    }

    public decimal Apply(Order order, Coupon coupon)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (_validator.IsExpired(coupon, today) || !_validator.HasValidCode(coupon))
        {
            return order.Total;
        }

        return Apply(order, coupon.Percent);
    }
}
