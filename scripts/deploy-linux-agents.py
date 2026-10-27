#!/usr/bin/env python3
"""Deploy FunnelCloud agents to Linux hosts using paramiko for SSH automation."""

import paramiko
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

WORKSPACE = Path(__file__).parent.parent
SECRETS = WORKSPACE / "secrets"
FUNNELCLOUD = WORKSPACE / "layers" / "agents" / "FunnelCloud"
CERTS = FUNNELCLOUD / "certs"


def get_ssh_password():
    return (SECRETS / "ssh.txt").read_text().strip()


def get_ssh_client(host: str, user: str = "ian") -> paramiko.SSHClient:
    client = paramiko.SSHClient()
    client.load_system_host_keys()
    client.set_missing_host_key_policy(paramiko.RejectPolicy())

    # Use password auth (simpler and works reliably)
    password = get_ssh_password()
    client.connect(
        host,
        username=user,
        password=password,
        timeout=10,
        allow_agent=False,
        look_for_keys=False,
    )
    print(f"[✓] Connected to {host}")
    return client


def run_cmd(client: paramiko.SSHClient, cmd: str, sudo: bool = False) -> str:
    if sudo:
        password = get_ssh_password()
        # Use bash -c with heredoc-style sudo
        full_cmd = f"sudo -S bash -c '{cmd}'"
        stdin, stdout, stderr = client.exec_command(full_cmd, get_pty=True)
        stdin.write(password + "\n")
        stdin.flush()
    else:
        stdin, stdout, stderr = client.exec_command(cmd)

    exit_code = stdout.channel.recv_exit_status()
    output = stdout.read().decode()
    errors = stderr.read().decode()

    if exit_code != 0:
        # Filter out sudo password prompt from errors
        errors = "\n".join(
            l
            for l in errors.split("\n")
            if "[sudo]" not in l and "password" not in l.lower()
        )
        if errors.strip():
            print(f"    [!] {errors.strip()}")
        raise RuntimeError(f"Remote command failed with exit code {exit_code}")

    return output.strip()


def setup_ssh_key(host: str, user: str = "ian"):
    """Copy SSH public key to remote host for future passwordless access."""
    key_path = Path.home() / ".ssh" / "id_rsa.pub"
    if not key_path.exists():
        print(f"[!] No SSH public key at {key_path}")
        return

    pubkey = key_path.read_text().strip()
    client = get_ssh_client(host, user)

    try:
        run_cmd(client, "mkdir -p ~/.ssh && chmod 700 ~/.ssh")

        # Check if key already exists
        existing = run_cmd(client, "cat ~/.ssh/authorized_keys 2>/dev/null || echo ''")
        if pubkey in existing:
            print(f"[✓] SSH key already installed on {host}")
        else:
            run_cmd(
                client,
                f"echo '{pubkey}' >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys",
            )
            print(f"[✓] SSH key installed on {host}")
    finally:
        client.close()


def scp_directory(client: paramiko.SSHClient, local_dir: Path, remote_dir: str):
    """Copy directory contents to remote host."""
    sftp = client.open_sftp()

    try:
        # Create remote directory
        try:
            sftp.mkdir(remote_dir)
        except:
            pass

        for item in local_dir.iterdir():
            remote_path = f"{remote_dir}/{item.name}"
            if item.is_file():
                sftp.put(str(item), remote_path)
            elif item.is_dir():
                scp_directory_recursive(sftp, item, remote_path)
    finally:
        sftp.close()


def scp_directory_recursive(sftp, local_dir: Path, remote_dir: str):
    """Recursively copy directory."""
    try:
        sftp.mkdir(remote_dir)
    except:
        pass

    for item in local_dir.iterdir():
        remote_path = f"{remote_dir}/{item.name}"
        if item.is_file():
            sftp.put(str(item), remote_path)
        elif item.is_dir():
            scp_directory_recursive(sftp, item, remote_path)


def deploy_agent(
    host: str, user: str = "ian", install_path: str = "/opt/funnelcloud", *, publish_dir: Path
):
    """Deploy FunnelCloud agent to a Linux host."""
    agent_id = host.split(".")[0]
    cert_password = os.environ.get("FUNNEL_CERT_PASSWORD", "")
    allowed_clients = os.environ.get("FUNNEL_ALLOWED_CLIENT_SHA256", "")
    if not cert_password or not allowed_clients:
        raise RuntimeError(
            "Set FUNNEL_CERT_PASSWORD and FUNNEL_ALLOWED_CLIENT_SHA256 before deploying."
        )
    fingerprints = [value.strip() for value in allowed_clients.split(",")]
    if any(
        not re.fullmatch(
            r"[0-9a-fA-F]{64}", re.sub(r"(?i)^SHA256:", "", value).replace(":", "")
        )
        for value in fingerprints
    ):
        raise ValueError(
            "FUNNEL_ALLOWED_CLIENT_SHA256 contains an invalid fingerprint."
        )
    allowed_clients = ",".join(fingerprints)
    agent_pfx = CERTS / "agents" / agent_id / "agent.pfx"
    ca_cert = CERTS / "ca" / "ca.crt"
    if not agent_pfx.is_file() or not ca_cert.is_file():
        raise FileNotFoundError(
            "The agent PFX and CA certificate are required for secure deployment."
        )
    if not (publish_dir / "FunnelCloud.Agent").is_file():
        raise FileNotFoundError("A freshly published FunnelCloud agent is required.")

    print(f"\n{'='*60}")
    print(f"  Deploying FunnelCloud Agent to {host}")
    print(f"  Agent ID: {agent_id}")
    print(f"{'='*60}\n")

    # Setup SSH key first
    setup_ssh_key(host, user)

    client = get_ssh_client(host, user)

    try:
        # Stop existing service
        print("[>] Stopping existing service...")
        run_cmd(
            client, "systemctl stop funnelcloud-agent 2>/dev/null || true", sudo=True
        )
        print("    [OK] Service stopped")

        # Create staging directory
        print("[>] Creating staging directory...")
        run_cmd(client, "rm -rf /tmp/fc-staging && mkdir -m 700 /tmp/fc-staging")
        print("    [OK] Staging ready")

        # Copy agent files
        print("[>] Copying agent files (this may take a minute)...")
        scp_directory(client, publish_dir, "/tmp/fc-staging")
        print("    [OK] Files copied")

        print("[>] Copying certificates...")
        run_cmd(client, "mkdir -m 700 /tmp/fc-staging/Certificates")
        sftp = client.open_sftp()
        try:
            sftp.put(str(ca_cert), "/tmp/fc-staging/Certificates/ca.crt")
            sftp.put(str(agent_pfx), "/tmp/fc-staging/Certificates/agent.pfx")
            with sftp.open(
                "/tmp/fc-staging/Certificates/pfx-password", "w"
            ) as password_file:
                password_file.write(cert_password)
            sftp.chmod("/tmp/fc-staging/Certificates/agent.pfx", 0o600)
            sftp.chmod("/tmp/fc-staging/Certificates/pfx-password", 0o600)
        finally:
            sftp.close()
        print("    [OK] Certificates copied")

        # Create systemd service file
        service_content = f"""[Unit]
Description=FunnelCloud Agent - {agent_id}
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User={user}
WorkingDirectory={install_path}
ExecStart={install_path}/FunnelCloud.Agent
Restart=always
RestartSec=10
Environment=FUNNEL_AGENT_ID={agent_id}
Environment=FUNNEL_CERT_PATH={install_path}/Certificates/agent.pfx
LoadCredential=funnel-cert-password:{install_path}/Certificates/pfx-password
Environment=FUNNEL_CERT_PASSWORD_FILE=%d/funnel-cert-password
Environment=FUNNEL_CA_PATH={install_path}/Certificates/ca.crt
Environment=FUNNEL_ALLOWED_CLIENT_SHA256={allowed_clients}
StandardOutput=journal
StandardError=journal
SyslogIdentifier=funnelcloud-agent

[Install]
WantedBy=multi-user.target
"""
        sftp = client.open_sftp()
        with sftp.open("/tmp/fc-staging/funnelcloud-agent.service", "w") as f:
            f.write(service_content)
        sftp.close()

        # Install - run commands one at a time for better error handling
        print("[>] Installing agent...")

        # Run install commands separately to avoid quote escaping issues
        install_commands = [
            f"mkdir -p {install_path} {install_path}/logs",
            f"cp -r /tmp/fc-staging/* {install_path}/",
            f"chmod +x {install_path}/FunnelCloud.Agent",
            f"chown -R {user}:{user} {install_path}",
            f"chmod 700 {install_path}/Certificates",
            f"chmod 600 {install_path}/Certificates/agent.pfx {install_path}/Certificates/pfx-password",
            f"cp {install_path}/funnelcloud-agent.service /etc/systemd/system/",
            "systemctl daemon-reload",
            "systemctl enable funnelcloud-agent",
            "systemctl restart funnelcloud-agent",
        ]

        for cmd in install_commands:
            run_cmd(client, cmd, sudo=True)

        print("    [OK] Agent installed")

        # Cleanup
        run_cmd(client, "rm -rf /tmp/fc-staging")

        # Verify
        print("[>] Verifying...")
        import time

        time.sleep(3)
        status = run_cmd(client, "systemctl is-active funnelcloud-agent")
        if "active" in status:
            print("    [OK] Service is running!")
        else:
            print(f"    [!] Status: {status}")

        # Show recent logs
        print("\n[>] Recent logs:")
        logs = run_cmd(
            client,
            "journalctl -u funnelcloud-agent -n 5 --no-pager 2>/dev/null || echo 'No logs yet'",
            sudo=True,
        )
        for line in logs.split("\n")[-5:]:
            if line.strip():
                print(f"    {line}")

        print(f"\n{'='*60}")
        print(f"  Deployment Complete: {host}")
        print(f"{'='*60}")
        print(f"\nPorts: UDP 41420 (discovery), TCP 41235 (gRPC), TCP 41236 (HTTP)")
        print(f"\nCommands:")
        print(f"  sudo systemctl status funnelcloud-agent")
        print(f"  sudo journalctl -u funnelcloud-agent -f")

    finally:
        client.close()


def deploy_hosts(hosts: list[str]):
    with tempfile.TemporaryDirectory(prefix="funnelcloud-publish-") as directory:
        publish_dir = Path(directory)
        subprocess.run(
            [
                "dotnet",
                "publish",
                str(FUNNELCLOUD / "FunnelCloud.Agent" / "FunnelCloud.Agent.csproj"),
                "-c",
                "Release",
                "-r",
                "linux-x64",
                "--self-contained",
                "-o",
                str(publish_dir),
            ],
            check=True,
        )
        for host in hosts:
            deploy_agent(host, publish_dir=publish_dir)


def check_discovery(hosts: list[str]):
    """Check if agents are discoverable via multicast/gossip."""
    print(f"\n{'='*60}")
    print("  Checking Agent Discovery")
    print(f"{'='*60}\n")

    for host in hosts:
        agent_id = host.split(".")[0]
        try:
            import urllib.request

            url = f"http://{host}:41421/gossip"
            req = urllib.request.Request(url, method="GET")
            with urllib.request.urlopen(req, timeout=5) as resp:
                data = resp.read().decode()
                print(f"[✓] {agent_id}: Gossip endpoint responding")
                print(f"    {data[:100]}...")
        except Exception as e:
            print(f"[!] {agent_id}: Gossip not responding - {e}")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: python deploy-linux-agents.py <host1> [host2] ...")
        print("       python deploy-linux-agents.py postfix01 plex01")
        sys.exit(1)

    hosts = sys.argv[1:]

    deploy_hosts(hosts)

    # Check discovery
    check_discovery(hosts)
