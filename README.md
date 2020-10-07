# Spe Content Migrator

Script used to migrate content between Sitecore instances using Sitecore PowerShell Extensions.

![Demo](docs/images/demo.gif)

## Getting Started

Prerequisites:

* Unicorn and Rainbow are deployed to your Sitecore instances.
* Spe 6.0+ is installed and SPE Remoting is enabled on your Sitecore instances.

Running:

```powershell
Clear-Host

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
    "CopyBehavior"="Overwrite"
    "Recurse"=$true
    "RemoveNotInSource"=$false
    "ClearAllCaches"=$true
    "Detailed"=$true
    "CheckDependencies"=$false
    "BoringMode"=$false
}

$copyProps["SourceSession"] = $sourceSession
$copyProps["DestinationSession"] = $destinationSession
# Default Home
Copy-RainbowContent @copyProps -RootId "{110D559F-DEA5-42EA-9C1C-8A5DF7E70EF9}"
```

## How it works

My approach is this:

* Get all the "ItemId+RevisionId+ParentId+Language" combinations of the latest version for each item under RootId.
  * "Language" represents that of the `__Revision` field. (i.e. "en", "en-GB", etc.)
* Build a tree of all the unique items.
* Build a list of all unique items to skip.
  * If **Overwrite**, all items are included.
  * If **SkipExisting**, only the existence of the "ItemId" is checked.
  * If **CompareRevision**, the "ItemId:RevisionId:Language" is checked.
  * If an item is not skipped, all versions/languages of an item are migrated.