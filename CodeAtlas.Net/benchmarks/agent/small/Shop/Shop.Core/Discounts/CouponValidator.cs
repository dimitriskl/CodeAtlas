namespace Shop.Core.Discounts;

public sealed class CouponValidator
{
    public bool IsExpired(Coupon coupon, DateOnly today)
    {
        return coupon.ExpiresOn < today;
    }

    public bool HasValidCode(Coupon coupon)
    {
        return coupon.Code.Length == 8 && coupon.Code.All(char.IsLetterOrDigit);
    }
}
