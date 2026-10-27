namespace Shop.Core.Orders;

public sealed record Order(Guid Id, string CustomerId, decimal Total, decimal WeightKg, bool IsOpen);
