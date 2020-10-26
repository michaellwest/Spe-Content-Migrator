Clear-Host

Import-Module -Name SPE -Force

$scriptDirectory = & {
    if ($psISE) {
        Split-Path -Path $psISE.CurrentFile.FullPath        
    } else {
        $PSScriptRoot
    }
}
. "$($scriptDirectory)\Copy-RainbowContent.ps1"

$sourceSession = New-ScriptSession -user "Admin" -pass 'b' -conn "https://sourcesite.local"
$destinationSession = New-ScriptSession -user "Admin" -pass 'b' -conn "https://destinationsite.local"

$copyProps = @{
    "WhatIf"=$true
    "CopyBehavior"="CompareRevision"
    "Recurse"=$true
    "RemoveNotInSource"=$false
    "ClearAllCaches"=$true
    "LogLevel"="Normal"
    "CheckDependencies"=$false
    "BoringMode"=$false
    "FailOnError"=$false
}

$copyProps["SourceSession"] = $sourceSession
$copyProps["DestinationSession"] = $destinationSession
# Default Home
Copy-RainbowContent @copyProps -RootId "{110D559F-DEA5-42EA-9C1C-8A5DF7E70EF9}" *>&1 | Tee-Object "$($scriptDirectory)\Migration-DefaultHome.log"