namespace Shop.Core.Payments;

public sealed record PaymentResult(bool Succeeded, string Reference);
