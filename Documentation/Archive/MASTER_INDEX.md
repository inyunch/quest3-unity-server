# Master Documentation Index

**Last Updated**: 2026-04-14
**Status**: Active Development

## 🚨 START HERE - Critical Issues

### Current Known Issues
1. **"Completed" State in Excel** - [Troubleshooting/TELEMETRY_ROOT_CAUSE_ANALYSIS.md](Troubleshooting/TELEMETRY_ROOT_CAUSE_ANALYSIS.md)
   - **Status**: Root cause identified, patches ready
   - **Impact**: Final Excel summary contains intermediate states
   - **Priority**: CRITICAL

2. **Segmentation Telemetry Broken** - [Troubleshooting/TELEMETRY_STATUS_REPORT.md](Troubleshooting/TELEMETRY_STATUS_REPORT.md)
   - **Status**: All timestamps missing (0%)
   - **Impact**: No telemetry data for Segmentation mode
   - **Priority**: HIGH

3. **PoseEstimation Server Timestamps Missing** - [Troubleshooting/TELEMETRY_STATUS_REPORT.md](Troubleshooting/TELEMETRY_STATUS_REPORT.md)
   - **Status**: Unity timestamps OK, server timestamps NaN
   - **Impact**: Incomplete telemetry for PoseEstimation
   - **Priority**: MEDIUM

## 📁 Documentation Structure

### Troubleshooting/ (Current Issues & Fixes)
**Purpose**: Diagnose and fix current problems

| Document | Status | Description |
|----------|--------|-------------|
| [TELEMETRY_ROOT_CAUSE_ANALYSIS.md](Troubleshooting/TELEMETRY_ROOT_CAUSE_ANALYSIS.md) | ✅ CURRENT | Complete root cause analysis with patch plan |
| [TELEMETRY_STATUS_REPORT.md](Troubleshooting/TELEMETRY_STATUS_REPORT.md) | ✅ CURRENT | Per-mode telemetry validation results |
| [COMPILATION_FIXES_SUMMARY.md](Troubleshooting/COMPILATION_FIXES_SUMMARY.md) | ✅ ACTIVE | Unity compilation error fixes |
| [SERVER_LOGGING_FIX.md](Troubleshooting/SERVER_LOGGING_FIX.md) | ✅ ACTIVE | Server logging bug fixes |
| [FRAME_TRACKING_DIAGNOSIS.md](Troubleshooting/FRAME_TRACKING_DIAGNOSIS.md) | 📋 REFERENCE | Frame tracking diagnostics |

### Implementation/ (What's Been Built)
**Purpose**: Document completed implementations

| Document | Status | Description |
|----------|--------|-------------|
| [PARALLEL_PROCESSING_IMPLEMENTATION_COMPLETE.md](Implementation/PARALLEL_PROCESSING_IMPLEMENTATION_COMPLETE.md) | ✅ COMPLETE | Parallel processing migration |
| [TELEMETRY_PIPELINE_IMPLEMENTATION_COMPLETE.md](Implementation/TELEMETRY_PIPELINE_IMPLEMENTATION_COMPLETE.md) | ⚠️  PARTIAL | Delayed telemetry implementation |
| [QUEUE_BASED_TELEMETRY_IMPLEMENTATION.md](Implementation/QUEUE_BASED_TELEMETRY_IMPLEMENTATION.md) | ❌ REVERTED | Queue-based telemetry (user reverted code) |
| [SEGMENTATION_EXCEL_LOGGING_COMPLETE.md](Implementation/SEGMENTATION_EXCEL_LOGGING_COMPLETE.md) | ✅ COMPLETE | Segmentation Excel logging |

### Architecture/ (How It Works)
**Purpose**: System design and architecture

| Document | Status | Description |
|----------|--------|-------------|
| [SERVER_PARALLEL_COMPATIBILITY_REPORT.md](Architecture/SERVER_PARALLEL_COMPATIBILITY_REPORT.md) | ✅ ACTIVE | Server parallel processing architecture |

**TODO**: Create consolidated architecture docs:
- PARALLEL_PROCESSING_ARCHITECTURE.md
- TELEMETRY_ARCHITECTURE.md

### Testing/ (How to Test)
**Purpose**: Test procedures and validation

| Document | Status | Description |
|----------|--------|-------------|
| [QUICK_TEST_GUIDE.md](Testing/QUICK_TEST_GUIDE.md) | ✅ ACTIVE | Quick testing procedures |
| [TESTING_STATUS.md](Testing/TESTING_STATUS.md) | ✅ CURRENT | Latest test results |
| [DEPLOYMENT_CHECKLIST.md](Testing/DEPLOYMENT_CHECKLIST.md) | ✅ ACTIVE | Pre-deployment checks |

### Archive/ (Old/Superseded Docs)
**Purpose**: Historical reference

Contains outdated proposals, old metric explanations, and superseded implementations.
See [Archive/README.md](Archive/README.md) for details.

## 🎯 Quick Navigation by Task

### "I need to understand what's broken"
1. Read [Troubleshooting/TELEMETRY_ROOT_CAUSE_ANALYSIS.md](Troubleshooting/TELEMETRY_ROOT_CAUSE_ANALYSIS.md)
2. Check [Troubleshooting/TELEMETRY_STATUS_REPORT.md](Troubleshooting/TELEMETRY_STATUS_REPORT.md)

### "I need to fix the issues"
1. Follow patch plan in [Troubleshooting/TELEMETRY_ROOT_CAUSE_ANALYSIS.md](Troubleshooting/TELEMETRY_ROOT_CAUSE_ANALYSIS.md)
2. Verify fixes with [Testing/QUICK_TEST_GUIDE.md](Testing/QUICK_TEST_GUIDE.md)

### "I need to understand the architecture"
1. Start with [Architecture/SERVER_PARALLEL_COMPATIBILITY_REPORT.md](Architecture/SERVER_PARALLEL_COMPATIBILITY_REPORT.md)
2. Read implementation docs in [Implementation/](Implementation/)

### "I need to test changes"
1. Follow [Testing/QUICK_TEST_GUIDE.md](Testing/QUICK_TEST_GUIDE.md)
2. Check results against [Testing/TESTING_STATUS.md](Testing/TESTING_STATUS.md)

## 📊 Implementation Status Summary

### ✅ Completed
- Parallel processing infrastructure (Unity + Server)
- Basic delayed telemetry pipeline
- Excel logging for all 3 modes
- Frame state tracking (Pending/Completed/Displayed/Dropped/Failed)

### ⚠️  Partially Working
- MultiObjectDetection: 100% telemetry ✅
- PoseEstimation: Missing server timestamps ⚠️
- Segmentation: All telemetry broken ❌

### 🔧 Needs Fixing
- "Completed" state appearing in final Excel
- Segmentation telemetry completely broken
- PoseEstimation server timestamp parsing

### ❌ Reverted/Abandoned
- Queue-based telemetry (user reverted to single variable)

## 🔗 External Resources

### Server Documentation
Located in: `C:\Repo\Github\vision_server\Documentation\`

### Unity Project Root
Main README: `../README.md`

## 📝 Document Maintenance

### Update Frequency
- **Troubleshooting**: Update immediately when issues change
- **Implementation**: Update when implementations complete
- **Testing**: Update after each test run
- **Architecture**: Update on major design changes

### Naming Conventions
- Use UPPERCASE for document titles
- Use descriptive names (not generic like DOC1.md)
- Include status in name where appropriate (e.g., *_COMPLETE.md)
- Date-stamp superseded docs before archiving

### Archival Policy
- Move to Archive/ when superseded
- Keep for historical reference
- Add "ARCHIVED - " prefix to title
- Update Archive/README.md index
