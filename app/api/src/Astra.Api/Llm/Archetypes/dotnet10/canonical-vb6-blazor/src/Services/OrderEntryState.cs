// SPDX-Archetype: canonical-vb6-blazor
//
// Scoped form-level state. In VB6 these fields lived on the form
// class (Public mState, Private mOrderId). In Blazor Server the
// component class would re-instantiate on every render, so the
// form-level state moves to a Scoped service whose lifetime matches
// the user's circuit.

using System.ComponentModel.DataAnnotations;

namespace Demo.OrderEntry.Web.Services;

/// <summary>
/// Per-circuit (per-user) form state for the OrderEntry component.
/// Registered as Scoped in Program.cs.
/// </summary>
[SpecClaim("INV-1")]
public sealed class OrderEntryState
{
    [Required, StringLength(120)]
    public string CustomerName { get; set; } = "";

    public long OrderId { get; set; }

    public bool IsSubmitting { get; set; }

    public string Status { get; set; } = "";

    public IReadOnlyList<string> Products { get; set; } = [];
}
