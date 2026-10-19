$ErrorActionPreference = 'Stop'
$taskRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $taskRoot
$taskSession = Get-Content -LiteralPath (Join-Path $taskRoot 'output\playwright\session.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$taskFixtureUri = [System.Uri]$taskSession.url
if ($taskFixtureUri.Host -ne '127.0.0.1' -or $taskFixtureUri.Port -ne 27124 -or $taskSession.token -ne 'integration-session') { throw '仅能对本地集成测试服务执行此脚本。' }
$taskHeaders = @{'X-Skapihub-Admin'=$taskSession.token}
$taskState = Invoke-RestMethod -Uri ($taskSession.url + '/_app/state') -Headers $taskHeaders
foreach ($taskRelay in $taskState.relays) { Invoke-RestMethod -Uri ($taskSession.url + '/_app/relays/' + $taskRelay.id) -Headers $taskHeaders -Method Delete | Out-Null }
$taskSettings = @{port=27124;apiKey='sk-local'} | ConvertTo-Json
Invoke-RestMethod -Uri ($taskSession.url + '/_app/settings') -Headers $taskHeaders -Method Put -ContentType 'application/json' -Body $taskSettings | Out-Null
$taskTemplate = Get-Content -LiteralPath (Join-Path $taskRoot 'tools\verify-ui.js') -Raw -Encoding UTF8
$taskCodeFile = Join-Path $taskRoot 'output\playwright\verify-ui.generated.js'
[System.IO.File]::WriteAllText($taskCodeFile, $taskTemplate.Replace('__UPSTREAM_URL__', $taskSession.upstream), [System.Text.UTF8Encoding]::new($false))
$taskNpx = (Get-Command npx.cmd -ErrorAction Stop).Source
& $taskNpx --yes --package @playwright/cli playwright-cli -s=skapihub-ui open about:blank --browser msedge
& $taskNpx --yes --package @playwright/cli playwright-cli -s=skapihub-ui cookie-set skapihub-session $taskSession.token --domain 127.0.0.1 --path /_app --httpOnly --sameSite Strict
& $taskNpx --yes --package @playwright/cli playwright-cli -s=skapihub-ui goto $taskSession.url
& $taskNpx --yes --package @playwright/cli playwright-cli -s=skapihub-ui resize 1240 810
& $taskNpx --yes --package @playwright/cli playwright-cli -s=skapihub-ui snapshot
& $taskNpx --yes --package @playwright/cli playwright-cli -s=skapihub-ui run-code --filename $taskCodeFile
if ($LASTEXITCODE -ne 0) { throw '界面验证失败。' }
