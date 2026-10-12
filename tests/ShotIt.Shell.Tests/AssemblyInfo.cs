// The tests share one scratch registry key that is removed when its last child goes away,
// so they must not run side by side.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
