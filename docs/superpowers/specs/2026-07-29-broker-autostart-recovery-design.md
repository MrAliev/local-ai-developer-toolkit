# Broker Auto-start Recovery Design

## Problem

After a reboot or an unclean client shutdown, `host.json` can outlive the LocalAi
broker process. Windows may later reuse the recorded PID for a protected process.
The current health check probes that PID before checking the heartbeat and lets
`Win32Exception` escape. A normal client operation therefore fails instead of
starting the broker automatically.

## Desired behavior

- A missing, malformed, stale, mismatched, or inaccessible broker state is not
  healthy.
- A stale heartbeat is rejected before any operating-system process inspection.
- Process inspection failures do not escape from the health check.
- `EnsureRunningAsync` starts the broker once under the existing named semaphore
  and waits for a fresh matching state.
- Codex and Claude notify the user when recovery is started, but do not request
  permission for normal LocalAi broker startup.

## Design

`BrokerProcess.IsHealthy` will validate the schema and heartbeat first. Only a
fresh state may reach the injected process probe. The probe call will be wrapped
so `Win32Exception` means that process identity could not be confirmed and the
state is unhealthy. The existing startup semaphore remains the single-start
coordination mechanism, so no new service, scheduled task, or background watcher
is required.

User notification is an agent workflow rule rather than output from the broker
client. MCP stdout is reserved for JSON-RPC and must remain free of diagnostic
text.

## Verification

Regression tests will cover:

1. A stale state whose process probe would throw is discarded without probing.
2. A fresh state whose process probe throws is treated as unhealthy and triggers
   one successful replacement start.
3. Existing healthy reuse, bounded timeout, and concurrent-start behavior remain
   green.

After the focused tests pass, the complete solution will be tested. The fixed
artifacts will then be installed for Codex and Claude, followed by a CodeSearch
overlay synchronization for the Jira plugin.
