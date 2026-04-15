import requests
import json

# Initialize MCP session
init_response = requests.post(
    'http://127.0.0.1:8080/mcp',
    headers={
        'Content-Type': 'application/json',
        'Accept': 'application/json, text/event-stream'
    },
    json={
        "jsonrpc": "2.0",
        "id": 1,
        "method": "initialize",
        "params": {
            "protocolVersion": "2024-11-05",
            "capabilities": {},
            "clientInfo": {"name": "claude", "version": "1.0"}
        }
    },
    stream=True
)

# Skip init
for line in init_response.iter_lines():
    if line and line.decode('utf-8').startswith('data: '):
        break

# Get editor state
state_response = requests.post(
    'http://127.0.0.1:8080/mcp',
    headers={
        'Content-Type': 'application/json',
        'Accept': 'application/json, text/event-stream'
    },
    json={
        "jsonrpc": "2.0",
        "id": 3,
        "method": "resources/read",
        "params": {
            "uri": "mcpforunity://editor/state"
        }
    },
    stream=True
)

print("=== Raw Response ===")
for line in state_response.iter_lines():
    if line:
        print(line.decode('utf-8'))
