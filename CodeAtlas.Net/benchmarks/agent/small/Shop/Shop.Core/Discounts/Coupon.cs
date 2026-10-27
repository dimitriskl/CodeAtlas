namespace Shop.Core.Discounts;

public sealed record Coupon(string Code, decimal Percent, DateOnly ExpiresOn);
