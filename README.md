# ThingsGateway (Custom Enhanced Fork)

> **Note**: This repository is a custom fork of [ThingsGateway](https://github.com/ThingsGateway/ThingsGateway) featuring asynchronous read-write lock optimizations for high-concurrency device communication.

---

## 🛠️ Custom Modifications & Bug Fixes

### Async Read-Write Lock Optimization
- **Issue**: Under high-concurrency data collection and device communication, race conditions and lock contention bottlenecks occurred during async state synchronization.
- **Root Cause**: Inefficient synchronization primitives blocking threads or causing potential deadlocks in edge cases during concurrent async reads/writes.
- **Fix**: Refactored thread synchronization using a dual-channel additive async read-write lock (`AsyncReadWriteLock`) pattern.
- **Impact**: Reduced thread blocking, avoided deadlock risks, optimized memory/GC overhead, and improved overall throughput for concurrent industrial IoT data collection.