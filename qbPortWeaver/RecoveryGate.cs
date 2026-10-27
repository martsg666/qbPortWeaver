namespace qbPortWeaver;

/// <summary>
/// Decides when the failed-cycle auto-recovery trigger may run, and runs it: the failure streak, the
/// sustained-failure floor, the offline rate limiter and the consecutive-recovery cap. Split out of
/// <see cref="PortSyncService"/>, which owns one instance and feeds it every failed cycle; the port-closed
/// trigger and the manual recovery test do not pass through here and share only the dispatch
/// (<see cref="PortSyncService.DispatchRecoveryAsync"/>). Serialised by MainForm._updateSemaphore, like
/// the rest of the sync cycle's state.
/// </summary>
/// <param name="uptime">The sync service's monotonic uptime clock, shared so the sustained-failure floor
/// is measured on the same clock as the rest of the cycle's timing.</param>
internal sealed class RecoveryGate(System.Diagnostics.Stopwatch uptime)
{
    /// <summary>The part of the cycle's configuration the gate reads: whether the failed-cycle trigger is
    /// on, its threshold, and the sync interval the sustained-failure floor is derived from.</summary>
    internal readonly record struct RecoverySettings(bool Enabled, int TriggerCycles, int UpdateIntervalSeconds);

    private const string RecoveryCapStateKey = "vpn.recoveryCapReached";

    // How many auto-recoveries may run back to back on the failed-cycle path without a single successful
    // port read in between. A backstop, not a tuning knob: if three service restarts have not produced a
    // port, the cause is not something a fourth will fix, and each one costs the user a torn-down tunnel.
    // Cleared by any successful port fetch, so a genuinely intermittent VPN is never permanently
    // suspended - only an unbroken run of futile recoveries is. Applies to the failed-cycle trigger
    // alone: the port-closed trigger runs after a successful fetch (which has already cleared the
    // count), and a recovery the user asked for by hand is never withheld.
    internal const int MaxConsecutiveRecoveries = 3;

    // How long recovery waits between attempts while connectivity cannot be confirmed. The first
    // attempt of a streak is not delayed at all; these are the waits before the second, third, and
    // every later attempt. Capped at the last entry, so recovery keeps retrying at a steady 15 minutes
    // for as long as the condition lasts rather than stopping - see TryTakeRecoverySlotAsync for why
    // never stopping is the point.
    private static readonly TimeSpan[] OfflineRetryBackoff =
    [
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(15),
    ];

    // Consecutive sync cycles in which the VPN was disconnected or port detection failed.
    private int _consecutiveFailedCycles;
    // Uptime reading (from uptime) at which the current failure streak began - re-stamped on every
    // 0 -> 1 transition of the counter, so it always describes the streak in progress. Auto-recovery
    // gates on this in addition to the cycle count so a burst of early wakes (network-change re-syncs
    // interrupting the inter-cycle delay) cannot fast-track a heavy recovery action during a
    // transient outage that would self-heal. Only meaningful while the counter is non-zero.
    // Monotonic rather than wall-clock: a VPN reconnect frequently triggers an NTP correction, so a
    // clock jump is *correlated* with the failure streak this gate measures. A backward jump would
    // hold recovery off indefinitely; a forward jump would fire it on the first failure, which is
    // precisely the transient-blip restart the floor exists to prevent.
    private TimeSpan _failureStreakStarted;
    // Auto-recoveries dispatched on the failed-cycle path since the last successful port read. Capped at
    // MaxConsecutiveRecoveries for the same reason PortSyncService caps its disconnect restarts: a remedy
    // that has not worked three times running is not addressing the cause, and repeating it on a timer
    // costs the user a torn-down tunnel and interrupted transfers each time. The other gates do not cover
    // this - the cycle count and sustained-failure floor both reset with the streak, and the offline
    // limiter only engages when the machine cannot reach the internet at all.
    private int _consecutiveRecoveries;

    // Rate-limiter state for recovery attempted while offline: how many attempts this streak has made
    // (0 = none, so the next one runs immediately) and the Environment.TickCount64 reading of the last
    // one. Reset when the connectivity probe next succeeds, or when a cycle fetches a port. The gate
    // these belong to runs only from TriggerRecoveryIfDueAsync.
    private int _offlineRecoveryAttempts;
    private long _lastOfflineRecoveryMs;
    // Latches while a wait window is being served, so the "holding recovery" explanation is logged once
    // per window instead of once per re-evaluated cycle.
    private bool _recoveryHoldLogged;

    // When the sustained-failure floor will clear, or null when it is holding nothing back. Published
    // for the Status panel: this is the second of the two independent holds, and the one that fires
    // during ordinary fast-cycling failure - the offline limiter needs a real outage as well. Set
    // where the gate is evaluated, because that is the only place _failureStreakStarted and the
    // configured cadence are both in scope. Absolute rather than remaining, for the same reason as
    // RecoveryHoldUntil: it is read a cycle later than it is computed.
    private DateTimeOffset? _recoverySustainedUntil;

    /// <summary>Consecutive failed cycles so far, 0 once a port is read. Published in the status file.</summary>
    internal int FailedCycles => _consecutiveFailedCycles;

    /// <summary>When the sustained-failure floor clears, while it is holding recovery back; null otherwise.</summary>
    internal DateTimeOffset? SustainedUntil => _recoverySustainedUntil;

    /// <summary>Whether the consecutive-recovery cap is suspending the trigger until a port is read.</summary>
    internal bool Suspended => _consecutiveRecoveries >= MaxConsecutiveRecoveries;

    // Builds a failure log message with cycle count and optional recovery trigger suffix
    // The suffix has to track the cap as well as the setting. While the cap is suspending recovery the
    // streak keeps climbing (deliberately - the failures are real), so a suffix that only knew about the
    // setting promised "recovery may trigger after N cycles" on every one of those cycles, for a recovery
    // that could not run. The log then contradicted both the suspension warning and the Status panel.
    internal string BuildCycleCountMessage(string prefix, int count, RecoverySettings cfg)
    {
        string cycles = TextFormat.PluralizeNoun(count, "failed cycle");
        string recoverySuffix;
        if (!cfg.Enabled)
            recoverySuffix = string.Empty;
        else if (_consecutiveRecoveries >= MaxConsecutiveRecoveries)
            recoverySuffix = $", auto-recovery is suspended after {MaxConsecutiveRecoveries} attempts that did not restore a port";
        else
            recoverySuffix = $", recovery may trigger after {cfg.TriggerCycles} consecutive {TextFormat.PluralizeNoun(cfg.TriggerCycles, "failed cycle")}";
        return $"{prefix} ({count} consecutive {cycles}{recoverySuffix})";
    }

    // Single increment site for _consecutiveFailedCycles. Every failure path that contributes
    // to the auto-recovery threshold flows through here: VPN disconnected, port detection
    // failed, and NAT-PMP adapter not found. Stamps the streak start time on the first failure
    // (for the recovery time gate), logs the cycle count message, then dispatches recovery
    // (which may no-op if the cycle-count or time threshold has not been reached).
    internal async Task RegisterFailureAndTryRecoveryAsync(
        string reason, LogLevel logLevel,
        string recoveryAction, string? recoveryTarget, string displayName,
        RecoverySettings cfg, CancellationToken cancellationToken)
    {
        if (_consecutiveFailedCycles == 0) _failureStreakStarted = uptime.Elapsed;
        _consecutiveFailedCycles++;
        int count = _consecutiveFailedCycles;
        LogManager.Instance.LogMessage(BuildCycleCountMessage(reason, count, cfg), logLevel);
        await TriggerRecoveryIfDueAsync(recoveryAction, recoveryTarget, displayName, cfg, cancellationToken).ConfigureAwait(false);
    }

    // Resets the failure streak counter. The start timestamp is deliberately left alone: it is
    // re-stamped on the next streak's first failure (see RegisterFailureAndTryRecoveryAsync),
    // and the time gate in TriggerRecoveryIfDueAsync only reads it while the counter is non-zero,
    // so a stale value can never be observed.
    internal void ResetFailureStreak()
    {
        _consecutiveFailedCycles = 0;
        _recoverySustainedUntil = null;   // the floor is measured from a streak that no longer exists
    }

    // Ends a run of consecutive recoveries. Called from the successful-port path and when auto-recovery
    // is switched off, and deliberately not from ResetFailureStreak: the run has to be broken by recovery
    // actually working, not merely by the failure counter being reset - the dispatch path resets that
    // streak every time it fires, so clearing the count there would mean the cap was never reached.
    internal void ResetRecoveryCap()
    {
        _consecutiveRecoveries = 0;
        LogManager.Instance.ClearLogState(RecoveryCapStateKey);
    }

    // Triggers auto-recovery if enabled and both gates are cleared: the failure cycle threshold
    // AND enough monotonic time has elapsed since the streak began. The time gate (derived from
    // the normal cycle cadence) prevents a burst of early wakes from fast-tracking recovery during
    // a transient outage. Resets the counter before the target check so the warning does not fire
    // every cycle when no recovery target is found.
    private async Task TriggerRecoveryIfDueAsync(string action, string? recoveryTarget, string displayName, RecoverySettings cfg, CancellationToken cancellationToken)
    {
        if (!cfg.Enabled)
        {
            ResetFailureStreak();
            // Switched off, so nothing can be accumulating: clear the cap too, matching the existing
            // rule that a disabled feature leaves no stale counters to surprise the user on re-enable.
            ResetRecoveryCap();
            return;
        }
        if (_consecutiveFailedCycles < cfg.TriggerCycles) return;

        // Sustained-failure floor: the elapsed time recovery would have taken under normal
        // scheduled cycling ((TriggerCycles - 1) intervals between the first and last failure).
        // A streak driven faster than this by early wakes is held until the time also clears.
        TimeSpan minSustainedFailure = TimeSpan.FromSeconds((cfg.TriggerCycles - 1) * cfg.UpdateIntervalSeconds);
        TimeSpan elapsed = uptime.Elapsed - _failureStreakStarted;
        if (elapsed < minSustainedFailure)
        {
            _recoverySustainedUntil = DateTimeOffset.Now.Add(minSustainedFailure - elapsed);
            LogManager.Instance.LogMessage(
                $"Holding recovery - failures started only {elapsed.TotalSeconds:F0}s ago " +
                $"(recovery waits until {minSustainedFailure.TotalSeconds:F0}s to ignore brief network blips)",
                // Info, while the offline limiter logs its hold at Warn, and the asymmetry is
                // deliberate rather than an oversight: both lines mean "recovery is not running right
                // now", but this floor clears by itself within a cycle or two and is reached during
                // any ordinary blip, whereas an offline hold lasts 5 to 15 minutes and means something
                // is actually wrong. Warn here would badge the tray on every transient failure.
                LogLevel.Info);
            return;
        }

        _recoverySustainedUntil = null;   // floor cleared: it is no longer holding anything back
        int count = _consecutiveFailedCycles;

        if (recoveryTarget is null)
        {
            ResetFailureStreak();
            LogManager.Instance.LogMessage($"No recovery target found for '{displayName}' - skipping recovery", LogLevel.Warn);
            return;
        }

        // Rate-limits recovery while the machine cannot reach the internet. Deliberately gates this
        // trigger only: the port-closed trigger runs after a successful port fetch, so that path has
        // already proven it has connectivity and the probe there could only ever be wrong (a machine
        // that filters ICMP would be told its internet is down moments after a clean sync).
        //
        // Placed above ResetFailureStreak so a held attempt leaves the streak hot. The next attempt is
        // then governed purely by the backoff, rather than also having to rebuild the streak and clear
        // the sustained-failure floor again - which would make the real spacing longer than the 5, 10
        // and 15 minutes documented, by an amount that depends on the configured interval.
        var slot = await TryTakeRecoverySlotAsync(displayName, cancellationToken).ConfigureAwait(false);
        if (!slot.Allowed) return;

        // Backstop: stop dispatching once MaxConsecutiveRecoveries have run without a single successful
        // port read between them. Cleared by ResetRecoveryCap on the next successful port read.
        //
        // Evaluated *below* the connectivity slot, and gated on slot.Online, because the cap is a hard
        // stop and the limiter above exists precisely to keep hard stops away from an offline machine:
        // a killswitch blocks the probe itself, so a stuck VPN and a dead upstream are indistinguishable,
        // and refusing to retry strands the machine with the killswitch up and no way out. The cap's
        // premise is "three attempts proved this is not the fix", which only holds where the attempts
        // could have worked - so the block and the increment must be governed by the same test, taken at
        // the same point. Placing this check above the probe (as it first was) exempted the increment
        // while leaving the block, which reinstated exactly that deadlock after three online failures.
        //
        // Not cleared when offline either: a machine flapping in and out of connectivity would reset its
        // way past the cap forever, which is the unbounded restart loop this was added to stop.
        //
        // The streak is deliberately left running rather than reset - the failures really are
        // continuing, and the count is what the log line and the Status panel report. LogStateChange
        // keeps the message to one line however many cycles return here.
        if (slot.Online && _consecutiveRecoveries >= MaxConsecutiveRecoveries)
        {
            LogManager.Instance.LogStateChange(RecoveryCapStateKey,
                $"Suspending auto-recovery for '{displayName}' - {MaxConsecutiveRecoveries} consecutive recoveries " +
                "did not restore a forwarded port, so repeating it would only keep interrupting the connection. " +
                "Recovery resumes automatically once a port is read successfully.",
                LogLevel.Warn);
            return;
        }

        ResetFailureStreak();
        // Counted here, past every gate, so it records recoveries that are actually dispatched. Counting
        // at the trigger sites instead would let held attempts consume the cap without a restart running.
        //
        // Online attempts only, and that exemption is load-bearing rather than a nicety. The cap is a
        // hard stop, and the connectivity limiter exists precisely because a hard stop is wrong while the
        // machine cannot reach the internet: a killswitch blocks the probe itself, so a stuck VPN and a
        // dead upstream are indistinguishable, and refusing to retry leaves the killswitch up with no way
        // out. Counting offline attempts would put that deadlock back after three tries. Offline
        // recoveries are already bounded, by the 5/10/15 minute backoff, so they need no second bound.
        if (slot.Online) _consecutiveRecoveries++;

        await PortSyncService.DispatchRecoveryAsync(action, recoveryTarget, displayName, cancellationToken,
            triggerLogMessage: $"Triggering '{action}' for '{displayName}' after {count} consecutive failed {TextFormat.PluralizeNoun(count, "cycle")}").ConfigureAwait(false);
    }

    // When the offline rate limiter will allow the next recovery attempt, or null when
    // nothing is being held back. Read once per cycle for the status file so the Status panel can say
    // "Holding - cannot confirm internet, retry in ~15m" - without it the user sees a disconnected VPN and no sign that
    // the app is deliberately waiting rather than idle, which is the whole point of the limiter.
    // Computed rather than stored: the deadline is the last attempt plus its backoff step, and both
    // are already tracked. Returns null once the wait has elapsed - at that point the next due cycle
    // will attempt recovery, so there is nothing left to count down.
    internal DateTimeOffset? GetRecoveryHoldUntil()
    {
        if (_offlineRecoveryAttempts <= 0) return null;
        TimeSpan required = OfflineRetryBackoff[Math.Min(_offlineRecoveryAttempts - 1, OfflineRetryBackoff.Length - 1)];
        TimeSpan waited = TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastOfflineRecoveryMs);
        TimeSpan remaining = required - waited;
        return remaining > TimeSpan.Zero ? DateTimeOffset.Now.Add(remaining) : null;
    }

    // Clears the offline rate-limiter so the next offline streak starts with an immediate attempt.
    // Called from both ends: the probe succeeding here, and a successful port fetch in the sync cycle.
    // Both are needed - once the VPN is healthy again recovery is never dispatched, so the probe branch
    // alone would leave a stale count behind and needlessly delay the first attempt of a later outage.
    internal void ResetOfflineRecoveryBackoff()
    {
        _offlineRecoveryAttempts = 0;
        _lastOfflineRecoveryMs = 0;
        _recoveryHoldLogged = false;
    }

    // Decides whether an automatic recovery may run now, given whether the machine can reach the
    // internet. The probe rate-limits recovery, it never vetoes it outright, and that distinction is
    // load-bearing: a VPN killswitch blocks the probe itself while the tunnel is down, so a hard veto
    // would suppress recovery exactly when a restart is the fix, leaving the killswitch up, the probe
    // failing, and the machine deadlocked with no way out. Backing off instead bounds the damage of a
    // real outage while guaranteeing a stuck VPN still gets retried.
    //
    // Online: recover immediately, and reset the backoff.
    // Offline: the first recovery of a streak still runs (it is the one most likely to help), then
    // successive attempts wait OfflineRetryBackoff - 5, 10, then 15 minutes for every attempt after.
    private async Task<(bool Allowed, bool Online)> TryTakeRecoverySlotAsync(string displayName, CancellationToken cancellationToken)
    {
        if (await InternetConnectivityProbe.IsInternetReachableAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_offlineRecoveryAttempts > 0)
                LogManager.Instance.LogMessage("Internet connection confirmed - recovery resumed at the normal rate", LogLevel.Info);
            ResetOfflineRecoveryBackoff();
            return (true, Online: true);
        }

        // Monotonic, which is what matters here: a machine coming back from an outage often corrects
        // its clock by NTP, and a backward jump must not unblock every held attempt at once. The
        // shared uptime stopwatch is equally monotonic and equally reachable from here, so either
        // would serve - this path uses TickCount64 because the limiter already stores its state as a
        // raw tick count in _lastOfflineRecoveryMs, and GetRecoveryHoldUntil reads it back the same way.
        long nowMs = Environment.TickCount64;

        if (_offlineRecoveryAttempts > 0)
        {
            TimeSpan required = OfflineRetryBackoff[Math.Min(_offlineRecoveryAttempts - 1, OfflineRetryBackoff.Length - 1)];
            TimeSpan waited = TimeSpan.FromMilliseconds(nowMs - _lastOfflineRecoveryMs);
            if (waited < required)
            {
                // Logged once per wait window rather than per attempt: recovery is re-evaluated every
                // few cycles, so logging each held attempt would repeat the same line for the length of
                // the outage. Cleared when an attempt is allowed, so each new window reports its wait.
                if (!_recoveryHoldLogged)
                {
                    _recoveryHoldLogged = true;
                    // Points at Diagnostics because this line cannot tell the user which of the two
                    // causes they have, and the Internet connectivity row there is where that is
                    // explained. A network that filters ping reaches this message with perfect
                    // connectivity, and nothing else would ever tell them so.
                    LogManager.Instance.LogMessage(
                        $"Could not confirm an internet connection - holding recovery for '{displayName}' for {required.TotalMinutes:F0} minutes " +
                        "(restarting the VPN cannot restore a connection that is down upstream). " +
                        "Run Diagnostics to check whether this network answers ping at all",
                        LogLevel.Warn);
                }
                return (false, Online: false);
            }
        }

        _lastOfflineRecoveryMs = nowMs;
        _offlineRecoveryAttempts++;
        _recoveryHoldLogged = false;
        LogManager.Instance.LogMessage(
            $"Could not confirm an internet connection - trying recovery for '{displayName}' anyway (attempt {_offlineRecoveryAttempts}), " +
            "in case it is being blocked locally rather than being down upstream",
            // Info, unlike the hold above, and the asymmetry is deliberate: holding means recovery is
            // *not* happening and the VPN stays broken for the whole window, which is worth surfacing;
            // this line announces that recovery *is* proceeding, and its outcome reports itself (the
            // helper logs Error if the service does not come back). Warn here badged the tray on every
            // recovery for anyone whose network filters ICMP, for an app doing exactly its job. A real
            // outage is still surfaced independently by the per-cycle port-detection warning.
            LogLevel.Info);
        return (true, Online: false);
    }
}
