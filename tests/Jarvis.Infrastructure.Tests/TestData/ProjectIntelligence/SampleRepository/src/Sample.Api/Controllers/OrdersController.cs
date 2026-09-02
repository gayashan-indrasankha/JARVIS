using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sample.Domain;

namespace Sample.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/orders")]
public sealed class OrdersController(IOrderService orders) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<Order>> Create(
        string description,
        CancellationToken cancellationToken)
    {
        Order order = await orders.CreateAsync(description, cancellationToken);
        return Ok(order);
    }
}
