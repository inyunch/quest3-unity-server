# Documentation Organization Plan

**Date**: 2026-04-14

## Current Status
Total markdown files in root: 28
Need to organize into proper structure

## Organization Rules

### Keep in Root
- README.md (main project readme)
- CONTRIBUTING.md (contribution guidelines)
- CODE_OF_CONDUCT.md (community standards)
- CLAUDE.md (AI collaboration info)

### Move to Documentation/Architecture/
- PARALLEL_PROCESSING_ARCHITECTURE.md (consolidated from multiple parallel docs)
- TELEMETRY_ARCHITECTURE.md (consolidated from telemetry docs)
- SERVER_PARALLEL_COMPATIBILITY_REPORT.md

### Move to Documentation/Implementation/
- IMPLEMENTATION_STATUS.md (current status summary)
- PARALLEL_PROCESSING_IMPLEMENTATION_COMPLETE.md
- TELEMETRY_PIPELINE_IMPLEMENTATION_COMPLETE.md
- QUEUE_BASED_TELEMETRY_IMPLEMENTATION.md
- SEGMENTATION_EXCEL_LOGGING_COMPLETE.md

### Move to Documentation/Troubleshooting/
- TELEMETRY_ROOT_CAUSE_ANALYSIS.md (CURRENT - most important)
- TELEMETRY_STATUS_REPORT.md (CURRENT - diagnostic results)
- KNOWN_ISSUES.md (to be created - consolidate issues)
- COMPILATION_FIXES_SUMMARY.md
- SERVER_LOGGING_FIX.md
- FRAME_TRACKING_DIAGNOSIS.md

### Move to Documentation/Testing/
- TESTING_GUIDE.md (consolidated from QUICK_TEST_GUIDE + TESTING_GUIDE)
- TESTING_STATUS.md
- DEPLOYMENT_CHECKLIST.md

### Move to Documentation/Archive/
- PARALLEL_MIGRATION_PROPOSAL.md (superseded by implementation)
- PARALLEL_PROCESSING_PROPOSAL.md (superseded by implementation)
- TELEMETRY_DIAGNOSTIC_RESULTS.md (superseded by root cause analysis)
- TELEMETRY_ZERO_FIELDS_ROOT_CAUSE.md (superseded by root cause analysis)
- FINAL_STATE_ANALYSIS.md (superseded by root cause analysis)
- E2E_CALCULATION_DETAILED_ANALYSIS.md (old metrics explanation)
- EXCEL_METRICS_EXPLANATION.md (old metrics)
- LATENCY_BREAKDOWN_EXPLANATION.md (old metrics)
- DROP_FRAME_EXPLANATION.md (old implementation)
- DROP_FRAME_RESET_SOLUTION.md (old implementation)
- DROP_VS_FREEZE_DETAILED.md (old metrics)

### Delete (fully superseded/outdated)
- PARALLEL_PROCESSING_COMPLETE.md (duplicate of IMPLEMENTATION_COMPLETE)
- QUICK_TEST_GUIDE.md (merged into TESTING_GUIDE.md)

## Consolidation Targets

### 1. PARALLEL_PROCESSING_ARCHITECTURE.md
Consolidate from:
- SERVER_PARALLEL_COMPATIBILITY_REPORT.md (architecture section)
- PARALLEL_PROCESSING_IMPLEMENTATION_COMPLETE.md (architecture section)

### 2. TELEMETRY_ARCHITECTURE.md
Consolidate from:
- TELEMETRY_PIPELINE_IMPLEMENTATION_COMPLETE.md (architecture section)
- QUEUE_BASED_TELEMETRY_IMPLEMENTATION.md (architecture section)

### 3. IMPLEMENTATION_STATUS.md
Create new summary from:
- Current state of all implementations
- Links to detailed implementation docs

### 4. KNOWN_ISSUES.md
Create from:
- TELEMETRY_ROOT_CAUSE_ANALYSIS.md (issues section)
- TELEMETRY_STATUS_REPORT.md (issues section)

### 5. TESTING_GUIDE.md
Consolidate from:
- QUICK_TEST_GUIDE.md
- TESTING_GUIDE.md (if exists)
- TESTING_STATUS.md (current test results)
