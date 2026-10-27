"""Update postfix spam blocking rules on postfix01."""

import paramiko
import sys

# Read password from secrets
with open(r"C:\Code\aj\secrets\ssh.txt") as f:
    password = f.read().strip()

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect("postfix01", username="ian", password=password)

# New domains to block from recent spam analysis (April 2026)
new_sender_access = """
# Japanese phishing domains - added 2026-04-06
teknovaria.com          REJECT Spam domain blocked
scaliebe.com            REJECT Spam domain blocked
yaoankang.com           REJECT Spam domain blocked
cnt-wendingyule.com     REJECT Spam domain blocked
novareturns.com         REJECT Spam domain blocked
michianaworkout.com     REJECT Spam domain blocked
fxyh2022.com            REJECT Spam domain blocked
"""

# Append to sender_access
print("Adding domains to sender_access...")
stdin, stdout, stderr = ssh.exec_command(
    f'echo "{new_sender_access}" | sudo -S tee -a /etc/postfix/sender_access'
)
stdin.write(password + "\n")
stdin.flush()
out = stdout.read().decode()
err = stderr.read().decode()
if "REJECT" in out:
    print("  SUCCESS: Added new domains")
else:
    print(f"  Output: {out}")
    print(f"  Error: {err}")

# Also add header checks for Japanese subject patterns
new_header_checks = r"""
# Block Japanese phishing patterns - added 2026-04-06
/^From:.*@.*update[a-z]{2}\./i REJECT Spam pattern blocked
/^From:.*@.*service[a-z]{2,3}\./i REJECT Spam pattern blocked
/^From:.*@.*mail[0-9]+\./i REJECT Spam pattern blocked
/^From:.*@.*system[a-z]{2}\./i REJECT Spam pattern blocked
"""

print("Adding patterns to header_checks...")
stdin, stdout, stderr = ssh.exec_command(
    f'echo "{new_header_checks}" | sudo -S tee -a /etc/postfix/header_checks'
)
stdin.write(password + "\n")
stdin.flush()
out = stdout.read().decode()
err = stderr.read().decode()
if "REJECT" in out:
    print("  SUCCESS: Added new patterns")
else:
    print(f"  Output: {out}")
    print(f"  Error: {err}")

# Rebuild the hash and reload postfix
print("Rebuilding sender_access hash...")
stdin, stdout, stderr = ssh.exec_command("sudo -S postmap /etc/postfix/sender_access")
stdin.write(password + "\n")
stdin.flush()
stdout.read()
stderr.read()
print("  Done")

print("Reloading postfix...")
stdin, stdout, stderr = ssh.exec_command("sudo -S postfix reload")
stdin.write(password + "\n")
stdin.flush()
out = stdout.read().decode()
err = stderr.read().decode()
print(f"  {err.strip() or out.strip() or 'Done'}")

# Verify
print("\nVerifying new rules in sender_access:")
stdin, stdout, stderr = ssh.exec_command("tail -10 /etc/postfix/sender_access")
print(stdout.read().decode())

ssh.close()
print("Postfix spam rules updated!")
