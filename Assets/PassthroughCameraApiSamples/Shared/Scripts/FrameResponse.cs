// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace PassthroughCameraSamples.Shared
{
    /// <summary>
    /// Represents a server inference response for a single frame.
    ///
    /// This is the V3.0 unified response format that works across all inference modes:
    /// - Multi-Object Detection (YOLO)
    /// - Pose Estimation (KeypointRCNN)
    /// - Segmentation (YOLO-seg)
    ///
    /// Design principles:
    /// - JsonUtility compatible (no nested classes, use arrays)
    /// - All fields optional (allows different inference modes)
    /// - Server timestamps in Unix milliseconds
    /// - Timing breakdown for latency analysis
    /// </summary>
    [Serializable]
    public class FrameResponse
    {
        // ====================================================================
        // Identity (matches request)
        // ====================================================================
        public string session_id;           // Session GUID
        public int frame_id;                // Frame number

        // ====================================================================
        // Server Timing (Unix milliseconds)
        // ====================================================================
        public long server_receive_ts;      // When server received UDP packet
        public long server_process_start_ts; // When inference started (after queue wait)
        public long server_send_ts;         // When server sent UDP response

        // ====================================================================
        // Derived Server Timing (milliseconds)
        // ====================================================================
        public float queue_wait_ms;         // Time waiting in admission queue
        public float processing_time_ms;    // Inference execution time
        public float server_e2e_ms;         // Total server-side time

        // ====================================================================
        // Image Dimensions
        // ====================================================================
        public int input_image_width;       // Width of processed image
        public int input_image_height;      // Height of processed image

        // ====================================================================
        // Multi-Object Detection Results (YOLO)
        // ====================================================================
        public DetectionResult[] detections; // Bounding boxes + class IDs

        // ====================================================================
        // Pose Estimation Results (KeypointRCNN)
        // ====================================================================
        public PersonPose[] persons;        // Array of detected persons

        // ====================================================================
        // Segmentation Results (YOLO-seg)
        // ====================================================================
        public SegmentationResult segmentation; // Mask + metadata

        // ====================================================================
        // Unity-side Timing Metrics (optional, computed by Unity)
        // ====================================================================
        public float latency_ms;            // Total E2E latency (Unity send → Unity receive)
        public float upload_ms;             // Upload time (Unity → Server)
        public float download_ms;           // Download time (Server → Unity)
        public float parse_ms;              // JSON parse time (Unity side)
        public int upload_bytes_compressed; // Upload payload size (bytes)
        public int download_bytes_compressed; // Download payload size (bytes)

        // ====================================================================
        // Error Information
        // ====================================================================
        public string error;                // Error message if inference failed
        public string status;               // "success" or "error"

        // ====================================================================
        // Backward Compatibility Properties (for legacy managers)
        // ====================================================================

        /// <summary>
        /// Legacy field name for input_image_width
        /// </summary>
        public int input_width
        {
            get => input_image_width;
            set => input_image_width = value;
        }

        /// <summary>
        /// Legacy field name for input_image_height
        /// </summary>
        public int input_height
        {
            get => input_image_height;
            set => input_image_height = value;
        }

        /// <summary>
        /// Legacy wrapper for persons array (used by PoseInferenceRunManager)
        /// </summary>
        public PoseData pose
        {
            get => new PoseData { persons = this.persons };
            set => this.persons = value?.persons;
        }

        /// <summary>
        /// Check if response contains valid detection results
        /// </summary>
        public bool HasDetections()
        {
            return detections != null && detections.Length > 0;
        }

        /// <summary>
        /// Check if response contains valid pose results
        /// </summary>
        public bool HasPose()
        {
            return persons != null && persons.Length > 0;
        }

        /// <summary>
        /// Check if response contains valid segmentation results
        /// </summary>
        public bool HasSegmentation()
        {
            return segmentation != null && segmentation.mask != null;
        }
    }

    /// <summary>
    /// Single detection result (bounding box + class)
    /// </summary>
    [Serializable]
    public class DetectionResult
    {
        public int class_id;                // COCO class ID (0 = person)
        public string class_name;           // Class name (e.g., "person")
        public float confidence;            // Detection confidence [0-1]
        public float[] bbox;                // [x1, y1, x2, y2] in normalized coords [0-1]

        // Segmentation-specific fields (optional, only present in segmentation mode)
        public string mask_png_base64;      // Per-detection PNG mask (base64 encoded)
        public int[] bbox_pixels;           // Bounding box in pixel coordinates [x1, y1, x2, y2]
        public int mask_width;              // Width of the mask
        public int mask_height;             // Height of the mask
    }

    /// <summary>
    /// Pose estimation result for a single person
    /// </summary>
    [Serializable]
    public class PersonPose
    {
        public Keypoint[] keypoints;        // 17 COCO keypoints
        public float[] bbox;                // [x1, y1, x2, y2] in normalized coords [0-1]
        public float score;                 // Overall pose confidence
    }

    /// <summary>
    /// Single keypoint (joint) in COCO format
    /// </summary>
    [Serializable]
    public class Keypoint
    {
        public string name;                 // Keypoint name (e.g., "nose", "left_shoulder")
        public float x;                     // Normalized x coordinate [0-1]
        public float y;                     // Normalized y coordinate [0-1]
        public float score;                 // Keypoint confidence [0-1]
    }

    /// <summary>
    /// Segmentation result (mask + metadata)
    /// </summary>
    [Serializable]
    public class SegmentationResult
    {
        public int mask_width;              // Width of segmentation mask
        public int mask_height;             // Height of segmentation mask
        public byte[] mask;                 // Flattened mask array (0=background, 1-80=COCO classes)
        public int num_detections;          // Number of detected objects
        public int[] class_ids;             // Array of detected class IDs
        public float[] confidences;         // Array of detection confidences
    }

    /// <summary>
    /// Legacy wrapper for pose data (backward compatibility)
    /// </summary>
    [Serializable]
    public class PoseData
    {
        public PersonPose[] persons;        // Array of detected persons
    }
}
