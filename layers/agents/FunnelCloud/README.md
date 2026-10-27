# FunnelCloud Agent

> **FunnelCloud** is the distributed task execution layer for AJ. It enables remote agents on Windows, Linux, or macOS machines to execute tasks on behalf of the orchestrator.

---

## Overview

FunnelCloud extends AJ's capabilities beyond Docker containers to any machine on your network:

```
┌─────────────────────────────────────────────────────────────────┐
│                         AJ Orchestrator                         │
│                  (Docker - layers/orchestrator-dotnet)          │
└───────────────────────────┬─────────────────────────────────────┘
                            │ gRPC (mTLS)
            ┌───────────────┼───────────────┐
            ▼               ▼               ▼
    ┌───────────────┐ ┌───────────────┐ ┌───────────────┐
    │ FunnelCloud   │ │ FunnelCloud   │ │ FunnelCloud   │
    │ Agent         │ │ Agent         │ │ Agent         │
    │ (Windows)     │ │ (Linux)       │ │ (macOS)       │
    │ dev-workstation │ r730xd       │ │ build-server  │
    └───────────────┘ └───────────────┘ └───────────────┘
```

### Key Features

- **Gossip-Seed Discovery**: Agents discovered via HTTP gossip-seed endpoint (port 41421)
- **gRPC Task Execution**: RPC for task submission and results (port 41235, mutual TLS)
- **Certificate Infrastructure**: Pinned CA validation and an explicit client certificate allowlist
- **Windows Service**: Runs as a background service with auto-start

---

## Architecture

### Components

| Component              | Purpose                             | Technology              |
| ---------------------- | ----------------------------------- | ----------------------- |
| **FunnelCloud.Agent**  | Task execution service              | .NET 8 / gRPC / Kestrel |
| **FunnelCloud.Shared** | Common contracts                    | .NET 8 class library    |
| **scripts/**           | Certificate & deployment automation | PowerShell              |

### Network Ports

| Port  | Protocol | Purpose                          |
| ----- | -------- | -------------------------------- |
| 41421 | HTTP     | Discovery / gossip-seed endpoint |
| 41235 | TCP      | gRPC task execution (mutual TLS) |

### Security Model

```
┌──────────────────────────────────────────────────────────────┐
│                     Certificate Authority                     │
│              (layers/agents/FunnelCloud/certs/ca/)           │
│                                                              │
│  ca.crt          - CA public certificate                    │
│  ca.key          - CA private key (keep secure!)            │
│  ca-fingerprint.txt - SHA256 fingerprint for pinning        │
└──────────────────────────────────────────────────────────────┘
                              │
                    Signs agent certificates
                              │
        ┌─────────────────────┼─────────────────────┐
        ▼                     ▼                     ▼
┌───────────────┐     ┌───────────────┐     ┌───────────────┐
│ dev-workstation │   │    r730xd     │     │ build-server  │
│ agent.pfx      │    │ agent.pfx     │     │ agent.pfx     │
│ agent-fingerprint │ │ agent-fingerprint │ │ agent-fingerprint │
└───────────────┘     └───────────────┘     └───────────────┘
```

The agent verifies the CA fingerprint, validates each client certificate's chain and client-authentication purpose, and requires its SHA-256 fingerprint in `FUNNEL_ALLOWED_CLIENT_SHA256`. The orchestrator validates the agent's certificate against the configured CA and the requested agent ID. A missing certificate or allowlist stops the execution service; plaintext execution is not available.

---

## Quick Start

### Prerequisites

- **Build Server** (your development workstation):
  - .NET 10 SDK
  - OpenSSL (comes with Git for Windows)
  - PowerShell 5.1+
- **Client Machines** (where agents will run):
  - PowerShell Remoting enabled
  - Administrator access
  - NSSM (installed automatically)
- **Linux agents**: systemd 247 or newer for `LoadCredential`
- **Python Linux deployment**: verify each SSH host key out of band and add it to the deploying user's `~/.ssh/known_hosts`

### Step 1: Create the Certificate Authority (One Time)

On your build server, set `FUNNEL_CERT_PASSWORD` from a secure source, then create the CA that will sign all agent certificates:

```powershell
cd C:\Code\aj.westerfield.cloud\layers\agents\FunnelCloud\scripts
.\New-CACertificate.ps1
```

This creates:

- `certs/ca/ca.crt` - CA certificate
- `certs/ca/ca.key` - CA private key (protect this!)
- `certs/ca/ca-fingerprint.txt` - SHA256 fingerprint

When creating or rotating a CA, update the pinned fingerprint in `FunnelCloud.Agent/TrustConfig.cs` and rebuild every agent. Do not replace an existing CA without coordinating certificate rotation.

### Step 2: Create the Orchestrator Identity

From the AJ repository root, with `FUNNEL_CERT_PASSWORD` set, generate a dedicated client certificate and put it in the ignored local secrets directory:

```powershell
$funnel = Join-Path $PWD 'layers/agents/FunnelCloud'
& "$funnel/scripts/New-AgentCertificate.ps1" -AgentId 'aj-orchestrator'
New-Item -ItemType Directory -Force ./secrets/funnelcloud | Out-Null
Copy-Item "$funnel/certs/agents/aj-orchestrator/agent.pfx" ./secrets/funnelcloud/orchestrator.pfx
Copy-Item "$funnel/certs/ca/ca.crt" ./secrets/funnelcloud/ca.crt
[IO.File]::WriteAllText("$PWD/secrets/funnelcloud/pfx-password", $env:FUNNEL_CERT_PASSWORD)
$env:FUNNEL_ALLOWED_CLIENT_SHA256 = (Get-Content "$funnel/certs/agents/aj-orchestrator/agent-fingerprint.txt" -Raw).Trim()
```

Restrict access to this directory: its PFX and password grant command execution on agents that allow the certificate. The Compose configuration mounts the directory read-only into the orchestrator.

Outside Compose, configure `FUNNEL_CLIENT_CERT_PATH`, `FUNNEL_CA_PATH`, and either `FUNNEL_CLIENT_CERT_PASSWORD_FILE` or `FUNNEL_CLIENT_CERT_PASSWORD` on the orchestrator. The CA file is public; do not copy the CA private key to the orchestrator or agents.

### Step 3: Enable PowerShell Remoting (Build Server)

On your build server, enable remoting so clients can trigger builds:

```powershell
Enable-PSRemoting -Force
```

### Step 4: Configure Client Trust (Each Client)

On each client machine, add the build server to TrustedHosts:

```powershell
# Run as Administrator on the CLIENT machine
Set-Item WSMan:\localhost\Client\TrustedHosts -Value "BUILD_SERVER_NAME" -Force
```

Or use the setup script:

```powershell
.\Setup-FunnelCloudClient.ps1 -BuildServer "ians-r16"
```

### Step 5: Deploy Agent (From Client)

On the client machine, set `FUNNEL_CERT_PASSWORD` and `FUNNEL_ALLOWED_CLIENT_SHA256`, then run the deployment script. For Azure DevOps Server pipelines, configure those same values in the `funnelcloud-deploy` variable group; mark the password secret.

Each agent certificate's DNS subject alternative name or common name must match the agent ID advertised by that agent. Use the same ID when generating its certificate and setting `FUNNEL_AGENT_ID`.

```powershell
# Copy the script to the client first, then run:
.\Deploy-FunnelCloudAgent.ps1 -BuildServer "ians-r16"
```

This will:

1. Connect to build server via PowerShell Remoting
2. Generate a unique certificate for this agent
3. Build the agent with embedded certificate
4. Download and install as a Windows service

---

## Scripts Reference

### New-CACertificate.ps1

Creates the Certificate Authority for signing agent certificates.

```powershell
.\New-CACertificate.ps1 [-OutputPath <path>] [-ValidDays <int>] [-Force]
```

| Parameter  | Default     | Description              |
| ---------- | ----------- | ------------------------ |
| OutputPath | ../certs/ca | Where to create CA files |
| ValidDays  | 3650        | CA validity (10 years)   |
| Force      | false       | Overwrite existing CA    |

### New-AgentCertificate.ps1

Creates a signed certificate for a specific agent.

```powershell
.\New-AgentCertificate.ps1 -AgentId <name> [-CAPath <path>] [-ValidDays <int>] [-Force]
```

| Parameter | Default     | Description                    |
| --------- | ----------- | ------------------------------ |
| AgentId   | (required)  | Unique agent identifier        |
| CAPath    | ../certs/ca | Path to CA certificate         |
| ValidDays | 730         | Certificate validity (2 years) |
| Force     | false       | Overwrite existing certificate |

**Output files** (in `certs/agents/<AgentId>/`):

- `agent.key` - Private key
- `agent.crt` - Signed certificate
- `agent.pfx` - PKCS#12 bundle for .NET
- `agent-fingerprint.txt` - SHA256 fingerprint

### Setup-FunnelCloudClient.ps1

Configures a client machine for PowerShell Remoting to the build server.

```powershell
.\Setup-FunnelCloudClient.ps1 [-BuildServer <name>]
```

| Parameter   | Default  | Description              |
| ----------- | -------- | ------------------------ |
| BuildServer | ians-r16 | Build server hostname/IP |

### Deploy-FunnelCloudAgent.ps1

One-click deployment from a client machine.

```powershell
.\Deploy-FunnelCloudAgent.ps1 [-BuildServer <name>] [-AgentId <name>] [-InstallPath <path>]
```

| Parameter   | Default              | Description              |
| ----------- | -------------------- | ------------------------ |
| BuildServer | ians-r16             | Build server hostname/IP |
| AgentId     | $env:COMPUTERNAME    | Unique agent identifier  |
| InstallPath | C:\FunnelCloud\Agent | Local installation path  |

---

## Manual Installation

If you prefer manual installation over the deployment script:

### 1. Generate Certificate

Set `FUNNEL_CERT_PASSWORD` and `FUNNEL_ALLOWED_CLIENT_SHA256` on the build server and the target machine before generating certificates or installing the service.

On the build server:

```powershell
cd C:\Code\aj.westerfield.cloud\layers\agents\FunnelCloud\scripts
.\New-AgentCertificate.ps1 -AgentId "my-machine"
```

### 2. Build Agent

```powershell
cd C:\Code\aj.westerfield.cloud\layers\agents\FunnelCloud\FunnelCloud.Agent
dotnet publish -c Release -r win-x64 --self-contained -o C:\FunnelCloud\Agent
```

### 3. Copy Certificates

```powershell
# Copy to target machine
Copy-Item "certs\agents\my-machine\agent.pfx" -Destination "C:\FunnelCloud\Agent\Certificates\"
Copy-Item "certs\ca\ca.crt" -Destination "C:\FunnelCloud\Agent\Certificates\"
```

### 4. Install Service

```powershell
# On target machine (as Administrator)
icacls C:\FunnelCloud\Agent\Certificates /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F'
[IO.File]::WriteAllText('C:\FunnelCloud\Agent\Certificates\pfx-password', $env:FUNNEL_CERT_PASSWORD)
nssm install FunnelCloudAgent "C:\FunnelCloud\Agent\FunnelCloud.Agent.exe"
nssm set FunnelCloudAgent AppDirectory "C:\FunnelCloud\Agent"
nssm set FunnelCloudAgent AppEnvironmentExtra "FUNNEL_CERT_PATH=C:\FunnelCloud\Agent\Certificates\agent.pfx" "FUNNEL_CERT_PASSWORD_FILE=C:\FunnelCloud\Agent\Certificates\pfx-password" "FUNNEL_CA_PATH=C:\FunnelCloud\Agent\Certificates\ca.crt" "FUNNEL_ALLOWED_CLIENT_SHA256=$env:FUNNEL_ALLOWED_CLIENT_SHA256"
nssm set FunnelCloudAgent Start SERVICE_AUTO_START
nssm start FunnelCloudAgent
```

---

## Service Management

### View Service Status

```powershell
Get-Service FunnelCloudAgent
```

### View Logs

```powershell
Get-Content "C:\FunnelCloud\Agent\logs\stdout.log" -Tail 50 -Wait
```

### Restart Service

```powershell
Restart-Service FunnelCloudAgent
```

### Stop Service

```powershell
Stop-Service FunnelCloudAgent
```

### Uninstall Service

```powershell
nssm stop FunnelCloudAgent
nssm remove FunnelCloudAgent confirm
```

---

## Certificate Rotation

`FUNNEL_ALLOWED_CLIENT_SHA256` accepts comma-separated SHA-256 fingerprints, optionally prefixed with `SHA256:`. Add a replacement client fingerprint before rotating the orchestrator certificate, then remove the old fingerprint and restart each agent. Certificate revocation lists are not checked; removing a fingerprint is the way to revoke a client.

The agent also accepts `FUNNEL_CERT_PASSWORD_FILE` instead of `FUNNEL_CERT_PASSWORD`. The deployment scripts use a protected password file and configure the service to read it. Restrict access to all private keys and password files, and rotate any credentials that have been exposed.

---

## Troubleshooting

### Agent not discovered?

1. Check firewall allows HTTP 41421 and TCP 41235
2. Verify agent is running: `Get-Service FunnelCloudAgent`
3. Check logs: `Get-Content "C:\FunnelCloud\Agent\logs\stderr.log" -Tail 50`

### "Can't connect to build server"?

1. Verify PowerShell Remoting: `Test-WSMan -ComputerName BUILD_SERVER`
2. Check TrustedHosts: `Get-Item WSMan:\localhost\Client\TrustedHosts`
3. Verify firewall allows WinRM (TCP 5985)

### Certificate errors?

1. Verify CA exists: `Test-Path "certs\ca\ca.crt"`
2. Verify agent cert: `Test-Path "certs\agents\AGENT_ID\agent.pfx"`
3. Check certificate dates: `openssl x509 -in agent.crt -noout -dates`

### Service won't start?

1. Check Windows Event Viewer: `eventvwr.msc` → Windows Logs → Application
2. Try running manually: `.\FunnelCloud.Agent.exe`
3. Verify executable exists: `Test-Path "C:\FunnelCloud\Agent\FunnelCloud.Agent.exe"`

---

## Development

### Project Structure

```
FunnelCloud/
├── certs/
│   ├── ca/                    # Certificate Authority
│   │   ├── ca.crt
│   │   ├── ca.key
│   │   └── ca-fingerprint.txt
│   └── agents/                # Per-agent certificates
│       ├── dev-workstation/
│       └── r730xd/
├── FunnelCloud.Agent/         # Main agent executable
│   ├── Program.cs
│   ├── TrustConfig.cs
│   ├── Services/
│   │   ├── DiscoveryListener.cs
│   │   ├── GrpcServerHost.cs
│   │   ├── TaskServiceImpl.cs
│   │   └── TaskExecutor.cs
│   └── Protos/
│       └── task_service.proto
├── FunnelCloud.Shared/        # Shared contracts
│   └── Contracts/
└── scripts/                   # Deployment automation
    ├── New-CACertificate.ps1
    ├── New-AgentCertificate.ps1
    ├── Setup-FunnelCloudClient.ps1
    └── Deploy-FunnelCloudAgent.ps1
```

### Building

```powershell
cd FunnelCloud
dotnet build
dotnet test
```

### Debugging

Run with verbose logging:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:Logging__LogLevel__Default = "Debug"
.\FunnelCloud.Agent.exe
```

---

## Integration with AJ

The orchestrator discovers and communicates with agents automatically:

```csharp
// layers/orchestrator-dotnet/AJ.Orchestrator/Services/AgentDiscoveryService.cs
// HTTP gossip-seed discovery at port 41421
// Returns list of agents with capabilities, roles, and addresses
```

```csharp
// layers/orchestrator-dotnet/AJ.Orchestrator.Abstractions/Protos/GrpcAgentClient.cs
// gRPC mutual-TLS connection to port 41235
// TaskService.TaskServiceClient sends TaskRequest, receives TaskResult
```

---

## Security Considerations

1. **Protect the CA private key** (`ca.key`) - if compromised, all agent identities can be forged
2. **Use unique agent IDs** - each machine should have its own certificate
3. **Rotate certificates** before expiration (default: 2 years)
4. **Restrict network access** - only allow gRPC traffic from known orchestrator IPs
5. **Allow only orchestrator certificates** - any allowed client can execute commands as the agent service account

---

_Last Updated: March 8, 2026_
