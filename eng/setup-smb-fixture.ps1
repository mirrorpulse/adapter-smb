[CmdletBinding()]
param([switch]$Cleanup, [switch]$DisconnectLeft, [switch]$ReconnectLeft)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -cne 'true' -or -not $env:RUNNER_TEMP) { throw 'The SMB fixture may only run on a disposable GitHub Actions runner.' }
$runnerRoot = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\') + '\'
if ($DisconnectLeft -or $ReconnectLeft) {
    if ($DisconnectLeft -and $ReconnectLeft) { throw 'Choose one fixture transition.' }
    $user = $env:MP_SMB_FIXTURE_LEFT_USER
    if ($user -cnotmatch '\AMpSmbL[a-f0-9]{8}\z' -or $env:MP_SMB_FIXTURE_LEFT_SHARE -cne "\\localhost\$user") {
        throw 'The fixture transition targets an unexpected share.'
    }
    $backing = [IO.Path]::GetFullPath($env:MP_SMB_FIXTURE_BACKING)
    if (-not $backing.StartsWith($runnerRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($backing) -cnotmatch '\Amp-smb-[a-f0-9]{32}\z') { throw 'The share backing path escaped the fixture.' }
    if ($DisconnectLeft) { Remove-SmbShare -Name $user -Force }
    else { New-SmbShare -Name $user -Path $backing -FullAccess @("$env:COMPUTERNAME\$user", "$env:USERDOMAIN\$env:USERNAME") | Out-Null }
    return
}
if ($Cleanup) {
    foreach ($side in @('LEFT', 'RIGHT')) {
        $user = [Environment]::GetEnvironmentVariable("MP_SMB_FIXTURE_${side}_USER")
        $share = [Environment]::GetEnvironmentVariable("MP_SMB_FIXTURE_${side}_SHARE")
        if ($user -and $user -cmatch '\AMpSmb[LR][a-f0-9]{8}\z') {
            if ($share -cne "\\localhost\$user") { throw 'The fixture share identity differs from its disposable user.' }
            if (Get-SmbShare -Name $user -ErrorAction SilentlyContinue) { Remove-SmbShare -Name $user -Force }
            if (Get-LocalUser -Name $user -ErrorAction SilentlyContinue) { Remove-LocalUser -Name $user }
        }
    }
    $backing = $env:MP_SMB_FIXTURE_BACKING
    if ($backing) {
        $resolved = [IO.Path]::GetFullPath($backing)
        if (-not $resolved.StartsWith($runnerRoot, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($resolved) -cnotmatch '\Amp-smb-[a-f0-9]{32}\z') { throw 'The fixture cleanup escaped its runner directory.' }
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
    return
}
$suffix = [Guid]::NewGuid().ToString('N')
$backing = Join-Path $runnerRoot "mp-smb-$suffix"
New-Item -ItemType Directory -Path $backing | Out-Null
"MP_SMB_FIXTURE_BACKING=$backing" | Out-File -LiteralPath $env:GITHUB_ENV -Append -Encoding utf8
$runner = "$env:USERDOMAIN\$env:USERNAME"
foreach ($side in @('LEFT', 'RIGHT')) {
    $letter = $side.Substring(0, 1)
    $user = "MpSmb$letter$($suffix.Substring(0, 8))"
    $password = 'Mp9!' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    Write-Output "::add-mask::$password"
    # Publish cleanup identity before creating resources, including partial setup failures.
    "MP_SMB_FIXTURE_${side}_USER=$user" | Out-File -LiteralPath $env:GITHUB_ENV -Append -Encoding utf8
    "MP_SMB_FIXTURE_${side}_SHARE=\\localhost\$user" | Out-File -LiteralPath $env:GITHUB_ENV -Append -Encoding utf8
    "MP_SMB_FIXTURE_${side}_PASSWORD=$password" | Out-File -LiteralPath $env:GITHUB_ENV -Append -Encoding utf8
    New-LocalUser -Name $user -Password (ConvertTo-SecureString $password -AsPlainText -Force) -PasswordNeverExpires | Out-Null
    $acl = Get-Acl -LiteralPath $backing
    $rule = [Security.AccessControl.FileSystemAccessRule]::new("$env:COMPUTERNAME\$user", 'Modify',
        'ContainerInherit, ObjectInherit', 'None', 'Allow')
    $acl.AddAccessRule($rule)
    Set-Acl -LiteralPath $backing -AclObject $acl
    New-SmbShare -Name $user -Path $backing -FullAccess @("$env:COMPUTERNAME\$user", $runner) | Out-Null
    $password = $null
}
