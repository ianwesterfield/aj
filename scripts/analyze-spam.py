"""Extract sender information from spam .msg files."""
import os
import re

try:
    import extract_msg
except ImportError:
    import subprocess
    subprocess.check_call(["pip", "install", "extract-msg", "-q"])
    import extract_msg

import sys
spam_dir = sys.argv[1] if len(sys.argv) > 1 else r"C:\Code\aj\spam"

print("=" * 60)
print("SPAM EMAIL ANALYSIS")
print("=" * 60)

all_ips = set()
all_domains = set()

for f in os.listdir(spam_dir):
    if not f.endswith(".msg"):
        continue
    
    path = os.path.join(spam_dir, f)
    try:
        msg = extract_msg.Message(path)
        print(f"\n--- {f[:50]} ---")
        print(f"From: {msg.sender}")
        print(f"Subject: {msg.subject[:60] if msg.subject else 'N/A'}")
        
        # Extract domain from sender
        if msg.sender and "@" in msg.sender:
            domain = msg.sender.split("@")[-1].strip(">").strip()
            all_domains.add(domain)
            print(f"Domain: {domain}")
        
        if msg.header:
            h = str(msg.header)
            
            # Extract IPs from Received headers
            ips = re.findall(r"\[(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})\]", h)
            for ip in ips:
                # Skip private/local IPs
                if not (ip.startswith("10.") or ip.startswith("192.168.") or 
                       ip.startswith("127.") or ip.startswith("172.")):
                    all_ips.add(ip)
            if ips:
                print(f"IPs in headers: {list(set(ips))}")
            
            # Return-Path
            rp = re.search(r"Return-Path:\s*<([^>]+)>", h)
            if rp:
                print(f"Return-Path: {rp.group(1)}")
                if "@" in rp.group(1):
                    all_domains.add(rp.group(1).split("@")[-1])
            
            # SPF result
            spf = re.search(r"spf=(\w+)", h)
            if spf:
                print(f"SPF: {spf.group(1)}")
            
            # DKIM result
            dkim = re.search(r"dkim=(\w+)", h)
            if dkim:
                print(f"DKIM: {dkim.group(1)}")
            
            # X-Originating-IP
            xip = re.search(r"X-Originating-IP:\s*\[?(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})", h)
            if xip:
                print(f"X-Originating-IP: {xip.group(1)}")
                all_ips.add(xip.group(1))
        
        msg.close()
    except Exception as e:
        print(f"Error processing {f}: {e}")

print("\n" + "=" * 60)
print("SUMMARY - BLOCK THESE")
print("=" * 60)

# IP Lookup
try:
    import requests
    print(f"\nIP Ownership Lookup:")
    for ip in sorted(all_ips):
        try:
            r = requests.get(f"http://ip-api.com/json/{ip}", timeout=5)
            info = r.json()
            org = info.get("org", "?")
            country = info.get("country", "?")
            asn = info.get("as", "?")
            print(f"  {ip} | {org} | {country} | {asn}")
        except:
            print(f"  {ip} | lookup failed")
except ImportError:
    print(f"\nUnique External IPs ({len(all_ips)}):")
    for ip in sorted(all_ips):
        print(f"  {ip}")

print(f"\nUnique Sender Domains ({len(all_domains)}):")
for domain in sorted(all_domains):
    print(f"  {domain}")

print("\n" + "=" * 60)
print("FIREWALLA COMMANDS")
print("=" * 60)
print("\nTo block IPs (run in Firewalla SSH):")
for ip in sorted(all_ips):
    print(f"  sudo iptables -A INPUT -s {ip} -j DROP")

print("\nOr add to Firewalla app:")
print("  Settings > Rules > Block > IP Address")
print("  Paste IPs:", ", ".join(sorted(all_ips)))
