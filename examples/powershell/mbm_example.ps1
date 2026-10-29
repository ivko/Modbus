# Modbus Master command line (mbm) from PowerShell.
# Start a test slave first if you have no device:  mbm serve --port 1502 --simulate
$mbm = Join-Path $PSScriptRoot '..\..\mbm.exe'
if (-not (Test-Path $mbm)) { $mbm = 'mbm' }
$target = @('--tcp', '127.0.0.1:1502')

# Write two floats (registers 10-13) and read them back as JSON.
& $mbm write @target --address 10 --values '21.5,-3.25' --format float
if ($LASTEXITCODE -ne 0) { throw "write failed ($LASTEXITCODE)" }

$r = & $mbm read @target --address 10 --count 4 --format float --json | ConvertFrom-Json
foreach ($v in $r.values) { "{0}: {1}" -f $v.address, $v.value }

# Poll three input registers 5 times, one second apart, and append them to a CSV file.
1..5 | ForEach-Object {
    & $mbm read @target --table input-registers --address 0 --count 3 --plain --csv (Join-Path $env:TEMP 'trend.csv')
    Start-Sleep -Seconds 1
}

# Exit codes: 0 ok, 1 Modbus exception, 2 communication error, 3 usage error.
# Function 0x2B is not supported by the simulator, so it answers with exception 01 (exit code 1).
& $mbm raw @target --pdu "2B 0E 01 00" --json
"exit code: $LASTEXITCODE"
