using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Conduit.Emulator.GoogleWorkspace.Auth;
using Conduit.Emulator.GoogleWorkspace.Infrastructure;
using Conduit.Emulator.GoogleWorkspace.Models;
using Conduit.Emulator.GoogleWorkspace.Repositories;

namespace Conduit.Emulator.GoogleWorkspace.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = GoogleBearerDefaults.Scheme)]
[Route("admin/directory/v1/customer/{customer}/domains")]
public sealed class DomainsController : ControllerBase
{
    private readonly GwDomainRepository _domains;
    private readonly GwCustomerRepository _customers;

    public DomainsController(GwDomainRepository domains, GwCustomerRepository customers)
    {
        _domains = domains;
        _customers = customers;
    }

    [HttpGet]
    public async Task<IActionResult> List(string customer)
    {
        var customerId = await ResolveCustomerIdAsync(customer);
        var list = await _domains.ListAsync(customerId);
        return Ok(new GwDomainsList { Etag = EtagGenerator.New(), Domains = list.ToList() });
    }

    [HttpGet("{domainName}")]
    public async Task<IActionResult> Get(string customer, string domainName)
    {
        var customerId = await ResolveCustomerIdAsync(customer);
        var domain = await _domains.GetAsync(customerId, domainName);
        return domain is null
            ? GoogleErrorResult.NotFound("domainName", $"Resource Not Found: {domainName}")
            : Ok(domain);
    }

    private async Task<string> ResolveCustomerIdAsync(string customer)
    {
        if (customer == "my_customer")
            return (await _customers.GetDefaultAsync())?.Id ?? customer;
        return customer;
    }
}
