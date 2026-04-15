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

# Skip init response
for line in init_response.iter_lines():
    if line and line.decode('utf-8').startswith('data: '):
        break

# Get editor state resource
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

print("=== Unity Editor State ===")
for line in state_response.iter_lines():
    if line:
        line_str = line.decode('utf-8')
        if line_str.startswith('data: '):
            data = json.loads(line_str[6:])
            if 'result' in data:
                result = data['result']
                if 'contents' in result:
                    for item in result['contents']:
                        if item.get('mimeType') == 'application/json':
                            state = json.loads(item['text'])
                            print(f"Is Compiling: {state.get('isCompiling')}")
                            print(f"Is Playing: {state.get('isPlaying')}")
                            print(f"Ready for Tools: {state.get('readyForTools')}")
                            print(f"Blocking Reasons: {state.get('blockingReasons')}")
                            print(f"Current Scene: {state.get('currentScene')}")
                            if state.get('compilationErrors'):
                                print(f"\nCompilation Errors:")
                                for err in state['compilationErrors']:
                                    print(f"  - {err}")
