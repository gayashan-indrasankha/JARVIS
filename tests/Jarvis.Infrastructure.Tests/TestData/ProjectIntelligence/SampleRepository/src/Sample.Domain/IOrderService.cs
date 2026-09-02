namespace Sample.Domain;

public interface IOrderService
{
    Task<Order> CreateAsync(string description, CancellationToken cancellationToken);
}
