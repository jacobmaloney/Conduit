using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Core.SyncModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Xunit;

namespace Conduit.Connectors.EntraID.Tests;

/// <summary>
/// Slice 2 honesty fixes on the Entra license stream:
///   (1) AssignmentSource is DERIVED from licenseAssignmentStates (Group when the
///       grant came through group-based licensing, Unknown when Graph returned no
///       states) — never a hardcoded "Direct";
///   (2) a 403 on /subscribedSkus THROWS naming the missing scope, so the run goes
///       non-green instead of yielding nothing under a green banner.
/// The 403 test drives the real GraphServiceClient over a canned HTTP handler —
/// the models are POCOs but the throw path needs the actual Kiota error mapping.
/// </summary>
public class EntraLicenseSourceTests
{
    private static readonly string SkuA = "aaaaaaaa-1111-2222-3333-444444444444";
    private static readonly string SkuB = "bbbbbbbb-1111-2222-3333-444444444444";

    private static LicenseAssignmentState State(string skuId, string? assignedByGroup) => new()
    {
        SkuId = Guid.Parse(skuId),
        AssignedByGroup = assignedByGroup
    };

    // ─── (1) AssignmentSource derivation ─────────────────────────────────────

    [Fact]
    public void Null_states_collection_is_Unknown_not_a_fabricated_Direct() =>
        Assert.Equal("Unknown", EntraLicenseSource.DeriveAssignmentSource(null, SkuA));

    [Fact]
    public void Group_assigned_sku_reports_Group()
    {
        var states = new List<LicenseAssignmentState> { State(SkuA, assignedByGroup: "group-guid") };
        Assert.Equal("Group", EntraLicenseSource.DeriveAssignmentSource(states, SkuA));
    }

    [Fact]
    public void Directly_assigned_sku_reports_Direct()
    {
        var states = new List<LicenseAssignmentState> { State(SkuA, assignedByGroup: null) };
        Assert.Equal("Direct", EntraLicenseSource.DeriveAssignmentSource(states, SkuA));
    }

    [Fact]
    public void Direct_wins_when_the_same_sku_is_granted_both_ways()
    {
        // Both a group grant and a direct grant exist for one SKU: report Direct —
        // that is the grant an operator can actually act on.
        var states = new List<LicenseAssignmentState>
        {
            State(SkuA, assignedByGroup: "group-guid"),
            State(SkuA, assignedByGroup: null)
        };
        Assert.Equal("Direct", EntraLicenseSource.DeriveAssignmentSource(states, SkuA));
    }

    [Fact]
    public void A_sku_with_no_state_entry_is_Unknown_not_borrowed_from_another_sku()
    {
        // SkuB is group-assigned; SkuA has no state entry at all → no evidence for
        // SkuA, so Unknown — neither SkuB's Group nor a fabricated Direct.
        var states = new List<LicenseAssignmentState> { State(SkuB, assignedByGroup: "group-guid") };
        Assert.Equal("Unknown", EntraLicenseSource.DeriveAssignmentSource(states, SkuA));
    }

    [Fact]
    public void Sku_match_is_case_insensitive()
    {
        var states = new List<LicenseAssignmentState> { State(SkuA, assignedByGroup: "group-guid") };
        Assert.Equal("Group", EntraLicenseSource.DeriveAssignmentSource(states, SkuA.ToUpperInvariant()));
    }

    // ─── (2) 403 on /subscribedSkus throws naming the scope ─────────────────

    private sealed class CannedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public CannedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_respond(request));
    }

    private static HttpResponseMessage Forbidden() => new(HttpStatusCode.Forbidden)
    {
        Content = new StringContent(
            "{\"error\":{\"code\":\"Authorization_RequestDenied\",\"message\":\"Insufficient privileges to complete the operation.\"}}",
            Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task A_403_on_subscribedSkus_throws_naming_the_missing_scope()
    {
        var handler = new CannedHandler(req =>
            req.RequestUri!.AbsolutePath.Contains("subscribedSkus", StringComparison.OrdinalIgnoreCase)
                ? Forbidden()
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"value\":[]}", Encoding.UTF8, "application/json")
                });

        var client = new GraphServiceClient(new HttpClient(handler));
        var source = new EntraLicenseSource(client, NullLogger.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in source.ReadAsync(new SyncProjectScope(), CancellationToken.None)) { }
        });

        Assert.Contains("Organization.Read.All", ex.Message);
        Assert.Contains("subscribedSkus", ex.Message);
    }
}
