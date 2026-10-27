#!/usr/bin/env python3
"""
Deploy AJ filter and/or action to Open-WebUI via API.

Usage:
    python scripts/deploy-filter.py              # Deploy both filter and action
    python scripts/deploy-filter.py --filter     # Deploy filter only
    python scripts/deploy-filter.py --action     # Deploy action only
    
    # Or from WSL:
    wsl -d Debian python3 /mnt/c/Code/aj.westerfield.cloud/scripts/deploy-filter.py
"""

import os
import sys
import json
import argparse
import urllib.request
import urllib.error
from pathlib import Path

# Paths
SCRIPT_DIR = Path(__file__).parent
PROJECT_ROOT = SCRIPT_DIR.parent
FILTER_PATH = PROJECT_ROOT / "filters" / "aj.filter.py"
ACTION_PATH = PROJECT_ROOT / "filters" / "training_capture.action.py"
SECRET_PATH = PROJECT_ROOT / "secrets" / "webui_admin_api_key.txt"

# Config
WEBUI_URL = os.environ.get("WEBUI_URL", "http://localhost:8180")


def get_api_key():
    """Load API key from secrets file."""
    if not SECRET_PATH.exists():
        print(f"[ERROR] API key not found: {SECRET_PATH}")
        sys.exit(1)
    
    api_key = SECRET_PATH.read_text().strip()
    if not api_key:
        print("[ERROR] API key is empty")
        sys.exit(1)
    
    return api_key


def get_headers(api_key):
    """Get HTTP headers for API requests."""
    return {
        "Authorization": f"Bearer {api_key}",
        "Content-Type": "application/json",
    }


def get_existing_functions(headers):
    """Fetch list of existing functions from Open-WebUI."""
    try:
        req = urllib.request.Request(
            f"{WEBUI_URL}/api/v1/functions/",
            headers=headers,
            method="GET"
        )
        with urllib.request.urlopen(req, timeout=10) as resp:
            return json.loads(resp.read().decode())
    except urllib.error.URLError as e:
        print(f"[ERROR] Cannot connect to Open-WebUI: {e}")
        sys.exit(1)


def deploy_function(headers, func_id, name, content, description, func_type, existing_functions):
    """Deploy a function (filter or action) to Open-WebUI."""
    print(f"\n[INFO] Deploying {func_type}: {name}")
    print(f"[INFO] Content size: {len(content)} chars")
    
    # Find existing function
    existing_id = None
    for f in existing_functions:
        if f.get("name") == name or f.get("id") == func_id:
            existing_id = f["id"]
            print(f"[OK] Found existing {func_type}: {existing_id}")
            break
    
    if existing_id:
        endpoint = f"{WEBUI_URL}/api/v1/functions/id/{existing_id}/update"
    else:
        print(f"[INFO] Creating new {func_type}...")
        endpoint = f"{WEBUI_URL}/api/v1/functions/create"
    
    # Prepare payload
    payload = json.dumps({
        "id": existing_id or func_id,
        "name": name,
        "content": content,
        "meta": {
            "description": description
        }
    }).encode("utf-8")
    
    # Deploy
    print(f"[DEPLOY] POST {endpoint}")
    try:
        req = urllib.request.Request(
            endpoint,
            data=payload,
            headers=headers,
            method="POST"
        )
        with urllib.request.urlopen(req, timeout=30) as resp:
            result = json.loads(resp.read().decode())
            print(f"[OK] {func_type.capitalize()} deployed: {result.get('name', result.get('id'))}")
            return True
    except urllib.error.HTTPError as e:
        error_body = e.read().decode() if e.fp else str(e)
        print(f"[ERROR] Deploy failed ({e.code}): {error_body}")
        return False
    except urllib.error.URLError as e:
        print(f"[ERROR] Connection failed: {e}")
        return False


def deploy_filter(headers, existing_functions):
    """Deploy the AJ filter."""
    if not FILTER_PATH.exists():
        print(f"[ERROR] Filter not found: {FILTER_PATH}")
        return False
    
    content = FILTER_PATH.read_text(encoding="utf-8")
    return deploy_function(
        headers=headers,
        func_id="aj",
        name="AJ",
        content=content,
        description="AJ - Agentic reasoning & memory filter",
        func_type="filter",
        existing_functions=existing_functions
    )


def deploy_action(headers, existing_functions):
    """Deploy the training capture action."""
    if not ACTION_PATH.exists():
        print(f"[ERROR] Action not found: {ACTION_PATH}")
        return False
    
    content = ACTION_PATH.read_text(encoding="utf-8")
    return deploy_function(
        headers=headers,
        func_id="training_capture",
        name="Capture Training Data",
        content=content,
        description="Rate and tag responses for training data capture",
        func_type="action",
        existing_functions=existing_functions
    )


def main():
    parser = argparse.ArgumentParser(description="Deploy AJ filter/action to Open-WebUI")
    parser.add_argument("--filter", action="store_true", help="Deploy filter only")
    parser.add_argument("--action", action="store_true", help="Deploy action only")
    args = parser.parse_args()
    
    # Default to both if neither specified
    deploy_filter_flag = args.filter or (not args.filter and not args.action)
    deploy_action_flag = args.action or (not args.filter and not args.action)
    
    api_key = get_api_key()
    headers = get_headers(api_key)
    
    print("[INFO] Fetching existing functions...")
    existing_functions = get_existing_functions(headers)
    
    success = True
    
    if deploy_filter_flag:
        if not deploy_filter(headers, existing_functions):
            success = False
    
    if deploy_action_flag:
        if not deploy_action(headers, existing_functions):
            success = False
    
    print(f"\n[DONE] Check: {WEBUI_URL}/admin/functions")
    
    if not success:
        sys.exit(1)


if __name__ == "__main__":
    main()
