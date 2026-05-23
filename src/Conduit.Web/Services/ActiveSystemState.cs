using System;
using System.Threading.Tasks;
using Conduit.Core.Models;

namespace Conduit.Web.Services
{
    /// <summary>
    /// Scoped Blazor state holding the user's active Connected System selection
    /// across pages. The sidebar's system switcher writes here; the Users and
    /// Groups pages read here to decide which tenant filter to apply.
    ///
    /// Null means "All Connected Systems" — admin sees every tenant's data.
    /// Lives for the circuit, so a page navigation keeps the selection but a
    /// browser refresh resets to All. That's intentional: keep it ephemeral
    /// until we have a reason to persist.
    /// </summary>
    public class ActiveSystemState
    {
        private Tenant? _activeSystem;

        /// <summary>Currently active Connected System. Null = All.</summary>
        public Tenant? ActiveSystem => _activeSystem;

        /// <summary>Fires whenever <see cref="ActiveSystem"/> changes.</summary>
        public event Func<Task>? OnChangeAsync;

        public async Task SetAsync(Tenant? system)
        {
            // No-op if it's the same selection (compare by Id rather than ref).
            if (_activeSystem?.Id == system?.Id) return;
            _activeSystem = system;
            if (OnChangeAsync is not null) await OnChangeAsync.Invoke();
        }
    }
}
