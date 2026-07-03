# System Architecture Diagram

Paste the code block below into [Mermaid Live Editor](https://mermaid.live) to render.

```mermaid
flowchart TD

    %% ─────────────────────────────────────────────
    %% VR CLIENT
    %% ─────────────────────────────────────────────
    subgraph QUEST["🥽  Meta Quest 3 — Unity 6"]
        direction TB

        subgraph CAPTURE["Frame Capture"]
            CAM["PassthroughCameraAccess\nTexture2D  1280 × 960"]
            ENC["EncodeToJPG\nquality = 80\n20–50 KB JPEG"]
            CAM --> ENC
        end

        subgraph SCHEDULER["Fixed-Cadence Scheduler"]
            SCHED["PoseInferenceRunManager.Update\ntargetFPS = 10  →  interval = 100 ms\nt_next = t_now + 1 / targetFPS\n(computed BEFORE dispatch)"]
        end

        subgraph XPORT["UDPTransportManager"]
            SEND["UDPTransport.SendFrame\n━━━ 70-byte header ━━━\nmagic 0xF2AE1234  |  session_id 16B\nframe_id 4B  |  unity_send_ts 8B\npayload_len 4B  |  SHA-256 32B\ntelemetry_len 2B\n━━━ JSON telemetry ━━━\n━━━ JPEG payload ━━━\n→ UDP port 8002"]
            HBEAT["HeartbeatLoop\n9-byte HEARTBEAT every 2 s\nNAT hole-punching\n(5G hotspot support)"]
            RECV["ReceiveLoop  background thread\nlisten on port 8003\nResponse Queue  max = 10"]
        end

        subgraph TELEMETRY["Telemetry"]
            FTT["FrameTelemetryTracker\nframe_id → FrameTrace\nPending → Completed → Displayed\n                    ↘ Dropped\n                    ↘ Failed\nlock per state transition"]
            CSV["LocalTelemetryWriter\n39-column CSV\nFlush after every row\n/sdcard/.../telemetry_session_ts.csv"]
            FTT --> CSV
        end

        HUD["SharedInferenceHUD\nE2E latency  queue_wait  FPS\ndrop count  freeze ratio"]
        AR["AR Overlay Renderer\nSkeleton / BBox / Mask"]
    end

    %% ─────────────────────────────────────────────
    %% NETWORK BOUNDARY
    %% ─────────────────────────────────────────────
    NET_UP(["━━━  5 GHz 802.11ac  ━━━\nUDP :8002  frames  ↑"])
    NET_DN(["━━━  5 GHz 802.11ac  ━━━\nUDP :8003  results  ↓"])
    NET_HTTP(["- -  HTTP :8001  - -\npoll fallback  ↕"])

    %% ─────────────────────────────────────────────
    %% INFERENCE SERVER
    %% ─────────────────────────────────────────────
    subgraph SERVER["🖥️  Vision Server — FastAPI + Uvicorn  port 8001"]
        direction TB

        subgraph INGEST["UDPFrameIngest  asyncio  :8002"]
            direction LR
            I1["① magic\n0xF2AE1234"]
            I2["② SHA-256\nverify"]
            I3["③ dedup cache\nTTL = 30 s\nsession+frame_id key"]
            I4["④ server_receive_ts\n= time.time × 1000"]
            I5["⑤ frame-gap tracker\nUDP packet-loss detection\ngaps logged per session"]
            I1 --> I2 --> I3 --> I4 --> I5
        end

        subgraph BAQ_BOX["BoundedAdmissionQueue"]
            direction LR
            AQ["max_pending = 6\nFIFO  drop-oldest policy\nasyncio.Queue + asyncio.Lock"]
            DROPPED["DroppedRequest\ndrop_reason =\nQueueFullOldestDiscarded\nlogged to CSV via Unity"]
        end

        subgraph WORKER_BOX["UDPInferenceWorker  asyncio loop"]
            WL["await queue.get_next\nqueue_wait_ms =\nproc_start_ts − recv_ts"]
            MGR["InferenceManager\nrun_inference context\nserver_process_start_ts"]
            WL --> MGR
        end

        subgraph GPU_BOX["GPU Inference — CUDA"]
            direction LR
            DET["DetectionProcessor\nYOLOv8n  ultralytics\nyolo_model.predict\ndevice='cuda'\nclasses=[0] person-only"]
            POSE["PoseProcessor\nKeypoint R-CNN\ntorchvision\nimg_tensor.to 'cuda'\ntorch.no_grad"]
            SEG["SegmentationProcessor\nYOLOv11n-seg\nultralytics\ndownsample factor\ndevice='cuda'"]
            MNT["MoveNet Thunder\npose_v2 mode\nlazy-loaded\nmodel registry\nlower threshold 0.3"]
        end

        subgraph GPUSCHED_BOX["GPU Scheduling Controls"]
            direction TB
            AFF["Worker–GPU Affinity\nassigned_gpu = pid mod gpu_count\nWORKER_GPU_ID env var\ndeterministic per-PID"]
            TPL["CPU Thread Pool\nOMP_NUM_THREADS\nMKL_NUM_THREADS\nBLAS threads\n= max(4, min(16, ncpu div 8))\ntorch.set_num_threads\ntorch.set_num_interop_threads"]
            WU["Startup GPU Warmup\n2x dummy inference per model\nCUDA context pre-init\nVRAM pre-load\ntarget: 10–50 ms steady state"]
            MON["gpu_monitor  pynvml\nnvmlDeviceGetClockInfo\nnvmlDeviceGetUtilizationRates\nnvmlDeviceGetPowerUsage\nthrottle flag if clock < 1500 MHz"]
        end

        subgraph RESP_BOX["Response Delivery"]
            RC["ResultCache\nTTL = 30 s  max = 1000\nasyncio cleanup every 10 s\nHTTP polling fallback store"]
            PUSH["UDP sendto client :8003\nserver_send_ts stamped\njust before send\n1–5 KB JSON response"]
        end

        HTTP_EP["FastAPI HTTP :8001\nGET /response/session/frame  poll\nGET /health\nGET /udp_stats\nGET /frame_loss_analysis"]
    end

    %% ─────────────────────────────────────────────
    %% MAIN DATA FLOW
    %% ─────────────────────────────────────────────

    ENC --> SCHED --> SEND
    SEND -->|"unity_send_ts  t₁"| NET_UP
    NET_UP -->|"server_receive_ts  t₂"| I1

    I5 -->|"enqueue"| AQ
    AQ -->|"admitted"| WL
    AQ -->|"queue full"| DROPPED

    MGR -->|"mode = detection"| DET
    MGR -->|"mode = pose"| POSE
    MGR -->|"mode = both\nstep 1"| DET
    DET -->|"crops\nstep 2"| POSE
    MGR -->|"mode = segmentation"| SEG
    MGR -->|"mode = pose_v2"| MNT

    DET --> RC
    POSE --> RC
    SEG --> RC
    MNT --> RC

    DET --> PUSH
    POSE --> PUSH
    SEG --> PUSH
    MNT --> PUSH

    PUSH -->|"server_send_ts  t₃"| NET_DN
    NET_DN -->|"unity_receive_ts  t₄"| RECV

    RECV --> FTT
    RECV --> AR
    RECV --> HUD

    RC --> HTTP_EP
    HTTP_EP -->|"fallback"| NET_HTTP
    NET_HTTP -->|"unity_receive_ts  t₄"| RECV

    HBEAT -.->|"keepalive\ndatagram"| NET_UP

    AFF --> MGR
    TPL --> MGR
    WU --> MGR
    MON -.->|"clock throttle\ndetection"| GPUSCHED_BOX

    %% ─────────────────────────────────────────────
    %% LATENCY BREAKDOWN ANNOTATION
    %% ─────────────────────────────────────────────
    subgraph LATENCY["E2E Latency Decomposition"]
        direction LR
        LA["L_upload\nt₂ − t₁\n~10–50 ms"]
        LB["L_queue\nqueue_wait_ms\nt_proc_start − t₂\n~0–100 ms"]
        LC["L_inference\nprocessing_time_ms\nt_proc_end − t_proc_start\n~10–300 ms"]
        LD["L_download\n~10–50 ms"]
        LE["L_parse\nparse_ms\n~5–20 ms"]
        LA --> LB --> LC --> LD --> LE
    end

    %% ─────────────────────────────────────────────
    %% STYLING
    %% ─────────────────────────────────────────────
    classDef quest fill:#1a1a2e,color:#e0e0ff,stroke:#5555cc,stroke-width:2px
    classDef server fill:#0d2b1a,color:#d0ffd0,stroke:#22aa55,stroke-width:2px
    classDef network fill:#2b1a00,color:#ffe0a0,stroke:#cc8800,stroke-width:2px
    classDef gpu fill:#2b0011,color:#ffccdd,stroke:#cc2244,stroke-width:2px
    classDef sched fill:#1a2b00,color:#ddffaa,stroke:#88cc00,stroke-width:2px
    classDef latency fill:#1a1a1a,color:#aaaaaa,stroke:#555555,stroke-width:1px

    class CAM,ENC,SCHED,SEND,HBEAT,RECV,FTT,CSV,HUD,AR quest
    class I1,I2,I3,I4,I5,AQ,DROPPED,WL,MGR,RC,PUSH,HTTP_EP server
    class DET,POSE,SEG,MNT gpu
    class AFF,TPL,WU,MON sched
    class NET_UP,NET_DN,NET_HTTP network
    class LA,LB,LC,LD,LE latency
```

---

## V3.0 Detailed Logic Flow

> Paste into [Mermaid Live Editor](https://mermaid.live) — use as **Fig. 2** in paper (detail view of latency critical path).

```mermaid
graph TB

    %% ─────────────────────────────────────────────
    %% UPDATE LOOP
    %% ─────────────────────────────────────────────
    subgraph LOOP["Unity Update() — called every rendered frame ~60 Hz"]
        A["Update() entry"] --> B{"useServerInference\n&& useUDPTransport?"}

        B -->|"Yes (V3.0)"| C["TryGetResponse()\nDrain response queue\n(non-blocking)"]
        B -->|"No (local)"| LOC["RunInference()\nLocal Sentis path\nConvert → Tensor → Engine\n→ NMS → DrawUIBoxes"]

        C --> D{"Response\nin queue?"}
        D -->|"Yes"| E["HandleV3Response(response)"]
        D -->|"No"| F{"time >=\nm_nextInferenceTime?"}
        E --> F

        F -->|"Yes — send next frame"| G["Compute cadence BEFORE dispatch\nm_nextInferenceTime =\nnow + 1 / targetFPS\n(decouples send rate from RTT)"]
        G --> H["StartCoroutine\nRunInferenceNonBlocking()"]
        F -->|"No"| Z["End frame\n(render continues at 60 Hz)"]
        H --> Z
        LOC --> Z
    end

    %% ─────────────────────────────────────────────
    %% NON-BLOCKING SEND
    %% ─────────────────────────────────────────────
    subgraph SEND["RunInferenceNonBlocking() — fire-and-forget coroutine"]
        S1["Get Texture\ncameraAccess.GetTexture()"] --> S2["EncodeTextureToJPEG\nquality=80  20–50 KB\nOptional downsample"]
        S2 --> S3["m_frameId++\nm_telemetry.CreateFrame()\nFrameTrace  state=Pending\nupload_bytes = jpegData.Length"]
        S3 --> S4["Build telemetry JSON\n{mode, scene}"]
        S4 --> S5["m_transport.SendFrame(trace, jpeg, json)\n━━ UDP datagram ━━\n70B header: magic | session_id | frame_id\n| unity_send_ts (t₁) | SHA-256 | tlen\nVariable: JSON + JPEG\n→ server port 8002"]
        S5 --> S6["Coroutine returns\nNO yield — main thread unblocked\nResponse arrives asynchronously"]
    end

    %% ─────────────────────────────────────────────
    %% BACKGROUND UDP RECV (background thread)
    %% ─────────────────────────────────────────────
    subgraph RECV["UDPTransportManager — background ReceiveThread"]
        R1["Socket.Receive()\nlisten on port 8003"] --> R2["Parse JSON response\nExtract frame_id, server timing fields"]
        R2 --> R3["Enqueue to\nConcurrentQueue<FrameResponse>\nmax = 10"]
        R4["HeartbeatThread\n9-byte HEARTBEAT every 2 s\nNAT hole-punching (5G hotspot)"]
    end

    %% ─────────────────────────────────────────────
    %% RESPONSE HANDLING
    %% ─────────────────────────────────────────────
    subgraph HANDLE["HandleV3Response(response)"]
        H1["MarkFrameCompleted(frame_id, response)\n━━ Timing calculation ━━\nunity_receive_ts = now  (t₄)\ne2e_ms = t₄ − t₁  (unity_send_ts)\nserver_e2e_ms = proc_end − recv_ts\nnetworkMs = e2e_ms − server_e2e_ms\nupload_ms ≈ networkMs / 2\ndownload_ms ≈ networkMs / 2\nparse_ms from JSON deserialization"]
        H1 --> H2["DisplayV3Frame(response)\nScale bbox: cameraRes → modelRes\n(scaleX = modelW / imgW)\nDrawUIBoxes() → AR overlay"]
        H2 --> H3["UpdateMetricsDisplay(response)\nm_sharedHUD.UpdateMetrics(response)\nm_inferenceHUD.UpdateHUD(...)"]
        H3 --> H4["MarkFrameDisplayed(frame_id)\nMark older pending frames → Dropped\nfreeze_frames = frameId − lastDisplayed − 1\nWriteLocalTelemetry → CSV flush"]
    end

    %% ─────────────────────────────────────────────
    %% HUD
    %% ─────────────────────────────────────────────
    subgraph HUD["SharedInferenceHUD — Update() every frame"]
        U1["Store latest metrics\ne2eMs, uploadMs, serverProcMs\ndownloadMs, parseMs, detections"] --> U2["Calculate inference FPS\n1000 / e2eMs\nRolling avg (10 samples)"]
        U2 --> U3["Format text\nFPS | E2E | breakdown\nObjects | Upload KB | Drop count"]
        U3 --> U4["TextMeshProUGUI.text = …"]
    end

    %% ─────────────────────────────────────────────
    %% FRAME STATE MACHINE
    %% ─────────────────────────────────────────────
    subgraph FSM["FrameTelemetryTracker — Frame Lifecycle"]
        direction LR
        FS1["Pending\nt₁ = unity_send_ts"] --> FS2["Completed\nt₄ = unity_receive_ts"]
        FS2 --> FS3["Displayed\n→ CSV row written"]
        FS2 --> FS4["Dropped\n(superseded by newer frame)\n→ CSV row written"]
        FS1 --> FS5["Failed\n(error / timeout)\n→ CSV row written"]
    end

    %% ─────────────────────────────────────────────
    %% LATENCY BREAKDOWN
    %% ─────────────────────────────────────────────
    subgraph LAT["E2E Latency Decomposition  L_e2e = L_up + L_queue + L_inf + L_down + L_parse"]
        direction LR
        LA["L_upload\nt₂ − t₁\n~10–50 ms\nJPEG encode + UDP send"]
        LB["L_queue\nqueue_wait_ms\nt_proc_start − t₂\n~0–50 ms\nBounded queue wait"]
        LC["L_inference\nprocessing_time_ms\nt_proc_end − t_proc_start\n~10–300 ms\nGPU model forward pass"]
        LD["L_download\n≈ L_net / 2\n~10–50 ms\nUDP response push"]
        LE["L_parse\nparse_ms\n~2–20 ms\nJSON deserialize"]
        LA --> LB --> LC --> LD --> LE
    end

    %% ─────────────────────────────────────────────
    %% CONNECTIONS
    %% ─────────────────────────────────────────────
    H --> S1
    S5 -.->|"UDP :8002"| R1
    R3 -.->|"TryGetResponse()"| C
    E --> H1
    H3 --> U1
    H4 --> FS3

    %% ─────────────────────────────────────────────
    %% STYLING
    %% ─────────────────────────────────────────────
    classDef unity fill:#1a1a2e,color:#e0e0ff,stroke:#5555cc,stroke-width:2px
    classDef send  fill:#002b1a,color:#d0ffd0,stroke:#22aa55,stroke-width:2px
    classDef recv  fill:#2b1a00,color:#ffe0a0,stroke:#cc8800,stroke-width:2px
    classDef hud   fill:#1a001a,color:#ffccff,stroke:#aa22aa,stroke-width:2px
    classDef fsm   fill:#0a0a0a,color:#cccccc,stroke:#555555,stroke-width:1px
    classDef lat   fill:#1a1a1a,color:#aaaaaa,stroke:#444444,stroke-width:1px

    class A,B,C,D,F,G,H,Z,LOC unity
    class S1,S2,S3,S4,S5,S6 send
    class R1,R2,R3,R4 recv
    class H1,H2,H3,H4 unity
    class U1,U2,U3,U4 hud
    class FS1,FS2,FS3,FS4,FS5 fsm
    class LA,LB,LC,LD,LE lat
```

---

## Component Quick Reference

| Layer | Component | Role |
|-------|-----------|------|
| VR | `PassthroughCameraAccess` | Capture 1280×960 passthrough frames |
| VR | `UDPTransport.SendFrame()` | Binary protocol, 70B header + SHA-256 |
| VR | `UDPTransportManager` | Background send/recv threads, NAT keepalive |
| VR | `FrameTelemetryTracker` | Frame state machine, 39-col CSV writer |
| VR | `SharedInferenceHUD` | Real-time latency / drop display |
| Net | UDP :8002 | Frame upload (non-blocking fire-and-forget) |
| Net | UDP :8003 | Result push-back (server-initiated) |
| Net | HTTP :8001 | Poll fallback (`GET /response/{session}/{frame}`) |
| Server | `UDPFrameIngest` | Magic + SHA-256 + dedup + receive timestamp |
| Server | `BoundedAdmissionQueue` | max_pending=6, FIFO drop-oldest |
| Server | `UDPInferenceWorker` | Single asyncio loop, measures `queue_wait_ms` |
| Server | `InferenceManager` | Mode routing to GPU processors |
| GPU | `DetectionProcessor` | YOLOv8n, `device='cuda'`, person-only |
| GPU | `PoseProcessor` | Keypoint R-CNN, torchvision, crops |
| GPU | `SegmentationProcessor` | YOLOv11n-seg, downsampled masks |
| GPU | MoveNet Thunder | `pose_v2`, lazy-loaded, lower threshold |
| Sched | `pid % gpu_count` | Deterministic worker–GPU affinity |
| Sched | `OMP/MKL_NUM_THREADS` | CPU thread-pool bound per worker |
| Sched | `gpu_monitor` (pynvml) | Clock MHz / util% / power W / throttle flag |
| Server | `ResultCache` | TTL=30s, HTTP fallback store |
