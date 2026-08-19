// Run test collections one after another. Many tests here start real scheduler hosts with sub-second lock TTLs
// and assert on timing (keepalive cadence, the lease rule, "stopped before the lease lapsed"); running them
// alongside the two-host/SQLite suites on a 2-core CI runner starved the timers badly enough to fail genuine
// behaviour with late wake-ups. Serial execution costs a minute and removes that class of flake.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
