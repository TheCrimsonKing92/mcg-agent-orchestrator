// These tests redirect process-global Console.Out (Console.SetOut) to capture
// CLI handler output. Console.Out is shared static state, so running test
// collections in parallel lets one test dispose its StringWriter while another
// test's handler is mid-write, throwing ObjectDisposedException ("Cannot write
// to a closed TextWriter"). Disable cross-collection parallelization for this
// assembly so Console capture is serialized and the suite runs deterministically.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
