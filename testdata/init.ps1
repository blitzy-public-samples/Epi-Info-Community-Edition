# Loads the rehearsal database into SQL Server LocalDB (Windows environment) or another instance.
# Usage: .\init.ps1                      (LocalDB, Windows auth)
#        .\init.ps1 -Server "localhost,1433" -User sa -Password '...'
param([string]$Server = "(localdb)\MSSQLLocalDB", [string]$User, [string]$Password)
$ErrorActionPreference = "Stop"
$auth = if ($User) { @("-U", $User, "-P", $Password) } else { @("-E") }
Get-ChildItem "$PSScriptRoot\sql\*.sql" | Sort-Object Name | ForEach-Object {
  Write-Host ">> $($_.Name)"
  & sqlcmd -C -b -f 65001 -S $Server @auth -i $_.FullName
  if ($LASTEXITCODE -ne 0) { throw "Failed on $($_.Name)" }
}
Write-Host "EpiRehearsal loaded. Check every row in the 99_verify output shows ok = 1."
