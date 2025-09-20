

param(
  [string]$RepoDir,                       # e.g. 'C:\path\to\unity-client'
  [string]$Repo,                          # optional: owner/repo; auto from RepoDir if omitted
  [Alias('Branch')]
  [string[]]$DevBranches = @('dev','development','main'),
  [string]$WorkflowName                   # optional: exact workflow name/file filter
)



$PipeName = 'TraycerHud'
$PipeTimeoutSeconds = 5
$PipeEncoding = [System.Text.UTF8Encoding]::new($false)
$BuildWellId = 'build'
$BuildWellWidth = 240

function Send-TraycerMessage {

  param([hashtable]$Payload)

  $json = (ConvertTo-Json $Payload -Compress) + "`n"
  $deadline = [DateTime]::UtcNow.AddSeconds($PipeTimeoutSeconds)
  $lastError = $null

  while ([DateTime]::UtcNow -lt $deadline) {

    $client = $null
    $writer = $null
    try {
      $client = [System.IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [System.IO.Pipes.PipeDirection]::Out)
      $client.Connect(250)
      $writer = [System.IO.StreamWriter]::new($client, $PipeEncoding)
      $writer.AutoFlush = $true
      $writer.Write($json)
      return $true
    } catch [TimeoutException] {
      Start-Sleep -Milliseconds 100

    }

    catch {
      $lastError = $_
      Start-Sleep -Milliseconds 100
    }

    finally {
      if ($writer) { $writer.Dispose() }
      if ($client) { $client.Dispose() }
    }
  }

  if ($lastError) {
    Write-Warning ("Failed to send to Traycer: {0}" -f $lastError.Exception.Message)
  }
  return $false
}



function Ensure-BuildWell {
  Send-TraycerMessage(@{ op = 'add'; well = $BuildWellId; width = $BuildWellWidth }) | Out-Null
}



function Set-BuildWellText {
  param([string]$Text, [string]$Foreground, [string]$Background)
  $payload = @{ op = 'set'; well = $BuildWellId; text = $Text }
  if ($Foreground) { $payload.fg = $Foreground }
  if ($Background) { $payload.bg = $Background }
  Send-TraycerMessage($payload) | Out-Null
}

function FmtLocal([string]$iso) {
  if ([string]::IsNullOrWhiteSpace($iso)) { return '-' }
  try { ([datetime]$iso).ToLocalTime().ToString('M/d h:mm tt') } catch { '-' }
}

function Get-NWOFromDir([string]$dir) {
  try {
    $url = git -C $dir remote get-url origin 2>$null
    if ($url -match 'github\.com[:/](?<nwo>[^/]+/[^/]+?)(?:\.git)?$') { return $Matches['nwo'] }
  } catch {}
  return $null
}

if ($RepoDir) { $RepoDir = (Resolve-Path $RepoDir).Path }
if (-not $Repo -and $RepoDir) { $Repo = Get-NWOFromDir $RepoDir }

$repoArg = $Repo ? @('-R', $Repo) : @()
$wfArg = $WorkflowName ? @('--workflow', $WorkflowName) : @()
$gitC = $RepoDir ? @('-C', $RepoDir) : @()

# ---- Very last run (any branch/workflow)
$last = gh run list @repoArg -L 1 --json status,conclusion,updatedAt,url 2>$null |
  ConvertFrom-Json | Select-Object -First 1

$palette = @{

  'success'   = @{ Label = '✔️'; Background = '#8050FA7B' }
  'failure'   = @{ Label = '❌'; Background = '#80FF5555' }
  'cancelled' = @{ Label = '✖️'; Background = '#80F1FA8C' }
  'timed_out' = @{ Label = '⏲️'; Background = '#80FFB86C' }
}
$stateLabel = '❓'
$fg = '#80F8F8F2'
$bg = '#8044475A'


if ($last -and $last.status -eq 'completed') {
  $key = $last.conclusion.ToLowerInvariant()
  if ($palette.ContainsKey($key)) {
    $stateLabel = $palette[$key].Label
    $fg = $palette[$key].Foreground
    $bg = $palette[$key].Background
  }
}

$right = ($last) ? ("{0} {1}" -f $stateLabel, (FmtLocal $last.updatedAt)) : '-'

# ---- Latest successful "dev" build
$cands = gh run list @repoArg @wfArg --status success -L 50 --json headSha,updatedAt,url,workflowName,headBranch 2>$null | ConvertFrom-Json
$succ = $null

if ($cands) {
  $lowerBranches = $DevBranches | ForEach-Object { $_.ToLowerInvariant() }
  $succ = $cands | Where-Object { $_.headBranch -and ($lowerBranches -contains $_.headBranch.ToLowerInvariant()) } | Select-Object -First 1
  if (-not $succ) { $succ = $cands | Select-Object -First 1 }
}



# Resolve version from tags (exact > nearest > short SHA). Requires local clone for git.
$left = '<no dev success>  -'

if ($succ) {
  $sha = $succ.headSha
  $version = $null
  if ($RepoDir -and $sha) {
    try { git @gitC fetch --tags --quiet 2>$null | Out-Null } catch {}
    $exact = (git @gitC tag --points-at $sha 2>$null | Select-Object -First 1)
    if ($exact) { $version = $exact }
    elseif ($sha) {
      try { $version = (git @gitC describe --tags --abbrev=0 $sha 2>$null) } catch {}
    }
  }

  if (-not $version -and $sha) { $version = $sha.Substring(0, 7) }
  $left = "{0}  {1}" -f ($version ?? '<no dev success>'), (FmtLocal $succ.updatedAt)
}

# ---- Final
$summary = "{0}  |  {1}" -f $left, $right
Write-Output $summary

Ensure-BuildWell
Set-BuildWellText -Text $summary -Foreground $fg -Background $bg