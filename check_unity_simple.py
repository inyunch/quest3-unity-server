#!/usr/bin/env python3
"""
Simplified Unity console error checker using direct HTTP GET
"""
import requests
import json

# Try the direct HTTP endpoint that might exist
BASE_URL = "http://127.0.0.1:8080"

def check_unity_errors():
    print("=" * 80)
    print("UNITY COMPILATION ERROR CHECK")
    print("=" * 80)
    print()

    # Try different endpoints
    endpoints_to_try = [
        f"{BASE_URL}/api/console/errors",
        f"{BASE_URL}/console/errors",
        f"{BASE_URL}/api/errors",
        f"{BASE_URL}/errors",
        f"{BASE_URL}/api/state",
        f"{BASE_URL}/state",
        f"{BASE_URL}/health",
        f"{BASE_URL}/api/health",
    ]

    print("Trying various endpoints to find Unity MCP server...")
    print()

    for endpoint in endpoints_to_try:
        try:
            print(f"Trying: {endpoint}")
            response = requests.get(endpoint, timeout=2)
            if response.status_code == 200:
                print(f"  SUCCESS! Status {response.status_code}")
                print(f"  Response: {response.text[:500]}")
                print()
            else:
                print(f"  Status {response.status_code}")
        except requests.exceptions.RequestException as e:
            print(f"  Error: {e}")
        print()

    print("=" * 80)

if __name__ == "__main__":
    check_unity_errors()
