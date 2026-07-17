$databasePath = Join-Path $PSScriptRoot "mailpit.db"

Start-Process mailpit.exe -ArgumentList "--database `"$databasePath`""