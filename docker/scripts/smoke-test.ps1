# Smoke test against a real, disposable Jellyfin container.
# Run from the project root:
#   powershell -File docker/scripts/smoke-test.ps1 -JellyfinVersion 10.11.10
#   powershell -File docker/scripts/smoke-test.ps1 -JellyfinVersion 12.4 -Keep
#
# Steps: start jellyfin/jellyfin:<version> with no host mount, complete the startup wizard through
# the API, install File Transformation through the Jellyfin catalogue (Jellyfin picks the build
# matching its own version), copy the local Release build in, restart, then check:
#   - both plugins load as Active (/Plugins)
#   - the anonymous EasyNotif assets answer 200
#   - the served index.html carries the injected enotif-user.js script
#   - the Jellyfin log since the restart has the injection line and no [ERR] line naming EasyNotif
# Prints a PASS/FAIL table and the duration; exit code 1 when any check fails. The container is
# removed at the end unless -Keep is given (then its URL and admin credentials are printed).
# Images are never removed.

param(
    [Parameter(Mandatory = $true)][string]$JellyfinVersion,
    [int]$Port = 8100,
    [switch]$Keep,
    [string]$AdminPassword = ""
)

$ErrorActionPreference = "Stop"
$started = Get-Date
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$image = "jellyfin/jellyfin:$JellyfinVersion"
$container = "easynotif-smoke-$JellyfinVersion"
$base = "http://localhost:$Port"
$ftRepo = "https://www.iamparadox.dev/jellyfin/plugins/manifest.json"
$ftName = "File Transformation"
$adminUser = "smoke"
if (-not $AdminPassword) { $AdminPassword = [guid]::NewGuid().ToString("N").Substring(0, 16) }
$authBase = 'MediaBrowser Client="EasyNotifSmoke", Device="smoke", DeviceId="easynotif-smoke", Version="1.0"'
$results = New-Object System.Collections.Generic.List[object]
$created = $false

function Invoke-Docker {
    # Native call with a non-terminating error preference: PS 5.1 turns redirected stderr into
    # errors, which "Stop" would make fatal even on exit code 0.
    $ErrorActionPreference = "Continue"
    $out = & docker @args 2>&1 | ForEach-Object { "$_" }
    [pscustomobject]@{ Code = $LASTEXITCODE; Output = ($out -join "`n") }
}

function Add-Result([string]$Check, [bool]$Ok, [string]$Value) {
    $results.Add([pscustomobject]@{ Result = $(if ($Ok) { "PASS" } else { "FAIL" }); Check = $Check; Value = $Value })
}

function Step([string]$Text) { Write-Host "[smoke] $Text" }

function Wait-Server([int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try { return Invoke-RestMethod "$base/System/Info/Public" -TimeoutSec 5 } catch { Start-Sleep 2 }
    }
    throw "Jellyfin did not answer on $base within $TimeoutSeconds s."
}

function Wait-Log([string]$Pattern, [string]$Since, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $log = (Invoke-Docker logs --since $Since $container).Output
        if ($log -match $Pattern) { return $log }
        Start-Sleep 2
    }
    throw "Log line '$Pattern' not seen within $TimeoutSeconds s."
}

function Get-Status([string]$Path) {
    try { return (Invoke-WebRequest "$base$Path" -UseBasicParsing -TimeoutSec 10).StatusCode }
    catch { if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode } return 0 }
}

function Invoke-Api([string]$Method, [string]$Path, $Body = $null, [string]$Token = "") {
    $auth = $authBase
    if ($Token) { $auth = "$authBase, Token=`"$Token`"" }
    $params = @{ Method = $Method; Uri = "$base$Path"; Headers = @{ Authorization = $auth }; TimeoutSec = 30 }
    if ($null -ne $Body) { $params.Body = (ConvertTo-Json $Body -Depth 5); $params.ContentType = "application/json" }
    # PS 5.1 writes a JSON array as one object: unroll it so callers get the elements.
    Invoke-RestMethod @params | ForEach-Object { $_ }
}

function UtcStamp { (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ") }

$exitCode = 1
try {
    Step "Pre-flight: $container on port $Port"
    if ((Invoke-Docker ps -a --filter "name=^$container$" --format "{{.Names}}").Output) {
        throw "A container named $container already exists. Remove it first: docker rm -f $container"
    }
    if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) {
        throw "Port $Port is already in use. Pass -Port <free port>."
    }
    if ((Invoke-Docker image inspect $image).Code -ne 0) {
        Step "Pulling $image"
        $pull = Invoke-Docker pull $image
        if ($pull.Code -ne 0) { throw "docker pull $image failed: $($pull.Output)" }
    }

    Step "Building the plugin"
    $stage = Join-Path $env:TEMP "easynotif-smoke-$JellyfinVersion-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    & powershell -NoProfile -File (Join-Path $scriptDir "deploy-plugin.ps1") -PluginsDir $stage -NoRestart | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Build failed (deploy-plugin.ps1 exit $LASTEXITCODE)." }
    $pluginDir = Join-Path $stage "Jellyfin.Plugin.EasyNotif"

    Step "Starting $image"
    $runSince = UtcStamp
    $run = Invoke-Docker run -d --name $container -p "${Port}:8096" $image
    if ($run.Code -ne 0) { throw "docker run failed: $($run.Output)" }
    $created = $true
    # System/Info/Public answers while the other endpoints still return 503: wait for the log line.
    Wait-Log "Startup complete" $runSince 180 | Out-Null
    $info = Wait-Server 60
    Add-Result "Jellyfin version" ($info.Version -eq $JellyfinVersion -or $info.Version.StartsWith("$JellyfinVersion.")) $info.Version

    Step "Startup wizard"
    Invoke-Api POST "/Startup/Configuration" @{ UICulture = "en-US"; MetadataCountryCode = "US"; PreferredMetadataLanguage = "en" } | Out-Null
    Invoke-Api GET "/Startup/User" | Out-Null
    Invoke-Api POST "/Startup/User" @{ Name = $adminUser; Password = $AdminPassword } | Out-Null
    Invoke-Api POST "/Startup/Complete" | Out-Null
    $token = (Invoke-Api POST "/Users/AuthenticateByName" @{ Username = $adminUser; Pw = $AdminPassword }).AccessToken

    Step "Installing $ftName from the catalogue"
    $repos = @(Invoke-Api GET "/Repositories" -Token $token)
    $repos = @($repos | Where-Object { $_.Url -ne $ftRepo }) + @([pscustomobject]@{ Name = $ftName; Url = $ftRepo; Enabled = $true })
    Invoke-Api POST "/Repositories" $repos -Token $token | Out-Null
    Invoke-Api POST ("/Packages/Installed/" + [uri]::EscapeDataString($ftName) + "?repositoryUrl=" + [uri]::EscapeDataString($ftRepo)) -Token $token | Out-Null
    $deadline = (Get-Date).AddSeconds(120)
    do {
        Start-Sleep 2
        $ftDir = (Invoke-Docker exec $container sh -c "ls -d '/config/plugins/$ftName'_*/meta.json 2>/dev/null").Output
    } until ($ftDir -or (Get-Date) -gt $deadline)
    if (-not $ftDir) { throw "$ftName was not installed within 120 s." }

    Step "Deploying the local build and restarting"
    $cp = Invoke-Docker cp $pluginDir "${container}:/config/plugins/"
    if ($cp.Code -ne 0) { throw "docker cp failed: $($cp.Output)" }
    $since = UtcStamp
    $restart = Invoke-Docker restart $container
    if ($restart.Code -ne 0) { throw "docker restart failed: $($restart.Output)" }
    $log = Wait-Log "Startup complete" $since 180
    Wait-Server 60 | Out-Null

    Step "Checks"
    $plugins = @(Invoke-Api GET "/Plugins" -Token $token)
    foreach ($name in @("Easy Notif", $ftName)) {
        $p = $plugins | Where-Object { $_.Name -eq $name } | Select-Object -First 1
        if ($p) { Add-Result "Plugin $name" ($p.Status -eq "Active") "$($p.Version) $($p.Status)" }
        else { Add-Result "Plugin $name" $false "absent" }
    }
    foreach ($path in @("/EasyNotif/config.js", "/EasyNotif/enotif-user.js", "/EasyNotif/enotif-user.css", "/EasyNotif/strings/en.json")) {
        $code = Get-Status $path
        Add-Result "GET $path" ($code -eq 200) "$code"
    }
    $index = ""
    try { $index = (Invoke-WebRequest "$base/web/index.html" -UseBasicParsing -TimeoutSec 10).Content } catch { $index = "" }
    Add-Result "index.html injected" ($index -match '/EasyNotif/enotif-user\.js') $(if ($index -match '/EasyNotif/enotif-user\.js') { "script present" } else { "script missing" })
    $log = (Invoke-Docker logs --since $since $container).Output
    $lines = $log -split "`n"
    $registered = @($lines | Where-Object { $_ -match '\[EasyNotif\] Registered index\.html transformation' }).Count
    Add-Result "Injection registered (log)" ($registered -ge 1) "$registered line(s)"
    $errors = @($lines | Where-Object { $_ -match '\[ERR\]' -and $_ -match 'EasyNotif' })
    Add-Result "[ERR] lines naming EasyNotif" ($errors.Count -eq 0) "$($errors.Count)"
    foreach ($e in $errors) { Write-Host "  $e" }

    if (@($results | Where-Object { $_.Result -eq "FAIL" }).Count -eq 0) { $exitCode = 0 }
}
catch {
    Add-Result "Run" $false $_.Exception.Message
}
finally {
    if ($stage -and (Test-Path $stage)) { Remove-Item $stage -Recurse -Force }
    $results | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
    $verdict = $(if ($exitCode -eq 0) { "PASS" } else { "FAIL" })
    Write-Host ("[smoke] {0} on Jellyfin {1} in {2:N0} s" -f $verdict, $JellyfinVersion, ((Get-Date) - $started).TotalSeconds)
    if ($created) {
        if ($Keep) {
            Write-Host "[smoke] Kept: $container at $base (admin '$adminUser', password '$AdminPassword'). Remove: docker rm -f $container"
        }
        else {
            Invoke-Docker rm -f $container | Out-Null
            Write-Host "[smoke] Removed $container"
        }
    }
}
exit $exitCode
