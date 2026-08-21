# Emits the current time in both UTC and local, with the offset, so timestamp comparisons
# across sources do not require guessing.
#
# Why this exists: orchestrator event logs (.orchestrator/logs/conduct-events.log, goal-events)
# are UTC with a +00:00 suffix. Git author dates are LOCAL with a -05:00 offset. Mixing the two
# once made a landing at 01:58:48 UTC look like it happened after an escalation seen at 01:58:50,
# which inverted a causal reading and produced a retracted backlog item.

$ErrorActionPreference = 'Stop'

$nowUtc = [DateTimeOffset]::UtcNow
$nowLocal = [DateTimeOffset]::Now
$offset = $nowLocal.Offset

$sign = if ($offset.Ticks -lt 0) { '-' } else { '+' }
$offsetText = '{0}{1:00}:{2:00}' -f $sign, [Math]::Abs($offset.Hours), [Math]::Abs($offset.Minutes)

Write-Output ("Current time: {0} UTC | {1} local ({2}). Orchestrator event logs are UTC; git author dates are local {2}." -f
    $nowUtc.ToString('yyyy-MM-dd HH:mm:ss'),
    $nowLocal.ToString('yyyy-MM-dd HH:mm:ss'),
    $offsetText)
