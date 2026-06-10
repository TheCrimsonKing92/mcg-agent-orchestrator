param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("create-goal", "complete-task", "smoke")]
    [string]$Action,
    [string]$Url = "http://localhost:5087/",
    [int]$Port = 9222,
    [string]$Objective,
    [ValidateSet("simple", "sdlc")]
    [string]$Workflow = "simple",
    [bool]$AutoHandoff = $false,
    [string]$Goal,
    [int]$TaskNumber = 1,
    [string]$Note
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Convert-ToJavaScriptString {
    param([AllowNull()][string]$Value)
    if ($null -eq $Value) {
        return "null"
    }

    return ConvertTo-Json -Compress -InputObject $Value
}

function Invoke-DashboardExpression {
    param(
        [string]$Expression,
        [string]$TargetUrl = $Url
    )
    & (Join-Path $PSScriptRoot "Invoke-DashboardBrowser.ps1") -Expression $Expression -Url $TargetUrl -Port $Port
}

function New-DashboardGoalUrl {
    param([string]$GoalPrefix)
    $baseUrl = $Url.TrimEnd('/')
    return "$baseUrl/goal/$([Uri]::EscapeDataString($GoalPrefix))"
}

switch ($Action) {
    "create-goal" {
        if ([string]::IsNullOrWhiteSpace($Objective)) {
            throw "Objective is required for create-goal."
        }

        $objectiveJson = Convert-ToJavaScriptString $Objective
        $workflowJson = Convert-ToJavaScriptString $Workflow
        $autoHandoffLiteral = if ($AutoHandoff) { "true" } else { "false" }
        $expression = @"
(async () => {
  const objective = $objectiveJson;
  const form = document.querySelector('form[data-action="/api/goals"]');
  if (!form) throw new Error('goal form not found');
  form.querySelector('input[name="objective"]').value = objective;
  form.querySelector('select[name="workflow"]').value = $workflowJson;
  const autoHandoff = form.querySelector('#new-goal-auto-handoff');
  if (autoHandoff) autoHandoff.checked = $autoHandoffLiteral;
  await window.__dashboardSubmitForm(form);
  await new Promise(resolve => setTimeout(resolve, 1800));
  const goals = await fetch('/api/goals', { cache: 'no-store' }).then(response => response.json());
  const goal = goals.find(item => item.Objective === objective);
  if (!goal) throw new Error('created goal was not found');
  return {
    action: 'create-goal',
    status: document.querySelector('#op-status')?.textContent || '',
    goalId: goal.Id,
    objective,
    workflow: $workflowJson,
    autoHandoff: $autoHandoffLiteral
  };
})()
"@
        Invoke-DashboardExpression $expression
    }

    "complete-task" {
        if ([string]::IsNullOrWhiteSpace($Goal)) {
            throw "Goal is required for complete-task."
        }

        if ([string]::IsNullOrWhiteSpace($Note)) {
            throw "Note is required for complete-task."
        }

        $goalJson = Convert-ToJavaScriptString $Goal
        $noteJson = Convert-ToJavaScriptString $Note
        $goalPrefix = if ($Goal.Length -gt 8) { $Goal.Substring(0, 8) } else { $Goal }
        $targetUrl = New-DashboardGoalUrl $goalPrefix
        $expression = @"
(async () => {
  const goal = $goalJson;
  const taskNumber = $TaskNumber;
  const note = $noteJson;
  const prefix = goal.slice(0, 8);
  const selector = 'form[data-action="/api/goals/' + prefix + '/tasks/' + taskNumber + '/complete-verify"]';
  const form = document.querySelector(selector);
  if (form) {
    form.querySelector('input[name="note"]').value = note;
    await window.__dashboardSubmitForm(form);
  } else {
    const response = await fetch('/api/goals/' + prefix + '/tasks/' + taskNumber + '/complete-verify', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ passed: true, note })
    });
    if (!response.ok) {
      throw new Error('complete-and-verify API fallback failed with HTTP ' + response.status + ': ' + await response.text());
    }
  }
  await new Promise(resolve => setTimeout(resolve, 1800));
  const detail = await fetch('/api/goals/' + prefix, { cache: 'no-store' }).then(response => response.json());
  return {
    action: 'complete-task',
    goal: detail.Goal,
    task: detail.Tasks.find(task => task.Number === taskNumber),
    verificationSatisfied: detail.VerificationSatisfied
  };
})()
"@
        Invoke-DashboardExpression $expression $targetUrl
    }

    "smoke" {
        & (Join-Path $PSScriptRoot "Run-DashboardBrowserScript.ps1") -ScriptPath (Join-Path $PSScriptRoot "dashboard-smoke.js") -Url $Url -Port $Port
    }
}
