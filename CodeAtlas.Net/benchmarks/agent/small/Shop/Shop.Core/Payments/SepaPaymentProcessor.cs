namespace Shop.Core.Payments;

public sealed class SepaPaymentProcessor : BankPaymentProcessorBase
{
    public override string Name => "sepa";

    protected override string Scheme => "SEPA";
}
