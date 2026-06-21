# UnityExplorerTLD.MCP - Windows host setup (run once, as Administrator).
#
# Only needed so clients on *other hosts* (i.e. WSL in NAT mode, or another PC on
# the LAN) can reach the in-game MCP server. NOT needed if your MCP client runs
# on Windows or in WSL mirrored networking (those use localhost, which is exempt).
#
#   1. URL ACL  - lets the (non-elevated) game bind http://+:PORT/
#   2. Firewall - allows inbound TCP PORT
#
# Run:  powershell -ExecutionPolicy Bypass -File setup.ps1   (elevated)

param([int]$Port = 3000)

$ErrorActionPreference = 'Continue'

# 1. URL ACL for the strong-wildcard prefix the server binds. sid=S-1-1-0 = "Everyone"
#    (locale-independent). Harmless if it already exists.
& netsh http add urlacl url="http://+:$Port/" sid=S-1-1-0 2>&1 | Out-Null
Write-Host "URL ACL ensured for http://+:$Port/"

# 2. Inbound firewall rule.
$ruleName = "TLD UnityExplorer MCP"
if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow `
        -Protocol TCP -LocalPort $Port -Profile Any | Out-Null
    Write-Host "Firewall rule '$ruleName' added (inbound TCP $Port)."
} else {
    Write-Host "Firewall rule '$ruleName' already present."
}

Write-Host "Host setup complete for port $Port."
