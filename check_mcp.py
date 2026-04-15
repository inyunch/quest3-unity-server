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

# Parse SSE response
session_id = None
for line in init_response.iter_lines():
    if line:
        line_str = line.decode('utf-8')
        if line_str.startswith('data: '):
            data = json.loads(line_str[6:])
            print("Initialized:", data.get('result', {}).get('serverInfo'))
            break

# Read console
console_response = requests.post(
    'http://127.0.0.1:8080/mcp',
    headers={
        'Content-Type': 'application/json',
        'Accept': 'application/json, text/event-stream'
    },
    json={
        "jsonrpc": "2.0",
        "id": 2,
        "method": "tools/call",
        "params": {
            "name": "read_console",
            "arguments": {
                "action": "get",
                "types": ["error", "warning"],
                "count": 30,
                "format": "detailed",
                "include_stacktrace": True
            }
        }
    },
    stream=True
)

print("\n=== Unity Console Errors ===")
for line in console_response.iter_lines():
    if line:
        line_str = line.decode('utf-8')
        if line_str.startswith('data: '):
            data = json.loads(line_str[6:])
            if 'result' in data:
                result = data['result']
                if 'content' in result:
                    for item in result['content']:
                        if item.get('type') == 'text':
                            print(item['text'])
