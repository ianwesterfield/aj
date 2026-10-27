import importlib.util
import io
from pathlib import Path
from unittest.mock import Mock

import pytest


@pytest.fixture
def deployment(monkeypatch, tmp_path):
    script = Path(__file__).resolve().parents[1] / "scripts" / "deploy-linux-agents.py"
    spec = importlib.util.spec_from_file_location("funnelcloud_deploy", script)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    monkeypatch.setattr(module, "CERTS", tmp_path)
    monkeypatch.setattr(module, "setup_ssh_key", Mock())
    module.original_get_ssh_client = module.get_ssh_client
    monkeypatch.setattr(module, "get_ssh_client", Mock())
    return module


def test_deploy_rejects_missing_or_invalid_security_configuration(
    deployment, monkeypatch, tmp_path
):
    monkeypatch.delenv("FUNNEL_CERT_PASSWORD", raising=False)
    monkeypatch.delenv("FUNNEL_ALLOWED_CLIENT_SHA256", raising=False)
    with pytest.raises(RuntimeError, match="FUNNEL_CERT_PASSWORD"):
        deployment.deploy_agent("worker-01", publish_dir=tmp_path / "publish")

    monkeypatch.setenv("FUNNEL_CERT_PASSWORD", "test-password")
    monkeypatch.setenv("FUNNEL_ALLOWED_CLIENT_SHA256", "invalid")
    with pytest.raises(ValueError, match="invalid fingerprint"):
        deployment.deploy_agent("worker-01", publish_dir=tmp_path / "publish")

    monkeypatch.setenv("FUNNEL_ALLOWED_CLIENT_SHA256", "A" * 64)
    with pytest.raises(FileNotFoundError, match="agent PFX and CA"):
        deployment.deploy_agent("worker-01", publish_dir=tmp_path / "publish")
    (tmp_path / "agents" / "worker-01").mkdir(parents=True)
    (tmp_path / "agents" / "worker-01" / "agent.pfx").write_bytes(b"test")
    (tmp_path / "ca").mkdir()
    (tmp_path / "ca" / "ca.crt").write_text("test")
    with pytest.raises(FileNotFoundError, match="freshly published"):
        deployment.deploy_agent("worker-01", publish_dir=tmp_path / "publish")
    deployment.setup_ssh_key.assert_not_called()
    deployment.get_ssh_client.assert_not_called()


def test_deploy_keeps_password_out_of_service_file(deployment, monkeypatch, tmp_path):
    password = "test-password"
    fingerprint = "A" * 64
    monkeypatch.setenv("FUNNEL_CERT_PASSWORD", password)
    monkeypatch.setenv("FUNNEL_ALLOWED_CLIENT_SHA256", fingerprint)
    (tmp_path / "agents" / "worker-01").mkdir(parents=True)
    (tmp_path / "agents" / "worker-01" / "agent.pfx").write_bytes(b"test")
    (tmp_path / "ca").mkdir()
    (tmp_path / "ca" / "ca.crt").write_text("test")
    publish_dir = tmp_path / "publish"
    publish_dir.mkdir()
    (publish_dir / "FunnelCloud.Agent").write_bytes(b"test")

    files = {}
    permissions = {}
    copied = []

    class RemoteFile(io.StringIO):
        def __init__(self, path):
            super().__init__()
            self.path = path

        def __exit__(self, *args):
            files[self.path] = self.getvalue()
            return super().__exit__(*args)

    class FakeSftp:
        def put(self, source, destination):
            copied.append((source, destination))

        def open(self, path, mode):
            return RemoteFile(path)

        def chmod(self, path, mode):
            permissions[path] = mode

        def close(self):
            pass

    client = Mock()
    client.open_sftp.return_value = FakeSftp()
    deployment.get_ssh_client.return_value = client
    monkeypatch.setattr(deployment, "scp_directory", Mock())
    monkeypatch.setattr(deployment, "run_cmd", Mock(return_value="active"))
    monkeypatch.setattr("time.sleep", lambda _: None)

    deployment.deploy_agent("worker-01", publish_dir=publish_dir)

    service = files["/tmp/fc-staging/funnelcloud-agent.service"]
    assert password not in service
    assert "LoadCredential=funnel-cert-password:" in service
    assert "FUNNEL_CERT_PASSWORD_FILE=%d/funnel-cert-password" in service
    assert f"FUNNEL_ALLOWED_CLIENT_SHA256={fingerprint}" in service
    assert files["/tmp/fc-staging/Certificates/pfx-password"] == password
    assert permissions["/tmp/fc-staging/Certificates/pfx-password"] == 0o600
    assert {Path(source).name for source, _ in copied} == {"agent.pfx", "ca.crt"}


def test_deploy_hosts_publishes_to_a_fresh_temporary_directory(deployment, monkeypatch):
    published = []

    def fake_publish(command, check):
        assert check is True
        assert command[:2] == ["dotnet", "publish"]
        published.append(Path(command[command.index("-o") + 1]))
        assert published[0].is_dir()

    deploy = Mock()
    monkeypatch.setattr(deployment.subprocess, "run", fake_publish)
    monkeypatch.setattr(deployment, "deploy_agent", deploy)
    deployment.deploy_hosts(["worker-01", "worker-02"])

    assert deploy.call_count == 2
    assert all(call.kwargs["publish_dir"] == published[0] for call in deploy.call_args_list)
    assert not published[0].exists()


def test_remote_command_failure_aborts_deployment(deployment):
    client = Mock()
    stdout = Mock()
    stdout.channel.recv_exit_status.return_value = 1
    stdout.read.return_value = b""
    stderr = Mock()
    stderr.read.return_value = b"permission denied"
    client.exec_command.return_value = (Mock(), stdout, stderr)

    with pytest.raises(RuntimeError, match="exit code 1"):
        deployment.run_cmd(client, "mkdir /restricted")


def test_ssh_rejects_unknown_host_keys(deployment, monkeypatch):
    client = Mock()
    monkeypatch.setattr(deployment.paramiko, "SSHClient", lambda: client)
    monkeypatch.setattr(deployment, "get_ssh_password", lambda: "test-password")

    deployment.original_get_ssh_client("worker-01")

    client.load_system_host_keys.assert_called_once()
    policy = client.set_missing_host_key_policy.call_args.args[0]
    assert isinstance(policy, deployment.paramiko.RejectPolicy)
