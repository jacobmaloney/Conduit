using System;

namespace Conduit.Core.SyncModels;

/// <summary>
/// Parses the step-boundary messages written by the sync orchestrator. Keeping
/// this in one place prevents the history page and run-detail API from drifting
/// away from the producer's canonical log format.
/// </summary>
public readonly record struct SyncRunStepMarker(string Name, string? StepType)
{
    public static bool TryParse(string? message, out SyncRunStepMarker marker)
    {
        marker = default;
        if (string.IsNullOrWhiteSpace(message)) return false;

        var trimmed = message.TrimStart();

        // Canonical orchestrator marker:
        //   Step '<name>' [<type>] starting (ordinal N).
        if (trimmed.StartsWith("Step '", StringComparison.Ordinal))
        {
            var nameOpen = trimmed.IndexOf('\'');
            var nameClose = trimmed.IndexOf('\'', nameOpen + 1);
            var typeOpen = nameClose >= 0 ? trimmed.IndexOf('[', nameClose + 1) : -1;
            var typeClose = typeOpen >= 0 ? trimmed.IndexOf(']', typeOpen + 1) : -1;

            if (nameClose > nameOpen && typeOpen > nameClose && typeClose > typeOpen)
            {
                var suffix = trimmed[(typeClose + 1)..].TrimStart();
                if (suffix.StartsWith("starting", StringComparison.Ordinal))
                {
                    marker = new SyncRunStepMarker(
                        trimmed[(nameOpen + 1)..nameClose],
                        trimmed[(typeOpen + 1)..typeClose].Trim());
                    return true;
                }
            }
        }

        // Compatibility with the short-lived legacy marker shape.
        const string legacyPrefix = "Step: ";
        if (!trimmed.StartsWith(legacyPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = trimmed[legacyPrefix.Length..].Trim();
        var legacyTypeOpen = rest.LastIndexOf('[');
        var legacyTypeClose = rest.LastIndexOf(']');
        if (legacyTypeOpen > 0 && legacyTypeClose > legacyTypeOpen && legacyTypeClose == rest.Length - 1)
        {
            marker = new SyncRunStepMarker(
                rest[..legacyTypeOpen].Trim(),
                rest[(legacyTypeOpen + 1)..legacyTypeClose].Trim());
            return true;
        }

        if (rest.Length == 0) return false;
        marker = new SyncRunStepMarker(rest, null);
        return true;
    }
}
