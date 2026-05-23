using Microsoft.AspNetCore.Mvc;

namespace Conduit.Emulator.GoogleWorkspace.Auth;

// Dev-only. Lets you GET /emulator/service-accounts/{email}.json and feed
// the download straight to GoogleCredential.FromFile on the client side.
[ApiController]
[Route("emulator/service-accounts")]
public sealed class KeysController : ControllerBase
{
    private readonly ServiceAccountStore _store;

    public KeysController(ServiceAccountStore store) => _store = store;

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var accounts = await _store.ListAsync();
        return Ok(accounts.Select(a => new
        {
            a.ClientEmail,
            a.ClientId,
            a.PrivateKeyId,
            a.ProjectId,
            a.Disabled,
            a.CreatedAt,
            downloadUrl = $"/emulator/service-accounts/{Uri.EscapeDataString(a.ClientEmail)}.json"
        }));
    }

    [HttpGet("{clientEmail}.json")]
    public async Task<IActionResult> Download(string clientEmail)
    {
        var account = await _store.GetByClientEmailAsync(clientEmail);
        if (account is null) return NotFound(new { error = "service_account_not_found" });

        var tokenUri = $"{Request.Scheme}://{Request.Host}/oauth2/v4/token";
        var json = ServiceAccountKeyFormatter.ToGoogleCredentialJson(account, tokenUri);

        Response.Headers["Content-Disposition"] = $"attachment; filename=\"{account.ClientEmail.Replace('@', '_')}.json\"";
        return Content(json, "application/json");
    }
}
