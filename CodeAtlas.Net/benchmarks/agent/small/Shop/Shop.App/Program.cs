using Shop.App;
using Shop.Core.Discounts;
using Shop.Core.Orders;
using Shop.Core.Payments;
using Shop.Core.Reports;
using Shop.Core.Shipping;

var repository = new InMemoryOrderRepository();
var order = new Order(Guid.NewGuid(), "c-1", 42m, 3m, IsOpen: true);
repository.Save(order);

var discounts = new DiscountCalculator();
var checkout = new CheckoutService(new CardPaymentProcessor(), discounts, new ShippingCalculator());
var result = checkout.Complete(order, new Coupon("WELCOME1", 10m, new DateOnly(2030, 1, 1)));
Console.WriteLine(result.Reference);

Console.WriteLine(new CustomerStatementService(repository).Build("c-1"));
foreach (var line in new ReminderJob(repository, discounts).Run("c-1"))
{
    Console.WriteLine(line);
}

var reports = new DailyReportService();
reports.Add(DateOnly.FromDateTime(DateTime.UtcNow), "c-1 open");
new ReportPrinter(reports).Print(DateOnly.FromDateTime(DateTime.UtcNow), Console.Out);
