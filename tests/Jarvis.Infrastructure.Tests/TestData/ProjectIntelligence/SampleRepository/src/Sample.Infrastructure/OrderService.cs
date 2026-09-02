using Sample.Domain;

namespace Sample.Infrastructure;

public sealed class OrderService(AppDbContext database) : IOrderService
{
    public async Task<Order> CreateAsync(
        string description,
        CancellationToken cancellationToken)
    {
        Order order = new() { Description = description };
        database.Orders.Add(order);
        await database.SaveChangesAsync(cancellationToken);
        return order;
    }
}
