namespace Shop.Core.Payments;

public sealed class WirePaymentProcessor : BankPaymentProcessorBase
{
    public override string Name => "wire";

    protected override string Scheme => "SWIFT";
}
