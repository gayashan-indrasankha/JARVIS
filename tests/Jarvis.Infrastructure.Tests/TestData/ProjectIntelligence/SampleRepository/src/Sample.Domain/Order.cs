namespace Sample.Domain;

public sealed class Order
{
    public int Id { get; set; }

    public required string Description { get; set; }
}
