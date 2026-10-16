# Bounded recognizer residency

Set `PaddleOcrServiceOptions.MaxResidentRecognizers` to a positive count to bound native recognition sessions retained by one service. The default zero retains all successfully loaded packs, as before. This cap includes in-progress loads and active recognition leases. It excludes detectors, classifiers, document/structure models, inference buffers and runtime arenas; it is not a byte or total process-memory limit.

```csharp
using var ocr = new PaddleOcrService(new PaddleOcrServiceOptions
{
    MaxResidentRecognizers = 2
});
```

Least-recently-used idle sessions are disposed before admitting another pack. If all slots are loading or being used, admission waits and observes the caller's cancellation token. Requests for the same pack share one load. Canceling one waiter does not cancel or poison that shared load; genuine load faults are removed so later calls can retry. Inference holds a lease until it returns or throws. Service shutdown drains these leases and pending loads before disposing session configuration.

Warm-up leases and warms each pack separately. A cap smaller than the requested pack count cannot keep all warmed packs resident. Auto routing also releases each candidate before loading the next, so a cap of one works without nesting leases. Small caps can cause substantial download-cache reads, session creation and latency; choose a limit appropriate to the active languages and benchmark the deployment workload. No speed or native-memory reduction measurement is claimed by the count invariant.

Seal recognition receives a borrowing facade rather than holding a native session forever. Each call reacquires its pack; eviction cannot invalidate the facade. Local model/dictionary overrides, recognizer variant selection, filters and calibration validation retain the existing loader paths. Legacy synchronous facade calls wait synchronously for admission and have no cancellation parameter; detailed calls carry cancellation through admission and inference boundaries.
