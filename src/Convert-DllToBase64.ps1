# https://powershell.one/tricks/assemblies/load-from-memory
Clear-Host

$scriptDirectory = & {
    if ($psISE) {
        Split-Path -Path $psISE.CurrentFile.FullPath        
    } else {
        $PSScriptRoot
    }
}

$library = "$($scriptDirectory)\Unicorn.PowerShell\bin\Debug\Unicorn.PowerShell.dll"
$libraryBytes = [System.IO.File]::ReadAllBytes($library)
# turn bytes into Base64 string:
$libraryBase64 = [System.Convert]::ToBase64String($libraryBytes)
$libraryBase64 | Set-Clipboard