#!/usr/bin/env python3
"""
Check Unity Editor state and read console errors via MCP
"""
import requests
import json
import time
import uuid

MCP_URL = "http://127.0.0.1:8080/mcp"
SESSION_ID = str(uuid.uuid4())

def make_mcp_request(method, params=None):
    """Make an MCP request with proper headers for SSE"""
    headers = {
        "Content-Type": "application/json",
        "Accept": "application/json, text/event-stream"
    }

    payload = {
        "jsonrpc": "2.0",
        "id": str(int(time.time() * 1000)),
        "method": method
    }

    if params:
        payload["params"] = params

    # Add session ID as query parameter
    url = f"{MCP_URL}?sessionId={SESSION_ID}"

    try:
        response = requests.post(url, json=payload, headers=headers, timeout=10, stream=True)

        # Check for error response
        if response.status_code >= 400:
            print(f"HTTP {response.status_code}: {response.text[:500]}")

        response.raise_for_status()

        # Handle SSE response
        if 'text/event-stream' in response.headers.get('Content-Type', ''):
            # Parse SSE stream
            result = None
            for line in response.iter_lines():
                if line:
                    line = line.decode('utf-8')
                    if line.startswith('data: '):
                        data = line[6:]  # Remove 'data: ' prefix
                        try:
                            result = json.loads(data)
                        except:
                            pass
            return result
        else:
            # Regular JSON response
            return response.json()
    except Exception as e:
        print(f"Error making request to {method}: {e}")
        return None

def read_resource(uri):
    """Read an MCP resource"""
    return make_mcp_request("resources/read", {"uri": uri})

def call_tool(tool_name, arguments=None):
    """Call an MCP tool"""
    params = {"name": tool_name}
    if arguments:
        params["arguments"] = arguments
    return make_mcp_request("tools/call", params)

def initialize_session():
    """Initialize MCP session"""
    print("Initializing MCP session...")
    result = make_mcp_request("initialize", {
        "protocolVersion": "2024-11-05",
        "capabilities": {},
        "clientInfo": {
            "name": "unity-mcp-checker",
            "version": "1.0"
        }
    })
    if result:
        print(f"Session initialized: {SESSION_ID}")
        return True
    else:
        print("Failed to initialize session")
        return False

def main():
    print("=" * 80)
    print("UNITY MCP CHECK - Editor State and Compilation Errors")
    print("=" * 80)
    print()

    # Initialize session
    if not initialize_session():
        print("Cannot proceed without session initialization")
        return
    print()

    # Step 1: Check editor state
    print("Step 1: Reading Editor State...")
    print("-" * 80)
    state_result = read_resource("mcpforunity://editor/state")

    if state_result and "result" in state_result:
        contents = state_result["result"].get("contents", [])
        if contents:
            state_data = json.loads(contents[0].get("text", "{}"))
            print(f"Unity Editor State:")
            print(f"  - Is Compiling: {state_data.get('is_compiling', 'Unknown')}")
            print(f"  - Ready for Tools: {state_data.get('ready_for_tools', 'Unknown')}")
            print(f"  - Is Playing: {state_data.get('is_playing', 'Unknown')}")
            print(f"  - Is Paused: {state_data.get('is_paused', 'Unknown')}")
            print(f"  - Domain Reload Pending: {state_data.get('is_domain_reload_pending', 'Unknown')}")

            blocking_reasons = state_data.get('blocking_reasons', [])
            if blocking_reasons:
                print(f"  - Blocking Reasons: {', '.join(blocking_reasons)}")

            # If compiling, wait
            if state_data.get('is_compiling'):
                print("\nWaiting for compilation to finish...")
                max_wait = 30  # seconds
                wait_time = 0
                while wait_time < max_wait:
                    time.sleep(2)
                    wait_time += 2
                    state_result = read_resource("mcpforunity://editor/state")
                    if state_result and "result" in state_result:
                        contents = state_result["result"].get("contents", [])
                        if contents:
                            state_data = json.loads(contents[0].get("text", "{}"))
                            if not state_data.get('is_compiling'):
                                print(f"Compilation finished after {wait_time} seconds")
                                break
                            print(f"  Still compiling... ({wait_time}s)")
                else:
                    print(f"  Timeout waiting for compilation after {max_wait}s")
    else:
        print("Failed to read editor state")
        if state_result and "error" in state_result:
            print(f"Error: {state_result['error']}")

    print()

    # Step 2: Read console for errors
    print("Step 2: Reading Console Errors...")
    print("-" * 80)
    console_result = call_tool("read_console", {
        "types": ["error"],
        "count": 50,
        "include_stacktrace": True
    })

    if console_result and "result" in console_result:
        content = console_result["result"].get("content", [])
        if content:
            for item in content:
                if item.get("type") == "text":
                    console_data = json.loads(item.get("text", "{}"))
                    entries = console_data.get("entries", [])

                    print(f"\nFound {len(entries)} error(s) in console:")
                    print("=" * 80)

                    if entries:
                        for i, entry in enumerate(entries, 1):
                            print(f"\n--- Error #{i} ---")
                            print(f"Message: {entry.get('message', 'N/A')}")
                            print(f"Type: {entry.get('type', 'N/A')}")
                            print(f"Mode: {entry.get('mode', 'N/A')}")
                            print(f"File: {entry.get('file', 'N/A')}")
                            print(f"Line: {entry.get('line', 'N/A')}")

                            if entry.get('stack_trace'):
                                print(f"\nStack Trace:")
                                print(entry['stack_trace'])
                            print("-" * 80)
                    else:
                        print("\nNo compilation errors found!")
    else:
        print("Failed to read console")
        if console_result and "error" in console_result:
            print(f"Error: {console_result['error']}")

    print("\n" + "=" * 80)
    print("Check complete")
    print("=" * 80)

if __name__ == "__main__":
    main()
